// SPDX-License-Identifier: MIT
// Frame tracing: every frame carries the frame_id assigned by the IDD (capture_ring.h) through the whole pipeline.
// Each component records QPC timestamps per stage and emits them as ETW events from the SplitPlay provider,
// so that a single ETW session (or PresentMon alongside it) reconstructs present -> encode -> send per frame.
#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ETW provider "SplitPlay" {6B2E3F5A-9C41-4D7B-A1E2-53504C415931}. TraceLogging, event name "Frame". */
#define SP_TRACE_PROVIDER_NAME "SplitPlay"

typedef enum SpTraceStage {
  SP_STAGE_PRESENT = 1,         /* DWM present time reported by IddCx */
  SP_STAGE_IDD_ACQUIRE = 2,     /* IDD acquired the swap-chain buffer */
  SP_STAGE_IDD_PUBLISH = 3,     /* IDD published the slot and set FrameReady */
  SP_STAGE_WORKER_WAKE = 4,     /* worker woke on FrameReady */
  SP_STAGE_CONVERT_DONE = 5,    /* colour conversion + cursor composite finished on the GPU */
  SP_STAGE_ENCODE_DONE = 6,     /* bitstream locked */
  SP_STAGE_FIRST_PACKET = 7,    /* first RTP packet of the frame handed to the socket */
  SP_STAGE_LAST_PACKET = 8,     /* last RTP/FEC packet of the frame handed to the socket */
  SP_STAGE_INPUT_RECV = 20,     /* input packet received from Moonlight (frame_id = input seq) */
  SP_STAGE_INPUT_INJECT = 21    /* input injected (SendInput or RDP PDU) */
} SpTraceStage;

typedef struct SpTraceRecord {
  uint64_t frame_id;
  uint64_t qpc;
  uint32_t stage;               /* SpTraceStage */
  uint32_t desk_hash;           /* FNV-1a of the desk id */
} SpTraceRecord;

static inline uint32_t sp_desk_hash(const char *desk_id) {
  uint32_t h = 2166136261u;
  for (; desk_id && *desk_id; ++desk_id) {
    h ^= (uint8_t)*desk_id;
    h *= 16777619u;
  }
  return h;
}

#ifdef __cplusplus
} /* extern "C" */
#endif
