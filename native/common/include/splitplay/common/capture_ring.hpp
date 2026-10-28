// SPDX-License-Identifier: MIT
// CPU-side protocol of the capture ring (splitplay/capture_ring.h): slot selection, publishing and acquisition.
// GPU work (texture copies, fence signal/wait) is the caller's job; shared memory and the FrameReady event are
// handled by splitplay/common/shared_memory.hpp on Windows.
//
// Race-free slot hand-over (a refinement of the steps in capture_ring.h, same layout):
//   producer: never picks `latest_slot`; picks a slot whose consumer_busy == 0 (seq_cst load), copies, signals the
//             fence, fills the slot (frame_id last), then stores latest_slot and latest_frame_id (release).
//   consumer: reads latest_frame_id / latest_slot (acquire), sets consumer_busy = 1 (seq_cst), then re-reads
//             latest_frame_id and the slot's frame_id. If either changed, the producer may already be reusing the
//             slot: it clears consumer_busy and retries with the newer frame.
// Because the producer only reuses a slot after publishing a newer frame elsewhere, the consumer's re-check detects
// every interleaving in which the producer could have selected the slot before consumer_busy became visible.
#pragma once

#include <cstddef>
#include <cstdint>
#include <optional>
#include <string>

#include "splitplay/capture_ring.h"

namespace splitplay::capture {

struct RingFormat {
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t refresh_num = 60;
  uint32_t refresh_den = 1;
  uint32_t pixel_format = SP_PIXEL_BGRA8;
  uint32_t color_space = SP_COLOR_SRGB;
  uint64_t adapter_luid = 0;
};

// Producer side: zero the header and write the constant fields. generation starts at 0 (no textures yet).
void init_header(SpCaptureRingHeader* header, const RingFormat& format, uint32_t producer_pid) noexcept;

bool validate_header(const SpCaptureRingHeader* header, size_t mapped_size, std::string* error);

struct PublishInfo {
  uint64_t frame_id = 0;
  uint64_t present_qpc = 0;
  uint64_t acquire_qpc = 0;
  uint64_t publish_qpc = 0;
  uint64_t fence_value = 0;
  uint32_t generation = 0;
  uint32_t dirty = 1;
  SpCursorState cursor{};
};

class Producer {
 public:
  explicit Producer(SpCaptureRingHeader* header) noexcept : header_(header) {}

  // Picks a slot for the next frame. Returns std::nullopt (and increments frames_dropped) if every non-latest slot
  // is busy.
  std::optional<uint32_t> begin_frame() noexcept;

  // Fills the slot and makes it the latest frame. The caller sets the FrameReady event afterwards.
  void publish(uint32_t slot, const PublishInfo& info) noexcept;

  // Announce a new texture generation (after the textures and the fence of that generation exist) and mode.
  void set_generation(uint32_t generation, uint32_t width, uint32_t height) noexcept;
  void heartbeat(uint64_t qpc) noexcept;

 private:
  SpCaptureRingHeader* header_;
};

struct AcquiredSlot {
  uint32_t slot = 0;
  SpCaptureSlot info{};       // snapshot of the slot fields (consumer_busy excluded)
  uint64_t skipped = 0;       // frames published since the previously acquired one and never seen
};

class Consumer {
 public:
  explicit Consumer(SpCaptureRingHeader* header) noexcept : header_(header) {}

  // Takes the latest frame if it is newer than the last acquired one. On success the slot is marked busy and must
  // be released with release() once the GPU no longer reads the texture.
  bool try_acquire(AcquiredSlot& out) noexcept;
  void release(uint32_t slot) noexcept;

  uint64_t last_frame_id() const noexcept { return last_frame_id_; }
  uint64_t retries() const noexcept { return retries_; }

 private:
  SpCaptureRingHeader* header_;
  uint64_t last_frame_id_ = 0;
  uint64_t retries_ = 0;
};

}  // namespace splitplay::capture
