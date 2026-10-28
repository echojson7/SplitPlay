// SPDX-License-Identifier: MIT
// FreeRDP 3 connector: a headless loopback RDP client that keeps the desk user's session logged on.
//
// - NLA only, certificate ignored (loopback), credentials from `welcome.rdp`.
// - rdpsnd (static and dynamic) with the "fake" backend, so the session gets a "Remote Audio" endpoint and the
//   samples are discarded; no clipboard, drive, printer, smart-card or port redirection; no microphone.
// - Display control channel for set_mode; legacy graphics (no GFX), client-side decoding switched off.
// - Session id from WTSEnumerateSessions (user name match, WTSActive, client name match preferred).
// - Auto-reconnect with backoff; state callbacks drive session_lost/session_restored; gives up after kMaxAttempts.
#pragma once

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <mutex>
#include <optional>
#include <string>
#include <thread>

#include "rdp_connector.hpp"
#include "splitplay/common/win_util.hpp"

struct rdp_context;
struct s_disp_client_context;

namespace splitplay::seat {

class FreeRdpConnector final : public IRdpConnector {
 public:
  struct Options {
    int max_attempts = 10;                                // consecutive failed attempts before giving up
    std::chrono::milliseconds logon_timeout{180000};      // connected -> WTSActive session for the user
    bool verbose = false;                                 // FreeRDP's own log at INFO instead of WARN
  };

  FreeRdpConnector();
  explicit FreeRdpConnector(Options options);
  ~FreeRdpConnector() override;
  FreeRdpConnector(const FreeRdpConnector&) = delete;
  FreeRdpConnector& operator=(const FreeRdpConnector&) = delete;

  bool connect(RdpConnectParams& params, std::string* error) override;
  void logoff(std::chrono::milliseconds timeout) override;
  void disconnect() override;
  RdpState state() const override { return state_.load(); }
  std::optional<uint32_t> session_id() const override;

  bool set_mode(uint32_t width, uint32_t height, std::string* error) override;
  bool inject(const SpInputEvent& event) override;
  bool supports_relative_mouse() const override { return false; }
  bool send_sas(std::string* error) override;

  void set_state_callback(StateCallback callback) override;
  std::string_view name() const override { return "freerdp"; }

  // FreeRDP callbacks (C linkage trampolines live in the .cpp).
  void on_disp_connected(s_disp_client_context* disp);
  void on_disp_disconnected(s_disp_client_context* disp);
  void on_disp_caps(uint32_t max_monitors, uint32_t factor_a, uint32_t factor_b);

 private:
  void thread_main();
  bool create_context(std::string* reason);
  void free_context();
  bool apply_settings(std::string* reason);
  bool first_connect(std::string* reason);
  bool reconnect(std::string* reason);
  // Runs the event loop until the connection ends or stop is requested. Returns the reason.
  std::string run_session(bool* reached_logon);
  void process_mode_request();
  void set_state(RdpState state, std::string_view detail);
  std::string describe_error() const;
  bool credentials_rejected() const;
  bool should_stop() const { return stop_.load() || no_reconnect_.load(); }

  Options options_;
  RdpConnectParams params_;  // holds the password until the connector is destroyed (needed for reconnects)
  std::wstring user_w_;
  std::string client_name_;
  std::wstring client_name_w_;

  std::mutex context_mutex_;  // guards context_ against disconnect() from other threads
  rdp_context* context_ = nullptr;
  bool context_connected_once_ = false;  // a freerdp_connect on this context succeeded (ARC cookie usable)

  std::thread thread_;
  std::atomic<bool> stop_{false};
  std::atomic<bool> no_reconnect_{false};  // logoff in progress: the next disconnect is final
  win::UniqueHandle wake_;                 // auto-reset; set by set_mode and disconnect
  win::UniqueHandle stop_event_;           // manual-reset; interrupts backoff sleeps

  std::atomic<RdpState> state_{RdpState::idle};
  std::atomic<int64_t> session_{-1};

  mutable std::mutex mutex_;
  std::condition_variable cv_;
  StateCallback callback_;
  bool connection_open_ = false;
  // Display control (channel pointer used on the RDP thread only; flags under mutex_).
  s_disp_client_context* disp_ = nullptr;
  bool disp_caps_ = false;
  uint32_t disp_max_monitors_ = 0;
  uint64_t mode_seq_ = 0;
  uint64_t mode_done_seq_ = 0;
  std::optional<std::pair<uint32_t, uint32_t>> pending_mode_;
  bool mode_ok_ = false;
  std::string mode_error_;
};

}  // namespace splitplay::seat
