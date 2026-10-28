// SPDX-License-Identifier: MIT
// SPSC semantics of the input ring: ordering, wrap-around, drop-when-full, concurrent producer/consumer.
#include <atomic>
#include <memory>
#include <thread>
#include <vector>

#include "sp_test.hpp"
#include "splitplay/common/input_ring.hpp"

using namespace splitplay;

namespace {

std::unique_ptr<SpInputRing> make_ring() {
  auto ring = std::make_unique<SpInputRing>();
  input::init_ring(ring.get(), 0);
  return ring;
}

SpInputEvent ev(uint32_t type, int32_t a, int32_t b = 0) {
  SpInputEvent e{};
  e.type = type;
  e.a = a;
  e.b = b;
  return e;
}

}  // namespace

TEST_CASE("init and validate") {
  auto ring = make_ring();
  std::string err;
  CHECK(input::validate_ring(ring.get(), sizeof(SpInputRing), &err));
  CHECK(!input::validate_ring(ring.get(), sizeof(SpInputRing) - 1, &err));
  ring->magic = 0;
  CHECK(!input::validate_ring(ring.get(), sizeof(SpInputRing), &err));
  ring = make_ring();
  ring->capacity = 512;
  CHECK(!input::validate_ring(ring.get(), sizeof(SpInputRing), &err));
  ring = make_ring();
  ring->version = 2;
  CHECK(!input::validate_ring(ring.get(), sizeof(SpInputRing), &err));
}

TEST_CASE("fifo order and sequence numbers") {
  auto ring = make_ring();
  input::Producer p(ring.get());
  input::Consumer c(ring.get());
  SpInputEvent out{};
  CHECK(!c.pop(out));
  for (int i = 0; i < 10; ++i) CHECK(p.push(ev(SP_IN_KEY, i)));
  CHECK_EQ(c.pending(), 10u);
  for (int i = 0; i < 10; ++i) {
    REQUIRE(c.pop(out));
    CHECK_EQ(out.a, i);
    CHECK_EQ(out.seq, static_cast<uint32_t>(i + 1));
    CHECK_EQ(out.type, static_cast<uint32_t>(SP_IN_KEY));
  }
  CHECK(!c.pop(out));
  CHECK_EQ(ring->write_index, 10u);
  CHECK_EQ(ring->read_index, 10u);
}

TEST_CASE("drop when full, never block") {
  auto ring = make_ring();
  input::Producer p(ring.get());
  input::Consumer c(ring.get());
  for (uint32_t i = 0; i < SP_INPUT_RING_CAPACITY; ++i) CHECK(p.push(ev(SP_IN_MOUSE_REL, static_cast<int32_t>(i))));
  CHECK(!p.push(ev(SP_IN_MOUSE_REL, -1)));
  CHECK(!p.push(ev(SP_IN_MOUSE_REL, -2)));
  CHECK_EQ(ring->dropped, 2u);
  CHECK_EQ(p.dropped(), 2u);
  CHECK_EQ(c.pending(), static_cast<uint64_t>(SP_INPUT_RING_CAPACITY));
  // Dropped events consume sequence numbers, so the consumer can see the gap.
  SpInputEvent out{};
  REQUIRE(c.pop(out));
  CHECK_EQ(out.a, 0);
  CHECK(p.push(ev(SP_IN_MOUSE_REL, 9999)));
  std::vector<SpInputEvent> rest(SP_INPUT_RING_CAPACITY);
  const size_t n = c.pop_many(rest.data(), rest.size());
  CHECK_EQ(n, static_cast<size_t>(SP_INPUT_RING_CAPACITY));
  CHECK_EQ(rest[n - 2].a, static_cast<int32_t>(SP_INPUT_RING_CAPACITY - 1));
  CHECK_EQ(rest[n - 1].a, 9999);
  CHECK_EQ(rest[n - 1].seq, static_cast<uint32_t>(SP_INPUT_RING_CAPACITY + 3));
}

TEST_CASE("wrap-around over many laps") {
  auto ring = make_ring();
  input::Producer p(ring.get());
  input::Consumer c(ring.get());
  int32_t next_expected = 0;
  int32_t next_value = 0;
  SpInputEvent buf[100];
  // Push in bursts of 700 and pop in chunks of 100: indices pass the capacity many times.
  for (int round = 0; round < 50; ++round) {
    for (int i = 0; i < 700; ++i) CHECK(p.push(ev(SP_IN_WHEEL, next_value++)));
    while (c.pending() > 0) {
      const size_t n = c.pop_many(buf, 100);
      for (size_t i = 0; i < n; ++i) CHECK_EQ(buf[i].a, next_expected++);
    }
  }
  CHECK_EQ(next_expected, 35000);
  CHECK_EQ(ring->dropped, 0u);
  CHECK(ring->write_index > 30u * SP_INPUT_RING_CAPACITY);
}

TEST_CASE("indices near 2^64 wrap correctly") {
  auto ring = make_ring();
  const uint64_t start = UINT64_MAX - 5;
  ring->write_index = start;
  ring->read_index = start;
  input::Producer p(ring.get());
  input::Consumer c(ring.get());
  for (int i = 0; i < 20; ++i) CHECK(p.push(ev(SP_IN_KEY, i)));
  CHECK_EQ(c.pending(), 20u);
  SpInputEvent out{};
  for (int i = 0; i < 20; ++i) {
    REQUIRE(c.pop(out));
    CHECK_EQ(out.a, i);
  }
  CHECK_EQ(ring->write_index, start + 20);  // wrapped past zero
  // Full detection across the wrap.
  for (uint32_t i = 0; i < SP_INPUT_RING_CAPACITY; ++i) CHECK(p.push(ev(SP_IN_KEY, 0)));
  CHECK(!p.push(ev(SP_IN_KEY, 0)));
}

TEST_CASE("producer and consumer resume from shared indices") {
  auto ring = make_ring();
  {
    input::Producer p(ring.get());
    for (int i = 0; i < 5; ++i) p.push(ev(SP_IN_KEY, i));
  }
  {
    input::Consumer c(ring.get());
    SpInputEvent out{};
    REQUIRE(c.pop(out));
    CHECK_EQ(out.a, 0);
  }
  input::Producer p2(ring.get());  // a restarted worker continues at write_index
  CHECK(p2.push(ev(SP_IN_KEY, 5)));
  input::Consumer c2(ring.get());  // a restarted seat continues at read_index
  CHECK_EQ(c2.pending(), 5u);
  SpInputEvent out{};
  for (int i = 1; i <= 5; ++i) {
    REQUIRE(c2.pop(out));
    CHECK_EQ(out.a, i);
  }
}

TEST_CASE("concurrent spsc stress") {
  auto ring = make_ring();
  constexpr int kEvents = 400000;
  std::atomic<bool> done{false};
  std::atomic<int> pushed_ok{0};
  std::thread producer([&] {
    input::Producer p(ring.get());
    for (int i = 0; i < kEvents; ++i) {
      if (p.push(ev(SP_IN_MOUSE_REL, i, -i))) pushed_ok.fetch_add(1, std::memory_order_relaxed);
      if ((i & 1023) == 0) std::this_thread::yield();
    }
    done.store(true);
  });
  input::Consumer c(ring.get());
  int received = 0;
  int32_t last_a = -1;
  uint32_t last_seq = 0;
  bool ordered = true, payload_ok = true;
  SpInputEvent buf[64];
  for (;;) {
    const size_t n = c.pop_many(buf, 64);
    for (size_t i = 0; i < n; ++i) {
      if (buf[i].a <= last_a || buf[i].seq <= last_seq) ordered = false;
      if (buf[i].b != -buf[i].a || buf[i].seq != static_cast<uint32_t>(buf[i].a) + 1) payload_ok = false;
      last_a = buf[i].a;
      last_seq = buf[i].seq;
      ++received;
    }
    if (n == 0 && done.load() && c.pending() == 0) break;
  }
  producer.join();
  CHECK(ordered);
  CHECK(payload_ok);
  CHECK_EQ(received, pushed_ok.load());
  CHECK_EQ(static_cast<uint64_t>(received) + ring->dropped, static_cast<uint64_t>(kEvents));
}

SP_TEST_MAIN()
