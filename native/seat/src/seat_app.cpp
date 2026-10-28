// SPDX-License-Identifier: MIT
#include "seat_app.hpp"

#include "splitplay/common/input_ring.hpp"
#include "splitplay/common/log.hpp"
#include "splitplay/common/pipe_transport.hpp"
#include "splitplay/common/qpc.hpp"
#include "splitplay/common/shared_atomic.hpp"
#include "splitplay/common/version.hpp"

namespace splitplay::seat {

SeatApp::SeatApp(SeatOptions options, std::unique_ptr<IRdpConnector> connector)
    : options_(std::move(options)), connector_(std::move(connector)), tracer_(options_.desk) {
  stop_event_.reset(CreateEventW(nullptr, TRUE, FALSE, nullptr));
  params_.desk = options_.desk;
}

SeatApp::~SeatApp() {
  SetEvent(stop_event_.get());
  if (input_thread_.joinable()) input_thread_.join();
  log_set_sink({});
  if (client_) client_->stop();
}

ipc::Result SeatApp::on_profile(const ipc::json& profile, bool initial) {
  if (auto w = ipc::get_int(profile, "width"); w && *w > 0) params_.width = static_cast<uint32_t>(*w);
  if (auto h = ipc::get_int(profile, "height"); h && *h > 0) params_.height = static_cast<uint32_t>(*h);
  if (!initial) log_info("profile updated; the desktop size applies on the next RDP connection");
  return ipc::Result::success();
}

std::optional<ipc::Result> SeatApp::on_request(const ipc::Message& request) {
  if (request.type == "send_sas") {
    std::string err;
    if (connector_->send_sas(&err)) return ipc::Result::success();
    return ipc::Result::unsupported(err);
  }
  return std::nullopt;  // worker-only requests (pair, list_clients, unpair): unsupported
}

ipc::HeartbeatInfo SeatApp::heartbeat() {
  ipc::HeartbeatInfo hb;
  hb.state = std::string(to_string(connector_->state()));
  hb.metrics = {{"input_events", input_events_.load()}, {"input_seq_gaps", input_seq_gaps_.load()}};
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

int SeatApp::run() {
  log_info("splitplay-seat {} starting for desk {} (pipe {})", kVersion, options_.desk, options_.pipe);

  std::string err;
  auto transport = ipc::PipeTransport::connect(options_.pipe, options_.connect_timeout, &err);
  if (!transport) {
    log_error("cannot connect to the service: {}", err);
    return kExitConnectFailed;
  }

  ipc::Client::Handlers handlers;
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

  connector_->set_state_callback([](RdpState state, std::string_view detail) {
    log_info("RDP state: {} ({})", to_string(state), detail);
  });
  if (!connector_->connect(params_, &err)) log_error("RDP connection failed: {}", err);

  ipc::json ready = {{"input_ring", input_ring_->section->name()}, {"connector", connector_->name()}};
  if (auto sid = connector_->session_id()) ready["session"] = *sid;
  client_->send_ready(ready);
  log_info("seat ready (connector {}, state {})", connector_->name(), to_string(connector_->state()));

  const ipc::ExitReason reason = client_->wait();
  log_set_sink({});
  SetEvent(stop_event_.get());
  if (input_thread_.joinable()) input_thread_.join();
  connector_->disconnect();
  client_->stop();
  log_info("seat exiting: {}", ipc::to_string(reason));
  return reason == ipc::ExitReason::shutdown ? kExitOk : kExitPipeBroken;
}

}  // namespace splitplay::seat
