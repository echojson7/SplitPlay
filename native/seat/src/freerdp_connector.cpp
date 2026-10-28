// SPDX-License-Identifier: MIT
#include "freerdp_connector.hpp"

#include <freerdp/channels/disp.h>
#include <freerdp/channels/rdpsnd.h>
#include <freerdp/client.h>
#include <freerdp/client/channels.h>
#include <freerdp/client/cmdline.h>
#include <freerdp/client/disp.h>
#include <freerdp/error.h>
#include <freerdp/event.h>
#include <freerdp/freerdp.h>
#include <freerdp/gdi/gdi.h>
#include <freerdp/settings.h>
#include <winpr/wlog.h>

#include <algorithm>
#include <cstring>
#include <format>

#include "splitplay/common/input_ring.hpp"
#include "splitplay/common/ipc_codec.hpp"
#include "splitplay/common/log.hpp"
#include "wts_session.hpp"

namespace splitplay::seat {

namespace {

using namespace std::chrono_literals;

// rdpClientContext must be the first member (FreeRDP casts between them).
struct SeatContext {
  rdpClientContext common;
  FreeRdpConnector* self;
};

FreeRdpConnector* self_of(rdpContext* context) {
  return context ? reinterpret_cast<SeatContext*>(context)->self : nullptr;
}

BOOL cb_pre_connect(freerdp* instance) {
  rdpSettings* settings = instance->context->settings;
  // Windows client identifiers (for the server's logs and WTS client info).
  if (!freerdp_settings_set_uint32(settings, FreeRDP_OsMajorType, OSMAJORTYPE_WINDOWS)) return FALSE;
  if (!freerdp_settings_set_uint32(settings, FreeRDP_OsMinorType, OSMINORTYPE_WINDOWS_NT)) return FALSE;
  return TRUE;
}

BOOL cb_desktop_resize(rdpContext* context) {
  rdpSettings* settings = context->settings;
  const UINT32 w = freerdp_settings_get_uint32(settings, FreeRDP_DesktopWidth);
  const UINT32 h = freerdp_settings_get_uint32(settings, FreeRDP_DesktopHeight);
  log_info("RDP: desktop resized to {}x{}", w, h);
  return gdi_resize(context->gdi, w, h);
}

BOOL cb_play_sound(rdpContext*, const PLAY_SOUND_UPDATE*) { return TRUE; }
BOOL cb_keyboard_indicators(rdpContext*, UINT16) { return TRUE; }
BOOL cb_keyboard_ime(rdpContext*, UINT16, UINT32, UINT32) { return TRUE; }

BOOL cb_post_connect(freerdp* instance) {
  // The framebuffer is never looked at (the worker captures inside the session), but the library expects a GDI.
  if (!gdi_init(instance, PIXEL_FORMAT_BGRX32)) return FALSE;
  rdpContext* context = instance->context;
  if (!freerdp_settings_set_bool(context->settings, FreeRDP_DeactivateClientDecoding, TRUE)) return FALSE;
  context->update->DesktopResize = cb_desktop_resize;
  context->update->PlaySound = cb_play_sound;
  context->update->SetKeyboardIndicators = cb_keyboard_indicators;
  context->update->SetKeyboardImeStatus = cb_keyboard_ime;
  return TRUE;
}

void cb_post_disconnect(freerdp* instance) { gdi_free(instance); }

// Never prompt: the credentials come from the service. Returning FALSE cancels the connection attempt.
BOOL cb_authenticate(freerdp*, char**, char**, char**, rdp_auth_reason) { return FALSE; }

// Loopback connection to this machine's own RDP listener: accept its (self-signed) certificate for this session.
DWORD cb_verify_certificate(freerdp*, const char*, UINT16, const char*, const char*, const char*, const char*, DWORD) {
  return 2;
}
DWORD cb_verify_changed_certificate(freerdp*, const char*, UINT16, const char*, const char*, const char*, const char*,
                                    const char*, const char*, const char*, DWORD) {
  return 2;
}

int cb_logon_error_info(freerdp*, UINT32 data, UINT32 type) {
  log_info("RDP: logon info {} [{}]", freerdp_get_logon_error_info_data(data), freerdp_get_logon_error_info_type(type));
  return 1;
}

UINT cb_disp_caps(DispClientContext* disp, UINT32 max_monitors, UINT32 factor_a, UINT32 factor_b) {
  if (auto* self = static_cast<FreeRdpConnector*>(disp->custom)) self->on_disp_caps(max_monitors, factor_a, factor_b);
  return CHANNEL_RC_OK;
}

void on_channel_connected(void* context, const ChannelConnectedEventArgs* e) {
  if (std::strcmp(e->name, DISP_DVC_CHANNEL_NAME) == 0) {
    if (auto* self = self_of(static_cast<rdpContext*>(context)))
      self->on_disp_connected(static_cast<DispClientContext*>(e->pInterface));
    return;
  }
  freerdp_client_OnChannelConnectedEventHandler(context, e);
}

void on_channel_disconnected(void* context, const ChannelDisconnectedEventArgs* e) {
  if (std::strcmp(e->name, DISP_DVC_CHANNEL_NAME) == 0) {
    if (auto* self = self_of(static_cast<rdpContext*>(context)))
      self->on_disp_disconnected(static_cast<DispClientContext*>(e->pInterface));
    return;
  }
  freerdp_client_OnChannelDisconnectedEventHandler(context, e);
}

BOOL cb_client_new(freerdp* instance, rdpContext*) {
  instance->PreConnect = cb_pre_connect;
  instance->PostConnect = cb_post_connect;
  instance->PostDisconnect = cb_post_disconnect;
  instance->AuthenticateEx = cb_authenticate;
  instance->VerifyCertificateEx = cb_verify_certificate;
  instance->VerifyChangedCertificateEx = cb_verify_changed_certificate;
  instance->LogonErrorInfo = cb_logon_error_info;
  return TRUE;
}

void cb_client_free(freerdp*, rdpContext*) {}

int client_entry(RDP_CLIENT_ENTRY_POINTS* ep) {
  std::memset(ep, 0, sizeof(*ep));
  ep->Version = RDP_CLIENT_INTERFACE_VERSION;
  ep->Size = sizeof(RDP_CLIENT_ENTRY_POINTS_V1);
  ep->ContextSize = sizeof(SeatContext);
  ep->ClientNew = cb_client_new;
  ep->ClientFree = cb_client_free;
  return 0;
}

bool set_str(rdpSettings* s, FreeRDP_Settings_Keys_String key, const std::string& value) {
  return freerdp_settings_set_string(s, key, value.c_str());
}

// Wipes and clears the password FreeRDP holds in its settings (best effort: FreeRDP also keeps an internal copy of
// the settings for reconnection, which it frees on disconnect).
void wipe_settings_password(rdpSettings* s) {
  if (!s) return;
  if (char* p = freerdp_settings_get_string_writable(s, FreeRDP_Password)) ipc::secure_wipe(p, std::strlen(p));
  (void)freerdp_settings_set_string(s, FreeRDP_Password, nullptr);
}

bool is_credential_error(UINT32 code) {
  switch (code) {
    case FREERDP_ERROR_AUTHENTICATION_FAILED:
    case FREERDP_ERROR_CONNECT_LOGON_FAILURE:
    case FREERDP_ERROR_CONNECT_WRONG_PASSWORD:
    case FREERDP_ERROR_CONNECT_ACCESS_DENIED:
    case FREERDP_ERROR_CONNECT_ACCOUNT_RESTRICTION:
    case FREERDP_ERROR_CONNECT_ACCOUNT_LOCKED_OUT:
    case FREERDP_ERROR_CONNECT_ACCOUNT_EXPIRED:
    case FREERDP_ERROR_CONNECT_ACCOUNT_DISABLED:
    case FREERDP_ERROR_CONNECT_PASSWORD_EXPIRED:
    case FREERDP_ERROR_CONNECT_PASSWORD_CERTAINLY_EXPIRED:
    case FREERDP_ERROR_CONNECT_PASSWORD_MUST_CHANGE:
    case FREERDP_ERROR_CONNECT_LOGON_TYPE_NOT_GRANTED:
    case FREERDP_ERROR_CONNECT_NO_OR_MISSING_CREDENTIALS:
    case FREERDP_ERROR_CONNECT_CLIENT_REVOKED:
      return true;
    default:
      return false;
  }
}

std::chrono::milliseconds backoff(int failures) {
  static constexpr int kDelaysMs[] = {1000, 2000, 4000, 8000};
  if (failures <= 0) return 0ms;
  const size_t i = static_cast<size_t>(failures - 1);
  return std::chrono::milliseconds(i < std::size(kDelaysMs) ? kDelaysMs[i] : 10000);
}

}  // namespace

FreeRdpConnector::FreeRdpConnector() : FreeRdpConnector(Options{}) {}

FreeRdpConnector::FreeRdpConnector(Options options) : options_(options) {
  wake_.reset(CreateEventW(nullptr, FALSE, FALSE, nullptr));
  stop_event_.reset(CreateEventW(nullptr, TRUE, FALSE, nullptr));
  // libfreerdp uses Winsock directly but leaves WSAStartup to the application (getaddrinfo fails without it).
  WSADATA wsa{};
  winsock_ = WSAStartup(MAKEWORD(2, 2), &wsa) == 0;
  if (!winsock_) log_error("RDP: WSAStartup failed");
}

FreeRdpConnector::~FreeRdpConnector() {
  disconnect();
  ipc::secure_wipe(params_.password);
  if (winsock_) WSACleanup();
}

bool FreeRdpConnector::connect(RdpConnectParams& params, std::string* error) {
  if (thread_.joinable()) {
    ipc::secure_wipe(params.password);
    if (error) *error = "already connected";
    return false;
  }
  if (params.username.empty() || params.password.empty() || params.host.empty() || params.port == 0) {
    ipc::secure_wipe(params.password);
    if (error) *error = "rdp.host, rdp.port, rdp.user and rdp.password are required";
    return false;
  }
  params_ = params;
  ipc::secure_wipe(params.password);
  params_.width = std::clamp<uint32_t>(params_.width & ~1u, DISPLAY_CONTROL_MIN_MONITOR_WIDTH,
                                       DISPLAY_CONTROL_MAX_MONITOR_WIDTH);
  params_.height = std::clamp<uint32_t>(params_.height, DISPLAY_CONTROL_MIN_MONITOR_HEIGHT,
                                        DISPLAY_CONTROL_MAX_MONITOR_HEIGHT);
  if (params_.domain.empty() || params_.domain == ".") {
    // "." means this computer; NTLM wants the real name.
    wchar_t name[MAX_COMPUTERNAME_LENGTH + 1];
    DWORD n = MAX_COMPUTERNAME_LENGTH + 1;
    if (GetComputerNameW(name, &n)) params_.domain = win::narrow(std::wstring_view(name, n));
  }
  user_w_ = win::widen(params_.username);
  // Unique per seat process (at most 15 characters): picks our session when the user has more than one.
  client_name_ = std::format("SPSEAT-{:X}", GetCurrentProcessId());
  client_name_w_ = win::widen(client_name_);

  WLog_SetLogLevel(WLog_GetRoot(), options_.verbose ? WLOG_INFO : WLOG_WARN);

  log_info("RDP: connecting to {}:{} as {}\\{} ({}x{}, client name {})", params_.host, params_.port, params_.domain,
           params_.username, params_.width, params_.height, client_name_);
  stop_ = false;
  no_reconnect_ = false;
  ResetEvent(stop_event_.get());
  set_state(RdpState::connecting, "connecting");
  thread_ = std::thread([this] { thread_main(); });
  return true;
}

std::optional<uint32_t> FreeRdpConnector::session_id() const {
  const int64_t s = session_.load();
  if (s < 0) return std::nullopt;
  return static_cast<uint32_t>(s);
}

void FreeRdpConnector::set_state_callback(StateCallback callback) {
  std::lock_guard lock(mutex_);
  callback_ = std::move(callback);
}

void FreeRdpConnector::set_state(RdpState state, std::string_view detail) {
  state_.store(state);
  StateCallback cb;
  {
    std::lock_guard lock(mutex_);
    cb = callback_;
  }
  if (cb) cb(state, detail);
}

bool FreeRdpConnector::create_context(std::string* reason) {
  RDP_CLIENT_ENTRY_POINTS ep;
  client_entry(&ep);
  rdpContext* ctx = freerdp_client_context_new(&ep);
  if (!ctx) {
    *reason = "freerdp_client_context_new failed";
    return false;
  }
  reinterpret_cast<SeatContext*>(ctx)->self = this;
  PubSub_SubscribeChannelConnected(ctx->pubSub, on_channel_connected);
  PubSub_SubscribeChannelDisconnected(ctx->pubSub, on_channel_disconnected);
  {
    std::lock_guard lock(context_mutex_);
    context_ = ctx;
  }
  if (!apply_settings(reason)) return false;
  if (freerdp_client_start(ctx) != 0) {
    *reason = "freerdp_client_start failed";
    return false;
  }
  return true;
}

void FreeRdpConnector::free_context() {
  rdpContext* ctx;
  {
    std::lock_guard lock(context_mutex_);
    ctx = context_;
    context_ = nullptr;
  }
  if (!ctx) return;
  freerdp_disconnect(ctx->instance);
  freerdp_client_stop(ctx);
  PubSub_UnsubscribeChannelConnected(ctx->pubSub, on_channel_connected);
  PubSub_UnsubscribeChannelDisconnected(ctx->pubSub, on_channel_disconnected);
  wipe_settings_password(ctx->settings);
  freerdp_client_context_free(ctx);
  std::lock_guard lock(mutex_);
  disp_ = nullptr;
  disp_caps_ = false;
}

bool FreeRdpConnector::apply_settings(std::string* reason) {
  rdpSettings* s = context_->settings;
  bool ok = true;
  auto b = [&](FreeRDP_Settings_Keys_Bool k, bool v) { ok = ok && freerdp_settings_set_bool(s, k, v ? TRUE : FALSE); };
  auto u = [&](FreeRDP_Settings_Keys_UInt32 k, uint32_t v) { ok = ok && freerdp_settings_set_uint32(s, k, v); };

  ok = ok && set_str(s, FreeRDP_ServerHostname, params_.host);
  u(FreeRDP_ServerPort, params_.port);
  ok = ok && set_str(s, FreeRDP_Username, params_.username);
  ok = ok && set_str(s, FreeRDP_Domain, params_.domain);
  ok = ok && set_str(s, FreeRDP_Password, params_.password);
  ok = ok && set_str(s, FreeRDP_ClientHostname, client_name_);
  u(FreeRDP_DesktopWidth, params_.width);
  u(FreeRDP_DesktopHeight, params_.height);
  u(FreeRDP_ColorDepth, 32);

  // Security: NLA only. The listener is this machine (loopback), so its self-signed certificate is accepted.
  b(FreeRDP_NlaSecurity, true);
  b(FreeRDP_TlsSecurity, false);
  b(FreeRDP_RdpSecurity, false);
  b(FreeRDP_ExtSecurity, false);
  b(FreeRDP_IgnoreCertificate, true);
  b(FreeRDP_AutoAcceptCertificate, true);
  b(FreeRDP_UseCommonStdioCallbacks, false);
  b(FreeRDP_CredentialsFromStdin, false);

  // Full desktop experience inside the session (the user sees it through Moonlight, not through this client).
  ok = ok && freerdp_set_connection_type(s, CONNECTION_TYPE_LAN);
  b(FreeRDP_DisableWallpaper, false);
  b(FreeRDP_DisableFullWindowDrag, false);
  b(FreeRDP_DisableMenuAnims, false);
  b(FreeRDP_DisableThemes, false);
  b(FreeRDP_AllowFontSmoothing, true);
  b(FreeRDP_AllowDesktopComposition, true);

  // Graphics: legacy path, nothing decoded.
  b(FreeRDP_SupportGraphicsPipeline, false);
  b(FreeRDP_RemoteFxCodec, false);

  // Audio playback with the null ("fake") backend: the session gets a Remote Audio endpoint, samples are dropped.
  b(FreeRDP_AudioPlayback, true);  // = /audio-mode:redirect
  b(FreeRDP_RemoteConsoleAudio, false);
  b(FreeRDP_AudioCapture, false);
  {
    const char* const sound[] = {RDPSND_CHANNEL_NAME, "sys:fake"};
    ok = ok && freerdp_client_add_static_channel(s, std::size(sound), sound);
    ok = ok && freerdp_client_add_dynamic_channel(s, std::size(sound), sound);
  }

  // No redirections besides audio (rdpdr is still registered because rdpsnd needs it, but without devices).
  b(FreeRDP_RedirectClipboard, false);
  b(FreeRDP_RedirectDrives, false);
  b(FreeRDP_RedirectHomeDrive, false);
  b(FreeRDP_RedirectPrinters, false);
  b(FreeRDP_RedirectSmartCards, false);
  b(FreeRDP_RedirectSerialPorts, false);
  b(FreeRDP_RedirectParallelPorts, false);
  b(FreeRDP_RedirectWebAuthN, false);
  b(FreeRDP_DeviceRedirection, false);
  b(FreeRDP_MultiTouchInput, false);

  // Resize through the display control channel.
  b(FreeRDP_SupportDisplayControl, true);
  b(FreeRDP_DynamicResolutionUpdate, true);
  b(FreeRDP_SupportDynamicChannels, true);

  // Reconnection is our own retry loop with fresh contexts; FreeRDP's built-in auto-reconnect stays off.
  b(FreeRDP_AutoReconnectionEnabled, false);
  // Headless: no client-side decoding (also set in PostConnect; here so every copy of the settings has it).
  b(FreeRDP_DeactivateClientDecoding, true);
  u(FreeRDP_TcpConnectTimeout, 10000);

  if (!ok) *reason = "cannot apply FreeRDP settings";
  return ok;
}

bool FreeRdpConnector::first_connect(std::string* reason) {
  if (!create_context(reason)) return false;
  if (!freerdp_connect(context_->instance)) {
    *reason = describe_error();
    return false;
  }
  return true;
}

std::string FreeRdpConnector::describe_error() const {
  if (!context_) return "no context";
  const UINT32 last = freerdp_get_last_error(context_);
  const UINT32 info = freerdp_error_info(context_->instance);
  std::string s = std::format("{}: {}", freerdp_get_last_error_name(last), freerdp_get_last_error_string(last));
  if (info != 0) s += std::format("; server: {} ({})", freerdp_get_error_info_name(info), freerdp_get_error_info_string(info));
  return s;
}

bool FreeRdpConnector::credentials_rejected() const {
  return context_ && is_credential_error(freerdp_get_last_error(context_));
}

void FreeRdpConnector::thread_main() {
  int failures = 0;
  int credential_failures = 0;
  bool ever_logged_on = false;
  std::string reason;
  while (!should_stop()) {
    // Every attempt uses a fresh context: a new NLA logon of the same user reattaches the disconnected session
    // (fSingleSessionPerUser), and freerdp_reconnect on a used context loses its codecs (gdi_bitmap_update fails).
    free_context();
    const bool connected = first_connect(&reason);
    if (should_stop()) break;

    if (connected) {
      bool reached_logon = false;
      reason = run_session(&reached_logon);
      if (reached_logon) {
        ever_logged_on = true;
        failures = 0;
        credential_failures = 0;
      }
      if (should_stop()) break;
      log_warn("RDP: connection lost: {}", reason);
    } else {
      log_warn("RDP: connection attempt failed: {}", reason);
      if (credentials_rejected()) ++credential_failures;
    }
    set_state(ever_logged_on ? RdpState::disconnected : RdpState::connecting, reason);

    ++failures;
    if (failures >= options_.max_attempts || credential_failures >= 2) {
      log_error("RDP: giving up after {} attempts: {}", failures, reason);
      set_state(RdpState::failed, reason);
      break;
    }
    const auto delay = backoff(failures);
    log_info("RDP: retry {}/{} in {} ms", failures + 1, options_.max_attempts, delay.count());
    if (WaitForSingleObject(stop_event_.get(), static_cast<DWORD>(delay.count())) == WAIT_OBJECT_0) break;
  }
  free_context();
  {
    std::lock_guard lock(mutex_);
    connection_open_ = false;
  }
  cv_.notify_all();
}

std::string FreeRdpConnector::run_session(bool* reached_logon) {
  {
    std::lock_guard lock(mutex_);
    connection_open_ = true;
  }
  const auto connected_at = std::chrono::steady_clock::now();
  auto last_poll = connected_at - 1s;
  bool logged_on = false;
  std::string reason;
  freerdp* instance = context_->instance;

  for (;;) {
    HANDLE handles[MAXIMUM_WAIT_OBJECTS];
    DWORD n = freerdp_get_event_handles(context_, handles, MAXIMUM_WAIT_OBJECTS - 1);
    if (n == 0) {
      reason = "freerdp_get_event_handles failed";
      break;
    }
    handles[n++] = wake_.get();
    const DWORD rc = WaitForMultipleObjects(n, handles, FALSE, 250);
    if (should_stop()) {
      reason = "stopped";
      // A logoff ends the connection from the server side; keep pumping until it does.
      if (stop_.load()) break;
    }
    if (rc == WAIT_FAILED) {
      reason = "WaitForMultipleObjects: " + win::last_error_message();
      break;
    }
    if (!freerdp_check_event_handles(context_) || freerdp_shall_disconnect_context(context_)) {
      reason = describe_error();
      break;
    }
    process_mode_request();

    const auto now = std::chrono::steady_clock::now();
    if (!logged_on && now - last_poll >= 500ms) {
      last_poll = now;
      if (auto sid = wts::find_active_session(user_w_, client_name_w_)) {
        logged_on = true;
        *reached_logon = true;
        session_.store(*sid);
        // NLA and the logon are done: this context no longer needs its copy (a retry builds a fresh context).
        wipe_settings_password(context_->settings);
        log_info("RDP: user {} logged on in session {}", params_.username, *sid);
        set_state(RdpState::logged_on, std::format("session {}", *sid));
      } else if (now - connected_at > options_.logon_timeout) {
        reason = std::format("no active session for {} within {} s", params_.username,
                             std::chrono::duration_cast<std::chrono::seconds>(options_.logon_timeout).count());
        break;
      }
    }
  }
  (void)instance;
  {
    std::lock_guard lock(mutex_);
    connection_open_ = false;
    disp_ = nullptr;
    disp_caps_ = false;
  }
  cv_.notify_all();
  return reason;
}

void FreeRdpConnector::on_disp_connected(DispClientContext* disp) {
  disp->custom = this;
  disp->DisplayControlCaps = cb_disp_caps;
  std::lock_guard lock(mutex_);
  disp_ = disp;
  disp_caps_ = false;
  log_debug("RDP: display control channel connected");
}

void FreeRdpConnector::on_disp_disconnected(DispClientContext* disp) {
  std::lock_guard lock(mutex_);
  if (disp_ == disp) {
    disp_ = nullptr;
    disp_caps_ = false;
  }
}

void FreeRdpConnector::on_disp_caps(uint32_t max_monitors, uint32_t factor_a, uint32_t factor_b) {
  {
    std::lock_guard lock(mutex_);
    disp_caps_ = true;
    disp_max_monitors_ = max_monitors;
  }
  cv_.notify_all();
  log_info("RDP: display control ready (max monitors {}, area factors {}x{})", max_monitors, factor_a, factor_b);
}

void FreeRdpConnector::process_mode_request() {
  std::optional<std::pair<uint32_t, uint32_t>> mode;
  DispClientContext* disp;
  uint64_t seq;
  {
    std::lock_guard lock(mutex_);
    if (!pending_mode_) return;
    mode = pending_mode_;
    pending_mode_.reset();
    disp = disp_;
    seq = mode_seq_;
  }
  bool ok = false;
  std::string error;
  if (!disp || !disp->SendMonitorLayout) {
    error = "the display control channel is not available";
  } else {
    DISPLAY_CONTROL_MONITOR_LAYOUT layout{};
    layout.Flags = DISPLAY_CONTROL_MONITOR_PRIMARY;
    layout.Left = 0;
    layout.Top = 0;
    layout.Width = mode->first;
    layout.Height = mode->second;
    layout.Orientation = ORIENTATION_LANDSCAPE;
    layout.DesktopScaleFactor = 100;
    layout.DeviceScaleFactor = 100;
    // Physical size of a 24" 16:9 panel; only used for DPI hints.
    layout.PhysicalWidth = 531;
    layout.PhysicalHeight = 299;
    const UINT rc = disp->SendMonitorLayout(disp, 1, &layout);
    ok = rc == CHANNEL_RC_OK;
    if (!ok) error = std::format("SendMonitorLayout failed (0x{:08X})", rc);
  }
  {
    std::lock_guard lock(mutex_);
    mode_ok_ = ok;
    mode_error_ = error;
    mode_done_seq_ = seq;
  }
  cv_.notify_all();
}

bool FreeRdpConnector::set_mode(uint32_t width, uint32_t height, std::string* error) {
  if (state_.load() != RdpState::logged_on) {
    if (error) *error = "no RDP session is logged on";
    return false;
  }
  const uint32_t w = std::clamp<uint32_t>(width & ~1u, DISPLAY_CONTROL_MIN_MONITOR_WIDTH,
                                          DISPLAY_CONTROL_MAX_MONITOR_WIDTH);
  const uint32_t h = std::clamp<uint32_t>(height, DISPLAY_CONTROL_MIN_MONITOR_HEIGHT,
                                          DISPLAY_CONTROL_MAX_MONITOR_HEIGHT);
  std::unique_lock lock(mutex_);
  // The server announces the channel's capabilities shortly after logon.
  if (!cv_.wait_for(lock, 3s, [&] { return disp_caps_; })) {
    if (error) *error = disp_ ? "the display control channel did not announce its capabilities"
                              : "the server did not open the display control channel";
    return false;
  }
  const uint64_t seq = ++mode_seq_;
  pending_mode_ = std::make_pair(w, h);
  SetEvent(wake_.get());
  if (!cv_.wait_for(lock, 2s, [&] { return mode_done_seq_ >= seq; })) {
    if (error) *error = "timed out waiting for the RDP thread";
    return false;
  }
  if (!mode_ok_) {
    if (error) *error = mode_error_;
    return false;
  }
  log_info("RDP: requested desktop size {}x{}", w, h);
  return true;
}

void FreeRdpConnector::logoff(std::chrono::milliseconds timeout) {
  const auto sid = session_id();
  if (!sid || !thread_.joinable()) return;
  no_reconnect_ = true;
  std::string err;
  if (!wts::logoff(*sid, &err)) {
    log_warn("RDP: logoff of session {} failed: {}", *sid, err);
    return;
  }
  log_info("RDP: logging off session {}", *sid);
  std::unique_lock lock(mutex_);
  if (!cv_.wait_for(lock, timeout, [&] { return !connection_open_; }))
    log_warn("RDP: the server did not end the connection within {} ms of the logoff", timeout.count());
}

void FreeRdpConnector::disconnect() {
  stop_ = true;
  SetEvent(stop_event_.get());
  SetEvent(wake_.get());
  {
    std::lock_guard lock(context_mutex_);
    if (context_) freerdp_abort_connect_context(context_);
  }
  if (thread_.joinable()) {
    thread_.join();
    set_state(RdpState::idle, "disconnected");
  }
}

bool FreeRdpConnector::inject(const SpInputEvent& event) {
  // Stage 3 keeps the input ring consumer as is: input reaches the desk through the worker, not through RDP.
  static std::atomic<uint64_t> injected{0};
  const uint64_t n = ++injected;
  if (n <= 16 || n % 1000 == 0)
    log_debug("RDP: input #{} {} a={} b={} seq={} (not injected)", n, input::input_type_name(event.type), event.a,
              event.b, event.seq);
  return true;
}

bool FreeRdpConnector::send_sas(std::string* error) {
  if (error) *error = "send_sas is not supported by the FreeRDP connector yet";
  return false;
}

}  // namespace splitplay::seat
