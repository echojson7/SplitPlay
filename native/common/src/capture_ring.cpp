// SPDX-License-Identifier: MIT
#include "splitplay/common/capture_ring.hpp"

#include <cstring>
#include <format>

#include "splitplay/common/shared_atomic.hpp"

namespace splitplay::capture {

void init_header(SpCaptureRingHeader* header, const RingFormat& format, uint32_t producer_pid) noexcept {
  std::memset(static_cast<void*>(header), 0, sizeof(SpCaptureRingHeader));
  header->magic = SP_CAPTURE_RING_MAGIC;
  header->version = SP_CAPTURE_RING_VERSION;
  header->header_size = sizeof(SpCaptureRingHeader);
  header->slot_count = SP_CAPTURE_SLOTS;
  header->width = format.width;
  header->height = format.height;
  header->refresh_num = format.refresh_num;
  header->refresh_den = format.refresh_den;
  header->pixel_format = format.pixel_format;
  header->color_space = format.color_space;
  header->adapter_luid = format.adapter_luid;
  header->producer_pid = producer_pid;
  std::atomic_thread_fence(std::memory_order_release);
}

bool validate_header(const SpCaptureRingHeader* header, size_t mapped_size, std::string* error) {
  auto fail = [&](std::string msg) {
    if (error) *error = std::move(msg);
    return false;
  };
  if (header == nullptr) return fail("capture ring is not mapped");
  if (mapped_size < sizeof(SpCaptureRingHeader))
    return fail(std::format("capture ring mapping too small: {} < {}", mapped_size, sizeof(SpCaptureRingHeader)));
  if (header->magic != SP_CAPTURE_RING_MAGIC) return fail(std::format("bad capture ring magic 0x{:08x}", header->magic));
  if (header->version != SP_CAPTURE_RING_VERSION)
    return fail(std::format("unsupported capture ring version {}", header->version));
  if (header->header_size < sizeof(SpCaptureRingHeader))
    return fail(std::format("capture ring header_size {} too small", header->header_size));
  if (header->slot_count != SP_CAPTURE_SLOTS) return fail(std::format("unexpected slot_count {}", header->slot_count));
  return true;
}

std::optional<uint32_t> Producer::begin_frame() noexcept {
  const uint32_t latest = shm::load_relaxed(header_->latest_slot);
  const bool have_latest = shm::load_relaxed(header_->latest_frame_id) != 0;
  // Prefer the slot that follows the latest one (round robin keeps the oldest frame for reuse).
  for (uint32_t i = 1; i <= SP_CAPTURE_SLOTS; ++i) {
    const uint32_t slot = (latest + i) % SP_CAPTURE_SLOTS;
    if (have_latest && slot == latest) continue;
    if (shm::load_seq_cst(header_->slots[slot].consumer_busy) == 0) return slot;
  }
  shm::store_relaxed(header_->frames_dropped, shm::load_relaxed(header_->frames_dropped) + 1);
  return std::nullopt;
}

void Producer::publish(uint32_t slot, const PublishInfo& info) noexcept {
  SpCaptureSlot& s = header_->slots[slot];
  shm::store_relaxed_nv(s.present_qpc, info.present_qpc);
  shm::store_relaxed_nv(s.acquire_qpc, info.acquire_qpc);
  shm::store_relaxed_nv(s.publish_qpc, info.publish_qpc);
  shm::store_relaxed_nv(s.fence_value, info.fence_value);
  shm::store_relaxed_nv(s.generation, info.generation);
  shm::store_relaxed_nv(s.dirty, info.dirty);
  std::memcpy(&s.cursor, &info.cursor, sizeof(SpCursorState));
  // frame_id last: a consumer that sees the new frame_id after acquiring latest_frame_id sees the fields above.
  shm::ref(s.frame_id).store(info.frame_id, std::memory_order_release);
  shm::store_release(header_->latest_slot, slot);
  shm::store_seq_cst(header_->latest_frame_id, info.frame_id);
  shm::store_relaxed(header_->frames_published, shm::load_relaxed(header_->frames_published) + 1);
}

void Producer::set_generation(uint32_t generation, uint32_t width, uint32_t height) noexcept {
  header_->width = width;
  header_->height = height;
  shm::store_release(header_->generation, generation);
}

void Producer::heartbeat(uint64_t qpc) noexcept { shm::store_relaxed(header_->producer_heartbeat_qpc, qpc); }

bool Consumer::try_acquire(AcquiredSlot& out) noexcept {
  for (int attempt = 0; attempt < 8; ++attempt) {
    const uint64_t frame_id = shm::load_acquire(header_->latest_frame_id);
    if (frame_id == 0 || frame_id == last_frame_id_) return false;
    const uint32_t slot = shm::load_acquire(header_->latest_slot);
    if (slot >= SP_CAPTURE_SLOTS) return false;
    SpCaptureSlot& s = header_->slots[slot];
    shm::store_seq_cst(s.consumer_busy, 1u);
    const bool still_latest = shm::load_seq_cst(header_->latest_frame_id) == frame_id;
    const bool slot_matches = shm::ref(s.frame_id).load(std::memory_order_acquire) == frame_id;
    if (!still_latest || !slot_matches) {
      shm::store_release(s.consumer_busy, 0u);
      ++retries_;
      continue;
    }
    out.slot = slot;
    out.info.frame_id = frame_id;
    out.info.present_qpc = shm::load_relaxed_nv(s.present_qpc);
    out.info.acquire_qpc = shm::load_relaxed_nv(s.acquire_qpc);
    out.info.publish_qpc = shm::load_relaxed_nv(s.publish_qpc);
    out.info.fence_value = shm::load_relaxed_nv(s.fence_value);
    out.info.generation = shm::load_relaxed_nv(s.generation);
    out.info.dirty = shm::load_relaxed_nv(s.dirty);
    out.info.consumer_busy = 1;
    std::memcpy(&out.info.cursor, &s.cursor, sizeof(SpCursorState));
    out.skipped = last_frame_id_ != 0 && frame_id > last_frame_id_ + 1 ? frame_id - last_frame_id_ - 1 : 0;
    last_frame_id_ = frame_id;
    return true;
  }
  return false;
}

void Consumer::release(uint32_t slot) noexcept {
  if (slot < SP_CAPTURE_SLOTS) shm::store_release(header_->slots[slot].consumer_busy, 0u);
}

}  // namespace splitplay::capture
