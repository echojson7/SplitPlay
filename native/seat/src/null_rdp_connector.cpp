// SPDX-License-Identifier: MIT
#include "null_rdp_connector.hpp"

#include "splitplay/common/input_ring.hpp"
#include "splitplay/common/log.hpp"

namespace splitplay::seat {

std::string_view to_string(RdpState state) noexcept {
  switch (state) {
    case RdpState::idle: return "idle";
    case RdpState::connecting: return "connecting";
    case RdpState::logged_on: return "logged_on";
    case RdpState::disconnected: return "disconnected";
    case RdpState::failed: return "failed";
    case RdpState::stub: return "stub";
  }
  return "idle";
}

bool NullRdpConnector::connect(const RdpConnectParams& params, std::string* error) {
  (void)error;
  log_info("null RDP connector: not connecting to {}:{} for desk {} (no RDP stack in Stage 2)", params.host,
           params.port, params.desk);
  set_state(RdpState::stub, "no RDP stack");
  return true;
}

void NullRdpConnector::disconnect() { set_state(RdpState::idle, "disconnect requested"); }

bool NullRdpConnector::inject(const SpInputEvent& event) {
  const uint64_t n = ++injected_;
  if (n <= 16 || n % 1000 == 0) {
    log_info("null RDP connector: input #{} {} a={} b={} seq={}", n, input::input_type_name(event.type), event.a,
             event.b, event.seq);
  }
  return true;
}

bool NullRdpConnector::send_sas(std::string* error) {
  if (error) *error = "send_sas needs the RDP connector (Stage 3); the null connector cannot send it";
  return false;
}

void NullRdpConnector::set_state_callback(StateCallback callback) {
  std::lock_guard lock(callback_mutex_);
  callback_ = std::move(callback);
}

void NullRdpConnector::set_state(RdpState state, std::string_view detail) {
  state_.store(state);
  StateCallback cb;
  {
    std::lock_guard lock(callback_mutex_);
    cb = callback_;
  }
  if (cb) cb(state, detail);
}

}  // namespace splitplay::seat
