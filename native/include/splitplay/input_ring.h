// SPDX-License-Identifier: MIT
// Input ring: the contract between the desk worker (producer) and splitplay-seat (consumer).
//
// Used by the "RDP input sink" (ARCHITECTURE.md §9, profile `input: "rdp"`): the worker forwards Moonlight keyboard
// and mouse input to the seat, which sends it into the desk session as genuine RDP input PDUs over its loopback
// connection (no "injected" flag, UAC prompts and Ctrl+Alt+Del work). With `input: "windows"` (the default) the
// worker keeps SendInput and leaves the ring alone.
//
// The seat (consumer) creates:
//   - a shared-memory section  Global\SplitPlay.InputRing.<desk-id>   holding SpInputRing
//   - an auto-reset event      Global\SplitPlay.InputReady.<desk-id>  signalled by the producer after writing
// Single producer, single consumer. write_index / read_index increase forever; slot = index % SP_INPUT_RING_CAPACITY.
// The producer drops events (and increments dropped) when the ring is full; it never blocks.
//
// A seat that restarts while a worker still maps the section reopens the same section: it keeps write_index, sets
// read_index to it (queued events are dropped) and rewrites flags and consumer_pid, so the producer carries on.
// The producer reads `flags` before every event: it only uses the ring while SP_INPUT_RING_READY is set (the seat's
// RDP connection is logged on and can send input), keeps relative mouse motion on SendInput unless
// SP_INPUT_RING_REL_MOUSE is set, and X buttons and the horizontal wheel unless their flags are set.
#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define SP_INPUT_RING_MAGIC    0x52495053u /* 'SPIR' */
#define SP_INPUT_RING_VERSION  1u
#define SP_INPUT_RING_CAPACITY 1024u      /* power of two */

/* SpInputRing::flags, written by the consumer (seat) at any time. */
#define SP_INPUT_RING_REL_MOUSE 0x1u  /* the RDP server accepts relative mouse PDUs (TS_FP_RELPOINTER_EVENT) */
#define SP_INPUT_RING_READY     0x2u  /* the seat's RDP connection is logged on and sends what it reads */
#define SP_INPUT_RING_XBUTTONS  0x4u  /* the server takes X1/X2 buttons (INPUT_FLAG_MOUSEX) */
#define SP_INPUT_RING_HWHEEL    0x8u  /* the server takes the horizontal wheel (TS_INPUT_FLAG_MOUSE_HWHEEL) */

/* Bits of SpInputEvent::b. */
#define SP_IN_FLAG_UP       0x1u  /* KEY, UNICODE, BUTTON: release */
#define SP_IN_FLAG_EXTENDED 0x2u  /* KEY: E0 prefix */
#define SP_IN_FLAG_E1       0x4u  /* KEY: E1 prefix (Pause) */
#define SP_IN_FLAG_POS      0x8u  /* BUTTON, WHEEL, HWHEEL: x, y hold the pointer position in desktop pixels (the
                                     worker sends none: every motion it puts in the ring is the newest position) */

typedef enum SpInputType {
  SP_IN_KEY = 1,        /* a = scancode (set 1), b = SP_IN_FLAG_UP | SP_IN_FLAG_EXTENDED | SP_IN_FLAG_E1 */
  SP_IN_UNICODE = 2,    /* a = UTF-32 code point, b = SP_IN_FLAG_UP */
  SP_IN_MOUSE_REL = 3,  /* a = dx, b = dy (signed) */
  SP_IN_MOUSE_ABS = 4,  /* a = x, b = y in desktop pixels (0,0 = top left of the session's desktop) */
  SP_IN_BUTTON = 5,     /* a = button (1 L, 2 R, 3 M, 4 X1, 5 X2), b = SP_IN_FLAG_UP | SP_IN_FLAG_POS */
  SP_IN_WHEEL = 6,      /* a = delta (120 = one notch, signed, positive = away from the user), b = SP_IN_FLAG_POS */
  SP_IN_HWHEEL = 7,     /* a = delta (signed, positive = right), b = SP_IN_FLAG_POS */
  SP_IN_SAS = 8,        /* secure attention sequence (Ctrl+Alt+Del) */
  SP_IN_RELEASE_ALL = 9 /* client disconnected or lost focus: release all held keys and buttons */
} SpInputType;

typedef struct SpInputEvent {
  uint64_t qpc;         /* QPC when the worker received the Moonlight packet (latency tracing) */
  uint32_t type;        /* SpInputType */
  int32_t  a;
  int32_t  b;
  uint32_t seq;         /* producer sequence number */
  int32_t  x;           /* BUTTON, WHEEL, HWHEEL with SP_IN_FLAG_POS: pointer position (RDP button and wheel PDUs */
  int32_t  y;           /* carry one; without it the seat uses the last position it sent) */
} SpInputEvent;

typedef struct SpInputRing {
  uint32_t magic;       /* SP_INPUT_RING_MAGIC */
  uint32_t version;     /* SP_INPUT_RING_VERSION */
  uint32_t capacity;    /* SP_INPUT_RING_CAPACITY */
  volatile uint32_t flags;  /* SP_INPUT_RING_REL_MOUSE | SP_INPUT_RING_READY, written by the consumer */
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
