// SPDX-License-Identifier: MIT
#include "rdp_input.hpp"

#include <algorithm>

namespace splitplay::seat::rdp {

namespace {

uint16_t coord(int32_t v) { return static_cast<uint16_t>(std::clamp<int32_t>(v, 0, 0xFFFF)); }

int16_t delta16(int32_t v) { return static_cast<int16_t>(std::clamp<int32_t>(v, INT16_MIN, INT16_MAX)); }

// Button number (input_ring.h: 1 L, 2 R, 3 M, 4 X1, 5 X2) -> pointer flag; extended = X button.
bool button_flag(int32_t button, uint16_t* flag, bool* extended) {
  switch (button) {
    case 1: *flag = kPtrButton1; *extended = false; return true;
    case 2: *flag = kPtrButton2; *extended = false; return true;
    case 3: *flag = kPtrButton3; *extended = false; return true;
    case 4: *flag = kPtrXButton1; *extended = true; return true;
    case 5: *flag = kPtrXButton2; *extended = true; return true;
    default: return false;
  }
}

}  // namespace

void InputMapper::reset(const ServerInputCaps& caps) {
  caps_ = caps;
  keys_.reset();
  buttons_ = 0;
  relative_ = false;
}

void InputMapper::take_position(const SpInputEvent& event) {
  if ((static_cast<uint32_t>(event.b) & SP_IN_FLAG_POS) == 0) return;
  x_ = coord(event.x);
  y_ = coord(event.y);
}

bool InputMapper::map_key(uint32_t scancode, uint32_t flags, std::vector<Op>& out) {
  if (scancode == 0 || scancode > 0xFF) return false;
  const bool up = (flags & SP_IN_FLAG_UP) != 0;
  if (flags & SP_IN_FLAG_E1) {
    // Pause has no break code of its own: FreeRDP sends the whole E1 1D 45 / E1 9D C5 sequence on the press.
    if (!up) out.push_back({Op::Kind::pause});
    return true;
  }
  const bool extended = (flags & SP_IN_FLAG_EXTENDED) != 0;
  const size_t id = scancode | (extended ? 0x100u : 0u);
  uint16_t kbd = extended ? kKbdExtended : 0;
  if (up) {
    kbd |= kKbdRelease;
    keys_.reset(id);
  } else {
    if (keys_.test(id)) kbd |= kKbdDown;  // auto-repeat: the key was already down
    keys_.set(id);
  }
  out.push_back({Op::Kind::keyboard, kbd, static_cast<uint16_t>(scancode)});
  return true;
}

bool InputMapper::map_button(const SpInputEvent& event, std::vector<Op>& out) {
  uint16_t flag = 0;
  bool extended = false;
  if (!button_flag(event.a, &flag, &extended)) return false;
  if (extended && !caps_.extended_mouse) return false;
  const bool up = (static_cast<uint32_t>(event.b) & SP_IN_FLAG_UP) != 0;
  take_position(event);
  const uint8_t bit = static_cast<uint8_t>(1u << (event.a - 1));
  if (up) {
    buttons_ &= static_cast<uint8_t>(~bit);
  } else {
    buttons_ |= bit;
  }
  const uint16_t down = up ? 0 : (extended ? kPtrXDown : kPtrDown);
  if (relative_ && caps_.relative_mouse) {
    // A relative-mode pointer (a game reading raw input) must not see an absolute position with the click.
    out.push_back({Op::Kind::relmouse, static_cast<uint16_t>(flag | down), 0, 0, 0});
  } else {
    out.push_back({extended ? Op::Kind::xmouse : Op::Kind::mouse, static_cast<uint16_t>(flag | down), 0, x_, y_});
  }
  return true;
}

bool InputMapper::map_wheel(const SpInputEvent& event, bool horizontal, std::vector<Op>& out) {
  if (horizontal && !caps_.horizontal_wheel) return false;
  if (event.a == 0) return true;
  take_position(event);
  // The rotation is a 9-bit two's complement value (bit 8 = PTR_FLAGS_WHEEL_NEGATIVE): -256..255 per PDU.
  int32_t remaining = event.a;
  const uint16_t base = horizontal ? kPtrHWheel : kPtrWheel;
  int pdus = 0;
  while (remaining != 0 && pdus < 64) {
    const int32_t step = std::clamp<int32_t>(remaining, -255, 255);
    remaining -= step;
    out.push_back({Op::Kind::mouse, static_cast<uint16_t>(base | (static_cast<uint16_t>(step) & kPtrWheelMask)), 0,
                   x_, y_});
    ++pdus;
  }
  return true;
}

bool InputMapper::map(const SpInputEvent& event, std::vector<Op>& out) {
  switch (event.type) {
    case SP_IN_KEY:
      return map_key(static_cast<uint32_t>(event.a), static_cast<uint32_t>(event.b), out);

    case SP_IN_UNICODE: {
      const auto cp = static_cast<uint32_t>(event.a);
      if (cp == 0 || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF) || !caps_.unicode) return false;
      const uint16_t flags = (static_cast<uint32_t>(event.b) & SP_IN_FLAG_UP) ? kKbdRelease : 0;
      if (cp < 0x10000) {
        out.push_back({Op::Kind::unicode, flags, static_cast<uint16_t>(cp)});
      } else {
        const uint32_t v = cp - 0x10000;
        out.push_back({Op::Kind::unicode, flags, static_cast<uint16_t>(0xD800 + (v >> 10))});
        out.push_back({Op::Kind::unicode, flags, static_cast<uint16_t>(0xDC00 + (v & 0x3FF))});
      }
      return true;
    }

    case SP_IN_MOUSE_ABS:
      x_ = coord(event.a);
      y_ = coord(event.b);
      relative_ = false;
      out.push_back({Op::Kind::mouse, kPtrMove, 0, x_, y_});
      return true;

    case SP_IN_MOUSE_REL: {
      if (!caps_.relative_mouse) return false;
      if (event.a == 0 && event.b == 0) return true;
      relative_ = true;
      // Moonlight deltas are 16-bit already; split anything larger rather than clip it.
      int32_t dx = event.a;
      int32_t dy = event.b;
      int pdus = 0;
      while ((dx != 0 || dy != 0) && pdus < 16) {
        const int16_t sx = delta16(dx);
        const int16_t sy = delta16(dy);
        dx -= sx;
        dy -= sy;
        out.push_back({Op::Kind::relmouse, kPtrMove, 0, sx, sy});
        ++pdus;
      }
      return true;
    }

    case SP_IN_BUTTON:
      return map_button(event, out);

    case SP_IN_WHEEL:
      return map_wheel(event, false, out);

    case SP_IN_HWHEEL:
      return map_wheel(event, true, out);

    case SP_IN_RELEASE_ALL:
      release_all(out);
      return true;

    default:
      // SP_IN_SAS is handled by the caller (it is a request, not a PDU of its own).
      return false;
  }
}

void InputMapper::sas(std::vector<Op>& out) const {
  out.push_back({Op::Kind::keyboard, 0, kScanLeftCtrl});
  out.push_back({Op::Kind::keyboard, 0, kScanLeftAlt});
  out.push_back({Op::Kind::keyboard, kKbdExtended, kScanDelete});
  out.push_back({Op::Kind::keyboard, static_cast<uint16_t>(kKbdExtended | kKbdRelease), kScanDelete});
  out.push_back({Op::Kind::keyboard, kKbdRelease, kScanLeftAlt});
  out.push_back({Op::Kind::keyboard, kKbdRelease, kScanLeftCtrl});
}

void InputMapper::release_all(std::vector<Op>& out) {
  for (size_t id = 0; id < keys_.size(); ++id) {
    if (!keys_.test(id)) continue;
    const bool extended = (id & 0x100u) != 0;
    out.push_back({Op::Kind::keyboard, static_cast<uint16_t>(kKbdRelease | (extended ? kKbdExtended : 0)),
                   static_cast<uint16_t>(id & 0xFFu)});
  }
  keys_.reset();
  for (int32_t button = 1; button <= 5; ++button) {
    if ((buttons_ & (1u << (button - 1))) == 0) continue;
    uint16_t flag = 0;
    bool extended = false;
    button_flag(button, &flag, &extended);
    if (relative_ && caps_.relative_mouse) {
      out.push_back({Op::Kind::relmouse, flag, 0, 0, 0});
    } else {
      out.push_back({extended ? Op::Kind::xmouse : Op::Kind::mouse, flag, 0, x_, y_});
    }
  }
  buttons_ = 0;
}

}  // namespace splitplay::seat::rdp
