// SPDX-License-Identifier: MIT
// The seat's RDP connection to its desk session. Stage 3 adds FreeRdpConnector (libfreerdp loopback connection with
// NLA, rdpsnd null output and all redirections off); Stage 2 ships NullRdpConnector.
#pragma once

#include <cstdint>
#include <functional>
#include <optional>
#include <string>
#include <string_view>

#include "splitplay/input_ring.h"

namespace splitplay::seat {

struct RdpConnectParams {
  std::string desk;
  std::string host = "127.0.0.2";   // loopback alias so TermService treats it as a remote connection
  uint16_t port = 3389;
  std::string username;
  std::string domain;
  // The password is read from LSA private data by the connector itself (never on the command line or the pipe).
  uint32_t width = 1920;
  uint32_t height = 1080;
  uint32_t desktop_scale_percent = 100;
};

enum class RdpState {
  idle,          // not connected
  connecting,
  logged_on,     // session exists and the user is logged on
  disconnected,  // was connected; the service decides whether to reconnect
  failed,
  stub,          // Stage 2 placeholder: no RDP stack
};

std::string_view to_string(RdpState state) noexcept;

class IRdpConnector {
 public:
  using StateCallback = std::function<void(RdpState state, std::string_view detail)>;

  virtual ~IRdpConnector() = default;

  virtual bool connect(const RdpConnectParams& params, std::string* error) = 0;
  virtual void disconnect() = 0;
  virtual RdpState state() const = 0;
  // Windows session id of the desk session once logged on.
  virtual std::optional<uint32_t> session_id() const = 0;

  // Input forwarded from the worker over the input ring (ARCHITECTURE.md §9 "RDP input sink").
  virtual bool inject(const SpInputEvent& event) = 0;
  // True if the server announced relative mouse support (sets bit0 of SpInputRing::flags).
  virtual bool supports_relative_mouse() const = 0;
  // Ctrl+Alt+Del into the session. Returns false if the connector cannot do it.
  virtual bool send_sas(std::string* error) = 0;

  virtual void set_state_callback(StateCallback callback) = 0;
  virtual std::string_view name() const = 0;
};

}  // namespace splitplay::seat
