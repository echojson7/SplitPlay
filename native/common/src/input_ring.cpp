// SPDX-License-Identifier: MIT
#include "splitplay/common/input_ring.hpp"

#include <cstring>
#include <format>

#include "splitplay/common/shared_atomic.hpp"

namespace splitplay::input {

static_assert((SP_INPUT_RING_CAPACITY & (SP_INPUT_RING_CAPACITY - 1)) == 0, "capacity must be a power of two");
static constexpr uint64_t kMask = SP_INPUT_RING_CAPACITY - 1;

void init_ring(SpInputRing* ring, uint32_t flags) noexcept {
  std::memset(static_cast<void*>(ring), 0, sizeof(SpInputRing));
  ring->magic = SP_INPUT_RING_MAGIC;
  ring->version = SP_INPUT_RING_VERSION;
  ring->capacity = SP_INPUT_RING_CAPACITY;
  ring->flags = flags;
  std::atomic_thread_fence(std::memory_order_release);
}

bool validate_ring(const SpInputRing* ring, size_t mapped_size, std::string* error) {
  auto fail = [&](std::string msg) {
    if (error) *error = std::move(msg);
    return false;
  };
  if (ring == nullptr) return fail("input ring is not mapped");
  if (mapped_size < sizeof(SpInputRing))
    return fail(std::format("input ring mapping too small: {} < {}", mapped_size, sizeof(SpInputRing)));
  if (ring->magic != SP_INPUT_RING_MAGIC) return fail(std::format("bad input ring magic 0x{:08x}", ring->magic));
  if (ring->version != SP_INPUT_RING_VERSION) return fail(std::format("unsupported input ring version {}", ring->version));
  if (ring->capacity != SP_INPUT_RING_CAPACITY) return fail(std::format("unexpected input ring capacity {}", ring->capacity));
  return true;
}

Producer::Producer(SpInputRing* ring) noexcept
    : ring_(ring), write_index_(shm::load_acquire(ring->write_index)) {}

bool Producer::push(const SpInputEvent& event) noexcept {
  const uint32_t seq = seq_++;
  const uint64_t read_index = shm::load_acquire(ring_->read_index);
  if (write_index_ - read_index >= SP_INPUT_RING_CAPACITY) {
    // Only the producer writes `dropped`; relaxed is enough, readers only use it for statistics.
    shm::store_relaxed(ring_->dropped, shm::load_relaxed(ring_->dropped) + 1);
    return false;
  }
  SpInputEvent& slot = ring_->events[write_index_ & kMask];
  slot = event;
  slot.seq = seq;
  ++write_index_;
  shm::store_release(ring_->write_index, write_index_);
  return true;
}

uint64_t Producer::dropped() const noexcept { return shm::load_relaxed(ring_->dropped); }

Consumer::Consumer(SpInputRing* ring) noexcept : ring_(ring), read_index_(shm::load_acquire(ring->read_index)) {}

bool Consumer::pop(SpInputEvent& out) noexcept { return pop_many(&out, 1) == 1; }

size_t Consumer::pop_many(SpInputEvent* out, size_t max) noexcept {
  const uint64_t write_index = shm::load_acquire(ring_->write_index);
  size_t n = 0;
  while (n < max && read_index_ != write_index) {
    out[n++] = ring_->events[read_index_ & kMask];
    ++read_index_;
  }
  if (n > 0) shm::store_release(ring_->read_index, read_index_);
  return n;
}

uint64_t Consumer::pending() const noexcept { return shm::load_acquire(ring_->write_index) - read_index_; }

const char* input_type_name(uint32_t type) noexcept {
  switch (type) {
    case SP_IN_KEY: return "key";
    case SP_IN_UNICODE: return "unicode";
    case SP_IN_MOUSE_REL: return "mouse_rel";
    case SP_IN_MOUSE_ABS: return "mouse_abs";
    case SP_IN_BUTTON: return "button";
    case SP_IN_WHEEL: return "wheel";
    case SP_IN_HWHEEL: return "hwheel";
    case SP_IN_SAS: return "sas";
    case SP_IN_RELEASE_ALL: return "release_all";
    default: return "unknown";
  }
}

}  // namespace splitplay::input
