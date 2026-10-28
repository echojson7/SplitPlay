// SPDX-License-Identifier: MIT
// Single-producer / single-consumer access to SpInputRing (splitplay/input_ring.h). Pure memory logic: the shared
// section and the InputReady event are handled by splitplay/common/shared_memory.hpp on Windows.
#pragma once

#include <cstddef>
#include <cstdint>
#include <string>

#include "splitplay/input_ring.h"

namespace splitplay::input {

// Consumer (seat) side: zero the ring and write magic/version/capacity/flags.
void init_ring(SpInputRing* ring, uint32_t flags) noexcept;

// Checks magic, version and capacity. `mapped_size` is the size of the mapping that holds the ring.
bool validate_ring(const SpInputRing* ring, size_t mapped_size, std::string* error);

// Producer side (worker). Never blocks: a full ring drops the event and increments `dropped`.
class Producer {
 public:
  explicit Producer(SpInputRing* ring) noexcept;

  // Copies `event`, assigns the next producer sequence number (also consumed by dropped events, so the consumer can
  // see gaps) and publishes it. Returns false when the ring is full.
  bool push(const SpInputEvent& event) noexcept;

  uint32_t next_seq() const noexcept { return seq_; }
  uint64_t dropped() const noexcept;

 private:
  SpInputRing* ring_;
  uint64_t write_index_;
  uint32_t seq_ = 1;
};

// Consumer side (seat).
class Consumer {
 public:
  explicit Consumer(SpInputRing* ring) noexcept;

  // Pops one event; returns false if the ring is empty.
  bool pop(SpInputEvent& out) noexcept;
  // Pops up to `max` events into `out`; returns the number popped.
  size_t pop_many(SpInputEvent* out, size_t max) noexcept;
  // Events currently queued.
  uint64_t pending() const noexcept;

 private:
  SpInputRing* ring_;
  uint64_t read_index_;
};

const char* input_type_name(uint32_t type) noexcept;

}  // namespace splitplay::input
