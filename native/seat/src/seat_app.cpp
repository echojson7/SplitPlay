// SPDX-License-Identifier: MIT
#include "seat_app.hpp"

#include "splitplay/common/input_ring.hpp"
#include "splitplay/common/log.hpp"
#include "splitplay/common/pipe_transport.hpp"
#include "splitplay/common/qpc.hpp"
#include "splitplay/common/shared_atomic.hpp"
#include "splitplay/common/version.hpp"

namespace splitplay::seat {

using namespace std::chrono_literals;

SeatApp::SeatApp(SeatOptions options, std::unique_ptr<IRdpConnector> connector)
    : options_(std::move(options)), connector_(std::move(connector)), tracer_(options_.desk) {
  stop_event_.reset(CreateEventW(nullptr, TRUE, FALSE, nullptr));
  params_.desk = options_.desk;
}

SeatApp::~SeatApp() {
  SetEvent(stop_event_.get());
  if (input_thread_.joinable()) input_thread_.join();
  log_set_sink({});
  connector_->disconnect();
  if (client_) client_->stop();
  ipc::secure_wipe(params_.password);
}

void SeatApp::on_welcome(ipc::json& welcome) {
  auto it = welcome.find("rdp");
  if (it == welcome.end() || !it->is_object()) return;
  ipc::json& rdp = *it;
  have_rdp_ = true;
  if (auto v = ipc::get_string(rdp, "host"); v && !v->empty()) params_.host = *v;
  if (auto v = ipc::get_int(rdp, "port"); v && *v > 0 && *v < 65536) params_.port = static_cast<uint16_t>(*v);
  if (auto v = ipc::get_string(rdp, "user")) params_.username = *v;
  if (auto v = ipc::get_string(rdp, "domain")) params_.domain = *v;
  if (auto v = ipc::get_int(rdp, "width"); v && *v > 0) params_.width = static_cast<uint32_t>(*v);
  if (auto v = ipc::get_int(rdp, "height"); v && *v > 0) params_.height = static_cast<uint32_t>(*v);
  if (auto pw = rdp.find("password"); pw != rdp.end() && pw->is_string()) {
    std::string& secret = pw->get_ref<std::string&>();
    ipc::secure_wipe(params_.password);
    params_.password = secret;
    ipc::secure_wipe(secret);
  }
  log_info("welcome: RDP {}:{} user {}\\{} {}x{}", params_.host, params_.port, params_.domain, params_.username,
           params_.width, params_.height);
}

ipc::Result SeatApp::on_profile(const ipc::json& profile, bool initial) {
  if (auto w = ipc::get_int(profile, "width"); w && *w > 0) profile_width_ = static_cast<uint32_t>(*w);
  if (auto h = ipc::get_int(profile, "height"); h && *h > 0) profile_height_ = static_cast<uint32_t>(*h);
  if (!initial) log_info("profile updated; the desktop size follows set_mode requests");
  return ipc::Result::success();
}

std::optional<ipc::Result> SeatApp::on_request(const ipc::Message& request) {
  if (request.type == "send_sas") {
    std::string err;
    if (connector_->send_sas(&err)) return ipc::Result::success();
    return ipc::Result::unsupported(err);
  }
  if (request.type == "set_mode") {
    const auto w = ipc::get_int(request.body, "width");
    const auto h = ipc::get_int(request.body, "height");
    if (!w || !h || *w <= 0 || *h <= 0 || *w > 16384 || *h > 16384)
      return ipc::Result::failure(ipc::kErrBadRequest, "set_mode needs positive integer \"width\" and \"height\"");
    std::string err;
    if (!connector_->set_mode(static_cast<uint32_t>(*w), static_cast<uint32_t>(*h), &err))
      return ipc::Result::unsupported(err);
    return ipc::Result::success(ipc::json{{"width", *w}, {"height", *h}});
  }
  return std::nullopt;  // worker-only requests (pair, list_clients, unpair): unsupported
}

ipc::HeartbeatInfo SeatApp::heartbeat() {
  ipc::HeartbeatInfo hb;
  hb.state = std::string(to_string(connector_->state()));
  hb.metrics = {{"input_events", input_events_.load()},
                {"input_seq_gaps", input_seq_gaps_.load()},
                {"rdp_reconnects", reconnects_.load()}};
  if (input_ring_) {
    SpInputRing* r = input_ring_->ring();
    hb.metrics["input_dropped"] = shm::load_relaxed(r->dropped);
    hb.metrics["input_pending"] = shm::load_acquire(r->write_index) - shm::load_acquire(r->read_index);
  }
  if (auto sid = connector_->session_id()) hb.metrics["session"] = *sid;
  return hb;
}

void SeatApp::input_loop() {
  SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
  input::Consumer consumer(input_ring_->ring());
  SpInputEvent events[64];
  uint32_t last_seq = 0;
  HANDLE handles[2] = {stop_event_.get(), input_ring_->input_ready->handle()};
  for (;;) {
    // The timeout also covers a producer that writes without signalling.
    const DWORD rc = WaitForMultipleObjects(2, handles, FALSE, 250);
    if (rc == WAIT_OBJECT_0) return;
    for (;;) {
      const size_t n = consumer.pop_many(events, std::size(events));
      if (n == 0) break;
      for (size_t i = 0; i < n; ++i) {
        const SpInputEvent& e = events[i];
        if (last_seq != 0 && e.seq != last_seq + 1) input_seq_gaps_.fetch_add(1);
        last_seq = e.seq;
        if (e.type == SP_IN_SAS) {
          std::string err;
          if (!connector_->send_sas(&err)) log_warn("SAS from the input ring ignored: {}", err);
        } else {
          connector_->inject(e);
        }
        tracer_.stamp_now(e.seq, SP_STAGE_INPUT_INJECT);
        input_events_.fetch_add(1);
      }
    }
  }
}

int SeatApp::on_state(const StateEvent& event) {
  log_info("RDP state: {} ({})", to_string(event.state), event.detail);
  switch (event.state) {
    case RdpState::logged_on:
    case RdpState::stub: {
      const auto sid = connector_->session_id();
      if (!ready_sent_) {
        ipc::json ready = {{"input_ring", input_ring_->section->name()}, {"connector", connector_->name()}};
        if (sid) ready["session"] = *sid;
        client_->send_ready(ready);
        ready_sent_ = true;
        log_info("seat ready (connector {}, session {})", connector_->name(),
                 sid ? std::to_string(*sid) : std::string("none"));
      } else if (session_lost_) {
        session_lost_ = false;
        ++reconnects_;
        ipc::json data = ipc::json::object();
        if (sid) data["session"] = *sid;
        client_->send_event("session_restored", data);
      }
      return -1;
    }
    case RdpState::disconnected:
      if (ready_sent_ && !session_lost_) {
        session_lost_ = true;
        client_->send_event("session_lost", {{"reason", event.detail}});
      }
      return -1;
    case RdpState::failed:
      log_error("RDP connection given up: {}", event.detail);
      if (ready_sent_ && !session_lost_) client_->send_event("session_lost", {{"reason", event.detail}});
      return kExitRdpGaveUp;
    case RdpState::idle:
    case RdpState::connecting:
      return -1;
  }
  return -1;
}

int SeatApp::run() {
  log_info("splitplay-seat {} starting for desk {} (pipe {})", kVersion, options_.desk, options_.pipe);

  std::string err;
  auto transport = ipc::PipeTransport::connect(options_.pipe, options_.connect_timeout, &err);
  if (!transport) {
    log_error("cannot connect to the service: {}", err);
    return kExitConnectFailed;
  }

  ipc::Client::Handlers handlers;
  handlers.on_welcome = [this](ipc::json& w) { on_welcome(w); };
  handlers.on_profile = [this](const ipc::json& p, bool initial) { return on_profile(p, initial); };
  handlers.on_request = [this](const ipc::Message& m) { return on_request(m); };
  handlers.on_heartbeat = [this] { return heartbeat(); };
  client_ = std::make_unique<ipc::Client>(std::move(transport), std::move(handlers));

  ipc::Hello hello{"seat", options_.desk, GetCurrentProcessId(), kVersion, win::current_session_id()};
  auto profile = client_->handshake(hello, &err);
  if (!profile) {
    log_error("handshake with the service failed: {}", err);
    return kExitConnectFailed;
  }
  log_set_sink([this](LogLevel level, std::string_view message) { client_->send_log(to_string(level), message); });

  const uint32_t ring_flags = connector_->supports_relative_mouse() ? 1u : 0u;
  input_ring_ = shm::InputRingMapping::create(options_.ns, options_.desk, ring_flags, &err);
  if (!input_ring_) {
    log_error("cannot create the input ring: {}", err);
    log_set_sink({});
    return kExitFailure;
  }
  input_thread_ = std::thread([this] { input_loop(); });

  connector_->set_state_callback([this](RdpState state, std::string_view detail) {
    {
      std::lock_guard lock(events_mutex_);
      events_.push_back({state, std::string(detail)});
    }
    events_cv_.notify_all();
  });

  if (connector_->name() != "null" && !have_rdp_) {
    log_error("welcome has no \"rdp\" object; the {} connector needs host, port, user and password", connector_->name());
    log_set_sink({});
    return kExitFailure;
  }
  // rdp.width/height set the desktop size; the profile size is only a fallback.
  if (!have_rdp_ && profile_width_ && profile_height_) {
    params_.width = profile_width_;
    params_.height = profile_height_;
  }
  if (!connector_->connect(params_, &err)) {
    log_error("RDP connection cannot start: {}", err);
    log_set_sink({});
    return kExitRdpGaveUp;
  }

  int exit_code = -1;
  ipc::ExitReason reason = ipc::ExitReason::none;
  while (exit_code < 0) {
    reason = client_->wait(100ms);
    if (reason != ipc::ExitReason::none) break;
    std::deque<StateEvent> batch;
    {
      std::lock_guard lock(events_mutex_);
      batch.swap(events_);
    }
    for (const StateEvent& e : batch) {
      exit_code = on_state(e);
      if (exit_code >= 0) break;
    }
  }

  if (reason == ipc::ExitReason::shutdown) {
    connector_->logoff(options_.logoff_wait);
    exit_code = kExitOk;
  } else if (reason == ipc::ExitReason::disconnected) {
    exit_code = kExitPipeBroken;
  }
  log_set_sink({});
  SetEvent(stop_event_.get());
  if (input_thread_.joinable()) input_thread_.join();
  connector_->disconnect();
  client_->stop();
  log_info("seat exiting with code {} ({})", exit_code, ipc::to_string(reason));
  return exit_code;
}

}  // namespace splitplay::seat
