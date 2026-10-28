// SPDX-License-Identifier: MIT
// Capture ring: the contract between the SplitPlay remote IDD (producer) and the desk worker (consumer).
//
// The producer owns all resources. For each desk it creates:
//   - a shared-memory section  Global\SplitPlay.CaptureRing.<desk-id>   holding SpCaptureRingHeader
//   - an auto-reset event      Global\SplitPlay.FrameReady.<desk-id>    signalled after every published frame
//   - per generation, SP_CAPTURE_SLOTS named shared D3D11 textures
//       Global\SplitPlay.Frame.<desk-id>.<generation>.<slot>
//     and one named shared fence
//       Global\SplitPlay.Fence.<desk-id>.<generation>
//
// A "generation" changes whenever the textures are recreated (mode change, adapter change, HDR toggle).
// The consumer re-opens textures and the fence when it sees a new generation.
//
// Publishing (producer):
//   1. pick a slot whose consumer_busy == 0 (none free -> drop the frame, increment frames_dropped)
//   2. GPU-copy the DWM surface into the slot texture, signal the fence with ++fence_value
//   3. fill SpCaptureSlot (frame_id, qpc stamps, fence_value, cursor, ...)
//   4. store latest_slot / latest_frame_id with release semantics, SetEvent(FrameReady)
//
// Consuming (worker):
//   1. wait on FrameReady (encode on arrival; never poll on a timer)
//   2. read latest_slot with acquire semantics, set that slot's consumer_busy = 1
//   3. GPU-wait on the fence for fence_value, convert + composite cursor + encode
//   4. set consumer_busy = 0 once the GPU no longer reads the texture
//
// All integers are little-endian; the layout is identical for x64 and ARM64 builds.
#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define SP_CAPTURE_RING_MAGIC   0x52435053u /* 'SPCR' */
#define SP_CAPTURE_RING_VERSION 1u
#define SP_CAPTURE_SLOTS        3u
#define SP_CURSOR_MAX_DIM       256u

typedef enum SpPixelFormat {
  SP_PIXEL_BGRA8 = 87,          /* DXGI_FORMAT_B8G8R8A8_UNORM, SDR */
  SP_PIXEL_RGBA16F = 10,        /* DXGI_FORMAT_R16G16B16A16_FLOAT, HDR (IddCx 1.10 FP16) */
} SpPixelFormat;

typedef enum SpColorSpace {
  SP_COLOR_SRGB = 0,            /* DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709 */
  SP_COLOR_SCRGB_LINEAR = 1,    /* DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709 */
  SP_COLOR_HDR10 = 2,           /* DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 */
} SpColorSpace;

typedef struct SpHdrMetadata {
  uint16_t primaries[8];        /* R.x R.y G.x G.y B.x B.y W.x W.y, units of 0.00002 */
  uint32_t max_mastering_nits;
  uint32_t min_mastering_millinits;
  uint16_t max_cll;
  uint16_t max_fall;
  uint32_t sdr_white_nits;      /* SDR content white level inside HDR, for tone mapping */
} SpHdrMetadata;

typedef struct SpCursorState {
  int32_t  x, y;                /* hot spot position in desktop pixels */
  uint32_t visible;             /* 0 or 1 */
  uint32_t shape_id;            /* increments when the shape below changes */
  uint32_t width, height;       /* <= SP_CURSOR_MAX_DIM */
  int32_t  hot_x, hot_y;
  uint32_t reserved[2];
} SpCursorState;

typedef struct SpCaptureSlot {
  uint64_t frame_id;            /* monotonically increasing, also used as the trace id (trace.h) */
  uint64_t present_qpc;         /* IDDCX_METADATA.PresentDisplayQPCTime */
  uint64_t acquire_qpc;         /* QPC when the producer acquired the buffer */
  uint64_t publish_qpc;         /* QPC right before SetEvent */
  uint64_t fence_value;         /* value the producer signalled after the copy */
  uint32_t generation;          /* generation of the texture this slot refers to */
  uint32_t dirty;               /* 0 = DWM reported no change since the previous frame */
  volatile uint32_t consumer_busy; /* written by the consumer only */
  uint32_t reserved0;
  SpCursorState cursor;
  uint32_t reserved[8];
} SpCaptureSlot;

typedef struct SpCaptureRingHeader {
  uint32_t magic;               /* SP_CAPTURE_RING_MAGIC */
  uint32_t version;             /* SP_CAPTURE_RING_VERSION */
  uint32_t header_size;         /* sizeof(SpCaptureRingHeader), for forward compatibility */
  uint32_t slot_count;          /* SP_CAPTURE_SLOTS */

  volatile uint32_t generation; /* current texture generation */
  uint32_t width, height;
  uint32_t refresh_num, refresh_den; /* active mode, e.g. 500/1 */
  uint32_t pixel_format;        /* SpPixelFormat */
  uint32_t color_space;         /* SpColorSpace */
  uint32_t reserved0;
  uint64_t adapter_luid;        /* render adapter the textures live on */

  volatile uint64_t latest_frame_id;
  volatile uint32_t latest_slot;
  uint32_t reserved1;

  volatile uint64_t frames_published;
  volatile uint64_t frames_dropped;  /* no free slot */
  volatile uint64_t producer_heartbeat_qpc;
  volatile uint32_t producer_pid;    /* WUDFHost process id */
  volatile uint32_t consumer_pid;    /* worker process id, written by the consumer */

  SpHdrMetadata hdr;
  SpCaptureSlot slots[SP_CAPTURE_SLOTS];

  /* Cursor shape: BGRA, premultiplied alpha, row pitch SP_CURSOR_MAX_DIM * 4. */
  volatile uint32_t cursor_shape_id;
  uint32_t reserved2[7];
  uint8_t cursor_bgra[SP_CURSOR_MAX_DIM * SP_CURSOR_MAX_DIM * 4];
} SpCaptureRingHeader;

#ifdef __cplusplus
} /* extern "C" */
static_assert(sizeof(SpCursorState) == 40, "SpCursorState layout");
static_assert(sizeof(SpCaptureSlot) == 128, "SpCaptureSlot layout");
#endif
