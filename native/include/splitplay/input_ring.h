// SPDX-License-Identifier: MIT
// Input ring: the contract between the desk worker (producer) and splitplay-seat (consumer).
//
// Used by the "RDP input sink" (ARCHITECTURE.md §9): the worker forwards Moonlight keyboard/mouse input to the seat,
// which sends it into the desk session as genuine RDP input PDUs. The MVP uses SendInput and does not need the ring,
// but the layout is fixed now so both sides can be built against it.
//
// The seat (consumer) creates:
//   - a shared-memory section  Global\SplitPlay.InputRing.<desk-id>   holding SpInputRing
//   - an auto-reset event      Global\SplitPlay.InputReady.<desk-id>  signalled by the producer after writing
// Single producer, single consumer. write_index / read_index increase forever; slot = index % SP_INPUT_RING_CAPACITY.
// The producer drops events (and increments dropped) when the ring is full; it never blocks.
#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define SP_INPUT_RING_MAGIC    0x52495053u /* 'SPIR' */
#define SP_INPUT_RING_VERSION  1u
#define SP_INPUT_RING_CAPACITY 1024u      /* power of two */

typedef enum SpInputType {
  SP_IN_KEY = 1,        /* a = scancode (set 1), b = flags: bit0 up, bit1 extended (E0), bit2 E1 */
  SP_IN_UNICODE = 2,    /* a = UTF-32 code point, b = bit0 up */
  SP_IN_MOUSE_REL = 3,  /* a = dx, b = dy (signed) */
  SP_IN_MOUSE_ABS = 4,  /* a = x, b = y in desktop pixels */
  SP_IN_BUTTON = 5,     /* a = button (1 L, 2 R, 3 M, 4 X1, 5 X2), b = bit0 up */
  SP_IN_WHEEL = 6,      /* a = delta (120 = one notch, signed) */
  SP_IN_HWHEEL = 7,     /* a = delta (signed) */
  SP_IN_SAS = 8,        /* secure attention sequence (Ctrl+Alt+Del) */
  SP_IN_RELEASE_ALL = 9 /* client disconnected or lost focus: release all held keys and buttons */
} SpInputType;

typedef struct SpInputEvent {
  uint64_t qpc;         /* QPC when the worker received the Moonlight packet (latency tracing) */
  uint32_t type;        /* SpInputType */
  int32_t  a;
  int32_t  b;
  uint32_t seq;         /* producer sequence number */
  uint32_t reserved[2];
} SpInputEvent;

typedef struct SpInputRing {
  uint32_t magic;       /* SP_INPUT_RING_MAGIC */
  uint32_t version;     /* SP_INPUT_RING_VERSION */
  uint32_t capacity;    /* SP_INPUT_RING_CAPACITY */
  uint32_t flags;       /* bit0: consumer supports relative mouse PDUs (else the producer keeps relative motion on SendInput) */
  volatile uint64_t write_index;  /* written by producer */
  volatile uint64_t read_index;   /* written by consumer */
  volatile uint64_t dropped;
  volatile uint32_t producer_pid;
  volatile uint32_t consumer_pid;
  uint32_t reserved[8];
  SpInputEvent events[SP_INPUT_RING_CAPACITY];
} SpInputRing;

#ifdef __cplusplus
} /* extern "C" */
static_assert(sizeof(SpInputEvent) == 32, "SpInputEvent layout");
#endif
