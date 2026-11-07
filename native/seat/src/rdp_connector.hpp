// SPDX-License-Identifier: MIT
// The seat's RDP connection to its desk session: FreeRdpConnector (libfreerdp 3 loopback connection with NLA, rdpsnd
// null output, display control and all redirections off) and NullRdpConnector (no RDP stack, for IPC tests).
#pragma once

#include <chrono>
#include <cstdint>
#include <functional>
#include <optional>
#include <string>
#include <string_view>

#include "splitplay/input_ring.h"

namespace splitplay::seat {

struct RdpConnectParams {
  std::string desk;
  std::string host = "127.0.0.2";  // loopback alias so TermService treats it as a remote connection
  uint16_t port = 3389;
  std::string username;
  std::string domain = ".";  // "." = this computer
  // From `welcome.rdp.password`. Never logged; the connector keeps its own copy and wipes it when it is done.
  std::string password;
  uint32_t width = 1920;
  uint32_t height = 1080;
};

enum class RdpState {
  idle,          // not connected
  connecting,    // first connection attempt in progress
  logged_on,     // session exists, is active and the user is logged on; session_id() is set
  disconnected,  // connection lost; the connector is reconnecting on its own
  failed,        // the connector gave up (the seat exits with kExitRdpGaveUp)
  stub,          // no RDP stack (null connector)
};

std::string_view to_string(RdpState state) noexcept;

// Input-ring events the connector handled since it started (heartbeat metrics).
struct InputStats {
  uint64_t sent = 0;           // events turned into RDP input PDUs
  uint64_t unsupported = 0;    // events this connection cannot carry (e.g. relative motion without server support)
  uint64_t not_connected = 0;  // events that arrived while no connection was up
  uint64_t failed = 0;         // events whose PDU could not be sent
  int32_t keyboard_leds = -1;  // the server's last Set Keyboard Indicators (KBD_SYNC_* bits), -1 = none yet
};

class IRdpConnector {
 public:
  // Called from the connector's thread. `detail` is a human-readable reason (never secret).
  using StateCallback = std::function<void(RdpState state, std::string_view detail)>;

  virtual ~IRdpConnector() = default;

  // Starts connecting in the background and returns. False only for unusable parameters. The connector takes the
  // password out of `params` (the caller's copy is wiped).
  virtual bool connect(RdpConnectParams& params, std::string* error) = 0;
  // Logs the desk session off (WTSLogoffSession) and waits up to `timeout` for the server to end the connection.
  virtual void logoff(std::chrono::milliseconds timeout) = 0;
  // Stops reconnecting and closes the connection. Idempotent.
  virtual void disconnect() = 0;
  virtual RdpState state() const = 0;
  // Windows session id of the desk session once logged on.
  virtual std::optional<uint32_t> session_id() const = 0;

  // Resizes the session through the display-control channel. False with `error` set if it is not available.
  virtual bool set_mode(uint32_t width, uint32_t height, std::string* error) = 0;

  // Input forwarded from the worker over the input ring (ARCHITECTURE.md §9 "RDP input sink"). Called from the
  // seat's input thread. False if the event was not delivered (counted in input_stats()).
  virtual bool inject(const SpInputEvent& event) = 0;
  // True if the current connection's server accepts relative mouse PDUs (SP_INPUT_RING_REL_MOUSE). Known once the
  // connection is up (state logged_on).
  virtual bool supports_relative_mouse() const = 0;
  // SP_INPUT_RING_* capability bits of the current connection (REL_MOUSE, XBUTTONS, HWHEEL).
  virtual uint32_t input_caps() const { return supports_relative_mouse() ? SP_INPUT_RING_REL_MOUSE : 0u; }
  // Ctrl+Alt+Del into the session. Returns false if the connector cannot do it.
  virtual bool send_sas(std::string* error) = 0;
  virtual InputStats input_stats() const { return {}; }

  virtual void set_state_callback(StateCallback callback) = 0;
  virtual std::string_view name() const = 0;
};

}  // namespace splitplay::seat
