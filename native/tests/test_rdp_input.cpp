// SPDX-License-Identifier: MIT
// Input-ring events -> RDP input PDUs (seat/src/rdp_input.hpp): flags, repeat, positions, wheel encoding, release-all.
#include <vector>

#include "rdp_input.hpp"
#include "sp_test.hpp"
#include "splitplay/common/input_ring.hpp"

using namespace splitplay;
using namespace splitplay::seat::rdp;
using Kind = Op::Kind;

namespace {

SpInputEvent ev(uint32_t type, int32_t a, int32_t b = 0, int32_t x = 0, int32_t y = 0) {
  SpInputEvent e{};
  e.type = type;
  e.a = a;
  e.b = b;
  e.x = x;
  e.y = y;
  return e;
}

InputMapper mapper(bool relative = false) {
  InputMapper m;
  ServerInputCaps caps;
  caps.relative_mouse = relative;
  caps.horizontal_wheel = true;
  caps.extended_mouse = true;
  m.reset(caps);
  return m;
}

std::vector<Op> map(InputMapper& m, const SpInputEvent& e, bool expect_ok = true) {
  std::vector<Op> out;
  const bool ok = m.map(e, out);
  CHECK(ok == expect_ok);
  return out;
}

}  // namespace

TEST_CASE("keys: press, repeat, release, extended, pause") {
  auto m = mapper();
  auto ops = map(m, ev(SP_IN_KEY, 0x1E));  // A down
  REQUIRE(ops.size() == 1);
  CHECK(ops[0] == (Op{Kind::keyboard, 0, 0x1E}));
  ops = map(m, ev(SP_IN_KEY, 0x1E));  // auto-repeat
  CHECK(ops[0] == (Op{Kind::keyboard, kKbdDown, 0x1E}));
  CHECK(m.keys_down() == 1);
  ops = map(m, ev(SP_IN_KEY, 0x1E, SP_IN_FLAG_UP));
  CHECK(ops[0] == (Op{Kind::keyboard, kKbdRelease, 0x1E}));
  CHECK(m.keys_down() == 0);

  ops = map(m, ev(SP_IN_KEY, 0x48, SP_IN_FLAG_EXTENDED));  // Up arrow
  CHECK(ops[0] == (Op{Kind::keyboard, kKbdExtended, 0x48}));
  ops = map(m, ev(SP_IN_KEY, 0x48, SP_IN_FLAG_EXTENDED | SP_IN_FLAG_UP));
  CHECK(ops[0] == (Op{Kind::keyboard, static_cast<uint16_t>(kKbdExtended | kKbdRelease), 0x48}));

  ops = map(m, ev(SP_IN_KEY, 0x45, SP_IN_FLAG_E1));  // Pause down: the whole sequence
  REQUIRE(ops.size() == 1);
  CHECK(ops[0].kind == Kind::pause);
  CHECK(map(m, ev(SP_IN_KEY, 0x45, SP_IN_FLAG_E1 | SP_IN_FLAG_UP)).empty());

  map(m, ev(SP_IN_KEY, 0), false);
  map(m, ev(SP_IN_KEY, 0x1FF), false);
}

TEST_CASE("unicode: BMP and surrogate pairs") {
  auto m = mapper();
  auto ops = map(m, ev(SP_IN_UNICODE, 0x0436));  // ж
  REQUIRE(ops.size() == 1);
  CHECK(ops[0] == (Op{Kind::unicode, 0, 0x0436}));
  ops = map(m, ev(SP_IN_UNICODE, 0x1F600, SP_IN_FLAG_UP));
  REQUIRE(ops.size() == 2);
  CHECK(ops[0] == (Op{Kind::unicode, kKbdRelease, 0xD83D}));
  CHECK(ops[1] == (Op{Kind::unicode, kKbdRelease, 0xDE00}));
  map(m, ev(SP_IN_UNICODE, 0xD800), false);
  map(m, ev(SP_IN_UNICODE, 0x110000), false);
}

TEST_CASE("absolute pointer and buttons at the last position") {
  auto m = mapper();
  auto ops = map(m, ev(SP_IN_MOUSE_ABS, 640, 360));
  CHECK(ops[0] == (Op{Kind::mouse, kPtrMove, 0, 640, 360}));
  ops = map(m, ev(SP_IN_BUTTON, 1));
  CHECK(ops[0] == (Op{Kind::mouse, static_cast<uint16_t>(kPtrButton1 | kPtrDown), 0, 640, 360}));
  ops = map(m, ev(SP_IN_BUTTON, 2, SP_IN_FLAG_UP));
  CHECK(ops[0] == (Op{Kind::mouse, kPtrButton2, 0, 640, 360}));
  ops = map(m, ev(SP_IN_BUTTON, 3, SP_IN_FLAG_POS, 10, 20));  // the worker knows where the pointer is
  CHECK(ops[0] == (Op{Kind::mouse, static_cast<uint16_t>(kPtrButton3 | kPtrDown), 0, 10, 20}));
  CHECK(m.x() == 10 && m.y() == 20);
  ops = map(m, ev(SP_IN_BUTTON, 5));
  CHECK(ops[0] == (Op{Kind::xmouse, static_cast<uint16_t>(kPtrXButton2 | kPtrXDown), 0, 10, 20}));
  map(m, ev(SP_IN_BUTTON, 6), false);
  ops = map(m, ev(SP_IN_MOUSE_ABS, -5, 70000));  // clamped to the 16-bit PDU fields
  CHECK(ops[0] == (Op{Kind::mouse, kPtrMove, 0, 0, 0xFFFF}));
}

TEST_CASE("relative pointer: needs server support; buttons stay relative") {
  auto plain = mapper(false);
  map(plain, ev(SP_IN_MOUSE_REL, 3, -4), false);

  auto m = mapper(true);
  auto ops = map(m, ev(SP_IN_MOUSE_REL, 3, -4));
  REQUIRE(ops.size() == 1);
  CHECK(ops[0] == (Op{Kind::relmouse, kPtrMove, 0, 3, -4}));
  ops = map(m, ev(SP_IN_BUTTON, 1, SP_IN_FLAG_POS, 500, 500));
  CHECK(ops[0] == (Op{Kind::relmouse, static_cast<uint16_t>(kPtrButton1 | kPtrDown), 0, 0, 0}));
  ops = map(m, ev(SP_IN_BUTTON, 4));
  CHECK(ops[0] == (Op{Kind::relmouse, static_cast<uint16_t>(kPtrXButton1 | kPtrXDown), 0, 0, 0}));
  ops = map(m, ev(SP_IN_MOUSE_REL, 40000, 0));  // split, not clipped
  REQUIRE(ops.size() == 2);
  CHECK(ops[0].x + ops[1].x == 40000);
  CHECK(map(m, ev(SP_IN_MOUSE_REL, 0, 0)).empty());
  // An absolute move ends relative mode: clicks carry the position again.
  map(m, ev(SP_IN_MOUSE_ABS, 7, 8));
  ops = map(m, ev(SP_IN_BUTTON, 1, SP_IN_FLAG_UP));
  CHECK(ops[0] == (Op{Kind::mouse, kPtrButton1, 0, 7, 8}));
}

TEST_CASE("wheel: 9-bit rotation, negative values, large deltas") {
  auto m = mapper();
  auto ops = map(m, ev(SP_IN_WHEEL, 120));
  REQUIRE(ops.size() == 1);
  CHECK(ops[0].flags == (kPtrWheel | 120));
  ops = map(m, ev(SP_IN_WHEEL, -120));
  CHECK(ops[0].flags == (kPtrWheel | kPtrWheelNegative | 0x88));  // -120 in 9 bits = 0x188
  ops = map(m, ev(SP_IN_HWHEEL, 600, SP_IN_FLAG_POS, 3, 4));
  REQUIRE(ops.size() == 3);  // 255 + 255 + 90
  int sum = 0;
  for (const Op& op : ops) {
    CHECK((op.flags & kPtrHWheel) != 0);
    CHECK(op.x == 3 && op.y == 4);
    int v = op.flags & kPtrWheelMask;
    if (v & 0x100) v -= 0x200;
    sum += v;
  }
  CHECK(sum == 600);
  CHECK(map(m, ev(SP_IN_WHEEL, 0)).empty());

  InputMapper no_hwheel;
  no_hwheel.reset(ServerInputCaps{});
  map(no_hwheel, ev(SP_IN_HWHEEL, 120), false);
  map(no_hwheel, ev(SP_IN_BUTTON, 4), false);  // X buttons need INPUT_FLAG_MOUSEX
}

TEST_CASE("release all and Ctrl+Alt+Del") {
  auto m = mapper();
  map(m, ev(SP_IN_MOUSE_ABS, 100, 200));
  map(m, ev(SP_IN_KEY, 0x2A));                        // left Shift
  map(m, ev(SP_IN_KEY, 0x1D, SP_IN_FLAG_EXTENDED));   // right Ctrl
  map(m, ev(SP_IN_BUTTON, 1));
  auto ops = map(m, ev(SP_IN_RELEASE_ALL, 0));
  REQUIRE(ops.size() == 3);
  CHECK(ops[0] == (Op{Kind::keyboard, kKbdRelease, 0x2A}));
  CHECK(ops[1] == (Op{Kind::keyboard, static_cast<uint16_t>(kKbdRelease | kKbdExtended), 0x1D}));
  CHECK(ops[2] == (Op{Kind::mouse, kPtrButton1, 0, 100, 200}));
  CHECK(m.keys_down() == 0 && m.buttons_down() == 0);
  CHECK(map(m, ev(SP_IN_RELEASE_ALL, 0)).empty());

  std::vector<Op> sas;
  m.sas(sas);
  REQUIRE(sas.size() == 6);
  CHECK(sas[0] == (Op{Kind::keyboard, 0, kScanLeftCtrl}));
  CHECK(sas[2] == (Op{Kind::keyboard, kKbdExtended, kScanDelete}));
  CHECK(sas[5] == (Op{Kind::keyboard, kKbdRelease, kScanLeftCtrl}));
}

TEST_CASE("ring flags and reattach keep the producer's index") {
  SpInputRing ring{};
  input::init_ring(&ring, 0);
  input::Producer p(&ring);
  for (int i = 0; i < 5; ++i) CHECK(p.push(ev(SP_IN_KEY, 0x1E)));
  input::Consumer c(&ring);
  SpInputEvent out{};
  CHECK(c.pop(out));
  // The seat restarts: 4 queued events are dropped, the producer's cached write index stays valid.
  CHECK(input::reattach_ring(&ring, SP_INPUT_RING_READY) == 4);
  CHECK(input::get_flags(&ring) == SP_INPUT_RING_READY);
  input::Consumer c2(&ring);
  CHECK(c2.pending() == 0);
  CHECK(p.push(ev(SP_IN_KEY, 0x30)));
  CHECK(c2.pop(out));
  CHECK(out.a == 0x30);
  input::set_flags(&ring, 0);
  CHECK(input::get_flags(&ring) == 0);
  // A section with garbage is initialised from scratch.
  ring.magic = 0;
  CHECK(input::reattach_ring(&ring, 0) == 0);
  std::string err;
  CHECK(input::validate_ring(&ring, sizeof(ring), &err));
}

SP_TEST_MAIN()
