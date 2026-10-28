// SPDX-License-Identifier: MIT
// Stand-in for the FreeRDP connector (--connector null): no connection, logs injected input, cannot send SAS or
// resize. Used by the IPC tests and when no RDP stack is wanted.
#pragma once

#include <atomic>
#include <mutex>

#include "rdp_connector.hpp"

namespace splitplay::seat {

class NullRdpConnector final : public IRdpConnector {
 public:
  bool connect(RdpConnectParams& params, std::string* error) override;
  void logoff(std::chrono::milliseconds) override {}
  void disconnect() override;
  RdpState state() const override { return state_.load(); }
  std::optional<uint32_t> session_id() const override { return std::nullopt; }

  bool set_mode(uint32_t width, uint32_t height, std::string* error) override;
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
