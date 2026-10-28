// SPDX-License-Identifier: MIT
// CPU-side capture ring protocol: slot selection, drop when busy, acquire/release, and a concurrent stress test that
// checks the consumer never sees its slot overwritten while it holds it.
#include <atomic>
#include <memory>
#include <thread>

#include "sp_test.hpp"
#include "splitplay/common/capture_ring.hpp"
#include "splitplay/common/shared_atomic.hpp"

using namespace splitplay;

namespace {

std::unique_ptr<SpCaptureRingHeader> make_ring() {
  auto h = std::make_unique<SpCaptureRingHeader>();
  capture::RingFormat f;
  f.width = 1920;
  f.height = 1080;
  f.refresh_num = 500;
  f.adapter_luid = 0x1234;
  capture::init_header(h.get(), f, 42);
  return h;
}

capture::PublishInfo info(uint64_t frame_id, uint32_t generation = 1) {
  capture::PublishInfo i;
  i.frame_id = frame_id;
  i.generation = generation;
  i.fence_value = frame_id * 10;
  i.present_qpc = frame_id * 100;
  i.cursor.x = static_cast<int32_t>(frame_id);
  return i;
}

}  // namespace

TEST_CASE("init and validate header") {
  auto h = make_ring();
  std::string err;
  CHECK(capture::validate_header(h.get(), sizeof(SpCaptureRingHeader), &err));
  CHECK_EQ(h->magic, SP_CAPTURE_RING_MAGIC);
  CHECK_EQ(h->header_size, static_cast<uint32_t>(sizeof(SpCaptureRingHeader)));
  CHECK_EQ(h->slot_count, SP_CAPTURE_SLOTS);
  CHECK_EQ(h->producer_pid, 42u);
  CHECK_EQ(h->refresh_num, 500u);
  CHECK_EQ(h->adapter_luid, 0x1234u);
  CHECK(!capture::validate_header(h.get(), 4096, &err));
  h->version = 7;
  CHECK(!capture::validate_header(h.get(), sizeof(SpCaptureRingHeader), &err));
}

TEST_CASE("producer never reuses the latest slot") {
  auto h = make_ring();
  capture::Producer p(h.get());
  uint32_t prev = UINT32_MAX;
  for (uint64_t f = 1; f <= 20; ++f) {
    auto slot = p.begin_frame();
    REQUIRE(slot.has_value());
    CHECK(*slot != prev);
    p.publish(*slot, info(f));
    CHECK_EQ(h->latest_slot, *slot);
    CHECK_EQ(h->latest_frame_id, f);
    prev = *slot;
  }
  CHECK_EQ(h->frames_published, 20u);
  CHECK_EQ(h->frames_dropped, 0u);
}

TEST_CASE("busy slots are skipped and a full ring drops") {
  auto h = make_ring();
  capture::Producer p(h.get());
  capture::Consumer c(h.get());
  auto s1 = p.begin_frame();
  REQUIRE(s1.has_value());
  p.publish(*s1, info(1));
  capture::AcquiredSlot a;
  REQUIRE(c.try_acquire(a));
  CHECK_EQ(a.slot, *s1);
  CHECK_EQ(a.info.frame_id, 1u);
  CHECK_EQ(a.info.fence_value, 10u);
  CHECK_EQ(a.info.cursor.x, 1);
  CHECK_EQ(h->slots[*s1].consumer_busy, 1u);
  CHECK(!c.try_acquire(a));  // nothing newer

  // Slot s1 is latest and busy: the producer uses the two others.
  for (uint64_t f = 2; f <= 6; ++f) {
    auto s = p.begin_frame();
    REQUIRE(s.has_value());
    CHECK(*s != *s1);
    p.publish(*s, info(f));
  }
  // Hold the latest frame as well.
  capture::AcquiredSlot b;
  REQUIRE(c.try_acquire(b));
  CHECK_EQ(b.info.frame_id, 6u);
  CHECK_EQ(b.skipped, 4u);  // frames 2..5 never seen
  // Now s1 (busy) and b.slot (busy, latest): the third slot is the only candidate.
  auto s = p.begin_frame();
  REQUIRE(s.has_value());
  CHECK(*s != *s1);
  CHECK(*s != b.slot);
  p.publish(*s, info(7));
  // latest = s (free), s1 busy, b.slot busy -> no candidate (latest is never reused).
  CHECK(!p.begin_frame().has_value());
  CHECK_EQ(h->frames_dropped, 1u);
  c.release(a.slot);
  auto again = p.begin_frame();
  REQUIRE(again.has_value());
  CHECK_EQ(*again, a.slot);
  c.release(b.slot);
}

TEST_CASE("consumer retries when the slot changed under it") {
  auto h = make_ring();
  capture::Producer p(h.get());
  capture::Consumer c(h.get());
  auto s = p.begin_frame();
  p.publish(*s, info(1));
  // Simulate the race: latest_slot points at a slot whose frame_id does not match latest_frame_id.
  shm::store_release(h->latest_frame_id, 2u);
  capture::AcquiredSlot a;
  CHECK(!c.try_acquire(a));
  CHECK(c.retries() > 0);
  CHECK_EQ(h->slots[*s].consumer_busy, 0u);  // released again
}

TEST_CASE("generation is announced") {
  auto h = make_ring();
  capture::Producer p(h.get());
  CHECK_EQ(h->generation, 0u);
  p.set_generation(3, 1280, 720);
  CHECK_EQ(h->generation, 3u);
  CHECK_EQ(h->width, 1280u);
  auto s = p.begin_frame();
  p.publish(*s, info(1, 3));
  capture::Consumer c(h.get());
  capture::AcquiredSlot a;
  REQUIRE(c.try_acquire(a));
  CHECK_EQ(a.info.generation, 3u);
}

TEST_CASE("concurrent producer and consumer never share a slot") {
  auto h = make_ring();
  // Stand-in for the slot textures: the producer writes the frame id into its slot's "texture" before publishing.
  std::atomic<uint64_t> texture[SP_CAPTURE_SLOTS] = {};
  std::atomic<bool> stop{false};
  std::atomic<uint64_t> produced{0}, dropped{0};
  std::atomic<bool> corrupted{false};

  std::thread producer([&] {
    capture::Producer p(h.get());
    for (uint64_t f = 1; !stop.load(); ++f) {
      auto s = p.begin_frame();
      if (!s) {
        dropped.fetch_add(1);
        std::this_thread::yield();
        continue;
      }
      // "Copy" in several steps so a concurrent reader would notice.
      texture[*s].store(0);
      texture[*s].store(f);
      p.publish(*s, info(f));
      produced.fetch_add(1);
    }
  });

  capture::Consumer c(h.get());
  uint64_t acquired = 0, last = 0;
  bool monotonic = true;
  const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(700);
  while (std::chrono::steady_clock::now() < deadline) {
    capture::AcquiredSlot a;
    if (!c.try_acquire(a)) continue;
    ++acquired;
    if (a.info.frame_id <= last) monotonic = false;
    last = a.info.frame_id;
    // While we hold the slot its texture must keep our frame id.
    for (int i = 0; i < 200; ++i) {
      if (texture[a.slot].load() != a.info.frame_id) corrupted.store(true);
    }
    c.release(a.slot);
  }
  stop.store(true);
  producer.join();
  CHECK(!corrupted.load());
  CHECK(monotonic);
  CHECK(acquired > 100);
  CHECK(produced.load() > acquired);
  CHECK_EQ(h->frames_published, produced.load());
  CHECK_EQ(h->frames_dropped, dropped.load());
}

SP_TEST_MAIN()
