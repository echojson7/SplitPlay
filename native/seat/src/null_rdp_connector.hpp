// SPDX-License-Identifier: MIT
// Stage 2 stand-in for the FreeRDP connector: no connection, logs injected input, cannot send SAS.
#pragma once

#include <atomic>
#include <mutex>

#include "rdp_connector.hpp"

namespace splitplay::seat {

class NullRdpConnector final : public IRdpConnector {
 public:
  bool connect(const RdpConnectParams& params, std::string* error) override;
  void disconnect() override;
  RdpState state() const override { return state_.load(); }
  std::optional<uint32_t> session_id() const override { return std::nullopt; }

  bool inject(const SpInputEvent& event) override;
  bool supports_relative_mouse() const override { return false; }
  bool send_sas(std::string* error) override;

  void set_state_callback(StateCallback callback) override;
  std::string_view name() const override { return "null"; }

  uint64_t injected() const noexcept { return injected_.load(); }

 private:
  void set_state(RdpState state, std::string_view detail);

  std::atomic<RdpState> state_{RdpState::idle};
  std::atomic<uint64_t> injected_{0};
  std::mutex callback_mutex_;
  StateCallback callback_;
};

}  // namespace splitplay::seat
