// SPDX-License-Identifier: MIT
// Golden layout of the shared-memory contracts. Any change here breaks the IDD <-> worker <-> seat ABI.
#include <cstddef>
#include <cstdint>
#include <cstring>

#include "sp_test.hpp"
#include "splitplay/capture_ring.h"
#include "splitplay/common/object_names.hpp"
#include "splitplay/input_ring.h"
#include "splitplay/trace.h"

// Compile-time checks: identical on x64 and ARM64 (all 64-bit fields naturally aligned, no implicit padding).
static_assert(sizeof(SpHdrMetadata) == 32);
static_assert(sizeof(SpCursorState) == 40);
static_assert(sizeof(SpCaptureSlot) == 128);
static_assert(sizeof(SpCaptureRingHeader) == 262696);
static_assert(alignof(SpCaptureRingHeader) == 8);
static_assert(sizeof(SpInputEvent) == 32);
static_assert(sizeof(SpInputRing) == 32848);
static_assert(sizeof(SpTraceRecord) == 24);

#define CHECK_OFFSET(type, field, expected) CHECK_EQ(offsetof(type, field), static_cast<size_t>(expected))

TEST_CASE("capture ring header offsets") {
  CHECK_OFFSET(SpCaptureRingHeader, magic, 0);
  CHECK_OFFSET(SpCaptureRingHeader, version, 4);
  CHECK_OFFSET(SpCaptureRingHeader, header_size, 8);
  CHECK_OFFSET(SpCaptureRingHeader, slot_count, 12);
  CHECK_OFFSET(SpCaptureRingHeader, generation, 16);
  CHECK_OFFSET(SpCaptureRingHeader, width, 20);
  CHECK_OFFSET(SpCaptureRingHeader, height, 24);
  CHECK_OFFSET(SpCaptureRingHeader, refresh_num, 28);
  CHECK_OFFSET(SpCaptureRingHeader, refresh_den, 32);
  CHECK_OFFSET(SpCaptureRingHeader, pixel_format, 36);
  CHECK_OFFSET(SpCaptureRingHeader, color_space, 40);
  CHECK_OFFSET(SpCaptureRingHeader, adapter_luid, 48);
  CHECK_OFFSET(SpCaptureRingHeader, latest_frame_id, 56);
  CHECK_OFFSET(SpCaptureRingHeader, latest_slot, 64);
  CHECK_OFFSET(SpCaptureRingHeader, frames_published, 72);
  CHECK_OFFSET(SpCaptureRingHeader, frames_dropped, 80);
  CHECK_OFFSET(SpCaptureRingHeader, producer_heartbeat_qpc, 88);
  CHECK_OFFSET(SpCaptureRingHeader, producer_pid, 96);
  CHECK_OFFSET(SpCaptureRingHeader, consumer_pid, 100);
  CHECK_OFFSET(SpCaptureRingHeader, hdr, 104);
  CHECK_OFFSET(SpCaptureRingHeader, slots, 136);
  CHECK_OFFSET(SpCaptureRingHeader, cursor_shape_id, 520);
  CHECK_OFFSET(SpCaptureRingHeader, cursor_bgra, 552);
  CHECK_EQ(sizeof(((SpCaptureRingHeader*)nullptr)->cursor_bgra), static_cast<size_t>(SP_CURSOR_MAX_DIM * SP_CURSOR_MAX_DIM * 4));
}

TEST_CASE("capture slot and cursor offsets") {
  CHECK_OFFSET(SpCaptureSlot, frame_id, 0);
  CHECK_OFFSET(SpCaptureSlot, present_qpc, 8);
  CHECK_OFFSET(SpCaptureSlot, acquire_qpc, 16);
  CHECK_OFFSET(SpCaptureSlot, publish_qpc, 24);
  CHECK_OFFSET(SpCaptureSlot, fence_value, 32);
  CHECK_OFFSET(SpCaptureSlot, generation, 40);
  CHECK_OFFSET(SpCaptureSlot, dirty, 44);
  CHECK_OFFSET(SpCaptureSlot, consumer_busy, 48);
  CHECK_OFFSET(SpCaptureSlot, cursor, 56);
  CHECK_OFFSET(SpCaptureSlot, reserved, 96);
  CHECK_OFFSET(SpCursorState, x, 0);
  CHECK_OFFSET(SpCursorState, y, 4);
  CHECK_OFFSET(SpCursorState, visible, 8);
  CHECK_OFFSET(SpCursorState, shape_id, 12);
  CHECK_OFFSET(SpCursorState, width, 16);
  CHECK_OFFSET(SpCursorState, height, 20);
  CHECK_OFFSET(SpCursorState, hot_x, 24);
  CHECK_OFFSET(SpCursorState, hot_y, 28);
  CHECK_OFFSET(SpHdrMetadata, max_mastering_nits, 16);
  CHECK_OFFSET(SpHdrMetadata, min_mastering_millinits, 20);
  CHECK_OFFSET(SpHdrMetadata, max_cll, 24);
  CHECK_OFFSET(SpHdrMetadata, max_fall, 26);
  CHECK_OFFSET(SpHdrMetadata, sdr_white_nits, 28);
}

TEST_CASE("input ring offsets") {
  CHECK_OFFSET(SpInputRing, magic, 0);
  CHECK_OFFSET(SpInputRing, version, 4);
  CHECK_OFFSET(SpInputRing, capacity, 8);
  CHECK_OFFSET(SpInputRing, flags, 12);
  CHECK_OFFSET(SpInputRing, write_index, 16);
  CHECK_OFFSET(SpInputRing, read_index, 24);
  CHECK_OFFSET(SpInputRing, dropped, 32);
  CHECK_OFFSET(SpInputRing, producer_pid, 40);
  CHECK_OFFSET(SpInputRing, consumer_pid, 44);
  CHECK_OFFSET(SpInputRing, events, 80);
  CHECK_OFFSET(SpInputEvent, qpc, 0);
  CHECK_OFFSET(SpInputEvent, type, 8);
  CHECK_OFFSET(SpInputEvent, a, 12);
  CHECK_OFFSET(SpInputEvent, b, 16);
  CHECK_OFFSET(SpInputEvent, seq, 20);
  CHECK((SP_INPUT_RING_CAPACITY & (SP_INPUT_RING_CAPACITY - 1)) == 0);
}

TEST_CASE("magic values spell their tags") {
  const uint32_t cr = SP_CAPTURE_RING_MAGIC, ir = SP_INPUT_RING_MAGIC;
  CHECK(std::memcmp(&cr, "SPCR", 4) == 0);
  CHECK(std::memcmp(&ir, "SPIR", 4) == 0);
}

TEST_CASE("trace record and desk hash") {
  CHECK_OFFSET(SpTraceRecord, frame_id, 0);
  CHECK_OFFSET(SpTraceRecord, qpc, 8);
  CHECK_OFFSET(SpTraceRecord, stage, 16);
  CHECK_OFFSET(SpTraceRecord, desk_hash, 20);
  CHECK_EQ(sp_desk_hash("desk-1"), 0xde69751au);
  CHECK_EQ(sp_desk_hash("gaming"), 0x7b685cf0u);
  CHECK_EQ(sp_desk_hash(""), 2166136261u);
  CHECK_EQ(sp_desk_hash(nullptr), 2166136261u);
}

TEST_CASE("object and pipe names") {
  using namespace splitplay;
  CHECK(capture_ring_section_name(ObjectNamespace::global, "desk-1") == "Global\\SplitPlay.CaptureRing.desk-1");
  CHECK(frame_ready_event_name(ObjectNamespace::global, "desk-1") == "Global\\SplitPlay.FrameReady.desk-1");
  CHECK(frame_texture_name(ObjectNamespace::global, "desk-1", 3, 2) == "Global\\SplitPlay.Frame.desk-1.3.2");
  CHECK(frame_fence_name(ObjectNamespace::global, "desk-1", 3) == "Global\\SplitPlay.Fence.desk-1.3");
  CHECK(input_ring_section_name(ObjectNamespace::global, "gaming") == "Global\\SplitPlay.InputRing.gaming");
  CHECK(input_ready_event_name(ObjectNamespace::local, "gaming") == "Local\\SplitPlay.InputReady.gaming");
  CHECK(control_pipe_name("seat", "desk-1") == "\\\\.\\pipe\\splitplay\\seat-desk-1");
  CHECK(is_valid_desk_id("desk-1"));
  CHECK(is_valid_desk_id("a"));
  CHECK(is_valid_desk_id("0123456789abcdef0123456789abcdef"));
  CHECK(!is_valid_desk_id(""));
  CHECK(!is_valid_desk_id("0123456789abcdef0123456789abcdef0"));
  CHECK(!is_valid_desk_id("Desk"));
  CHECK(!is_valid_desk_id("desk_1"));
  CHECK(!is_valid_desk_id("desk 1"));
  CHECK(!is_valid_desk_id("../x"));
}

SP_TEST_MAIN()
