// SPDX-License-Identifier: MIT
// Turns input-ring events (splitplay/input_ring.h) into RDP input PDUs (MS-RDPBCGR 2.2.8.1.1.3 / 2.2.8.1.2.2) and
// keeps the state RDP needs: which keys and buttons are down (key repeat flag, release-all), the last pointer
// position (button and wheel PDUs carry one) and whether the pointer was last moved relatively.
//
// Pure logic without FreeRDP, so it is unit-tested on every platform; FreeRdpConnector sends the operations. The
// flag values are MS-RDPBCGR's (FreeRDP's freerdp/input.h names them the same; freerdp_connector.cpp checks them).
#pragma once

#include <bitset>
#include <cstdint>
#include <vector>

#include "splitplay/input_ring.h"

namespace splitplay::seat::rdp {

// TS_KEYBOARD_EVENT keyboardFlags
inline constexpr uint16_t kKbdExtended = 0x0100;
inline constexpr uint16_t kKbdExtended1 = 0x0200;
inline constexpr uint16_t kKbdDown = 0x4000;  // "the key was down before" (repeat)
inline constexpr uint16_t kKbdRelease = 0x8000;
// TS_POINTER_EVENT pointerFlags
inline constexpr uint16_t kPtrHWheel = 0x0400;
inline constexpr uint16_t kPtrWheel = 0x0200;
inline constexpr uint16_t kPtrWheelNegative = 0x0100;
inline constexpr uint16_t kPtrWheelMask = 0x01FF;
inline constexpr uint16_t kPtrMove = 0x0800;
inline constexpr uint16_t kPtrDown = 0x8000;
inline constexpr uint16_t kPtrButton1 = 0x1000;  // left
inline constexpr uint16_t kPtrButton2 = 0x2000;  // right
inline constexpr uint16_t kPtrButton3 = 0x4000;  // middle
// TS_POINTERX_EVENT pointerFlags (also valid in TS_FP_RELPOINTER_EVENT)
inline constexpr uint16_t kPtrXDown = 0x8000;
inline constexpr uint16_t kPtrXButton1 = 0x0001;
inline constexpr uint16_t kPtrXButton2 = 0x0002;

// Scancodes (set 1) of the Ctrl+Alt+Del sequence.
inline constexpr uint8_t kScanLeftCtrl = 0x1D;
inline constexpr uint8_t kScanLeftAlt = 0x38;
inline constexpr uint8_t kScanDelete = 0x53;  // with E0

struct Op {
  enum class Kind : uint8_t {
    keyboard,  // flags (kKbd*), code = scancode
    unicode,   // flags (0 or kKbdRelease), code = UTF-16 code unit
    pause,     // the Pause key's E1 sequence, press and release (FreeRDP sends it as one call)
    mouse,     // flags (kPtr*), x, y
    xmouse,    // extended mouse: flags (kPtrX*), x, y
    relmouse,  // relative mouse: flags (kPtrMove, kPtrDown, kPtrButton*, kPtrXButton*), x = dx, y = dy
  };
  Kind kind;
  uint16_t flags = 0;
  uint16_t code = 0;
  int32_t x = 0;
  int32_t y = 0;

  bool operator==(const Op&) const = default;
};

// What the RDP server announced in its input capability set.
struct ServerInputCaps {
  bool relative_mouse = false;   // INPUT_FLAG_MOUSE_RELATIVE
  bool horizontal_wheel = false; // TS_INPUT_FLAG_MOUSE_HWHEEL
  bool extended_mouse = false;   // INPUT_FLAG_MOUSEX (X1/X2 buttons)
  bool unicode = true;           // INPUT_FLAG_UNICODE
};

class InputMapper {
 public:
  // A new connection: the server's keyboard and mouse state starts clean, so ours does too.
  void reset(const ServerInputCaps& caps);

  // Appends the PDUs for `event` to `out`. Returns false (and appends nothing) for an event RDP cannot carry on this
  // connection (relative motion without server support, X buttons or the horizontal wheel without their caps, bad
  // values); the caller counts those.
  bool map(const SpInputEvent& event, std::vector<Op>& out);

  // Ctrl+Alt+Del as scancodes: the RDP server turns the sequence into the secure attention sequence.
  void sas(std::vector<Op>& out) const;

  // Releases every key and button that is down (client gone, sink switched).
  void release_all(std::vector<Op>& out);

  const ServerInputCaps& caps() const noexcept { return caps_; }
  size_t keys_down() const noexcept { return keys_.count(); }
  uint8_t buttons_down() const noexcept { return buttons_; }
  int32_t x() const noexcept { return x_; }
  int32_t y() const noexcept { return y_; }

 private:
  bool map_key(uint32_t scancode, uint32_t flags, std::vector<Op>& out);
  bool map_button(const SpInputEvent& event, std::vector<Op>& out);
  bool map_wheel(const SpInputEvent& event, bool horizontal, std::vector<Op>& out);
  void take_position(const SpInputEvent& event);

  ServerInputCaps caps_;
  std::bitset<512> keys_;  // scancode | 0x100 for E0
  uint8_t buttons_ = 0;    // bit (button - 1)
  int32_t x_ = 0;
  int32_t y_ = 0;
  bool relative_ = false;  // the pointer was last moved with relative PDUs
};

}  // namespace splitplay::seat::rdp
