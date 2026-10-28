// SPDX-License-Identifier: MIT
// Control interface of the SplitPlay virtual gamepad driver (SplitPlayPad, UMDF2 + VHF).
//
// Who talks to it: the desk worker (splitplay-worker, SYSTEM token in the desk session). The worker is GPL-3.0
// and may include this MIT header; it talks to the driver only through these IOCTLs.
//
// Model: one root-enumerated SplitPlayPad device ("ROOT\SplitPlayPad", created by the installer/service) is the
// VHF parent. Every SP_IOCTL_PAD_CREATE makes VHF enumerate one HID child (a DS4-compatible gamepad). Pads are
// owned by the handle that created them: closing the handle (including a worker crash) removes them, so no
// "ghost" controllers are left behind (ARCHITECTURE.md section 2, SplitPlay Pad).
//
// Session isolation: the driver does NOT assign pads to sessions. After SP_IOCTL_PAD_CREATE the worker reports
// SpPadCreateOut.instance_id to the service, which locates the HID child devnode and stamps
// DEVPKEY_Device_SessionId = <desk session id> on it (HidHide per-session rules as backstop).
//
// All structures are little-endian, naturally aligned, identical on x64 and ARM64; each starts with `size`.
#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define SP_PAD_IOCTL_VERSION 1u

/* Hardware id of the root-enumerated parent device the installer creates; matches SplitPlayPad.inf. */
#define SP_PAD_HARDWARE_ID "ROOT\\SplitPlayPad"

/* {C4C04CE7-A979-4204-93B5-D49762025C52} */
#define SP_GUID_DEVINTERFACE_PAD_INIT \
  { 0xc4c04ce7, 0xa979, 0x4204, { 0x93, 0xb5, 0xd4, 0x97, 0x62, 0x02, 0x5c, 0x52 } }

#define SP_PAD_DEVICE_TYPE 0x9A11u
#define SP_PAD_CTL_CODE(function, access) \
  ((uint32_t)(((SP_PAD_DEVICE_TYPE) << 16) | ((uint32_t)(access) << 14) | ((uint32_t)(function) << 2) | 0u /* METHOD_BUFFERED */))
#define SP_PAD_ACCESS_ANY   0u /* FILE_ANY_ACCESS */
#define SP_PAD_ACCESS_WRITE 2u /* FILE_WRITE_ACCESS */

/* in: SpPadCreateIn, out: SpPadCreateOut */
#define SP_IOCTL_PAD_CREATE        SP_PAD_CTL_CODE(0x800, SP_PAD_ACCESS_WRITE)
/* in: SpPadSubmitIn. Hot path: one call per input report (up to the client poll rate, e.g. 1000/s). */
#define SP_IOCTL_PAD_SUBMIT        SP_PAD_CTL_CODE(0x801, SP_PAD_ACCESS_WRITE)
/* in: SpPadDestroyIn */
#define SP_IOCTL_PAD_DESTROY       SP_PAD_CTL_CODE(0x802, SP_PAD_ACCESS_WRITE)
/* in: SpPadFeedbackIn, out: SpPadFeedbackOut. Latest output report (rumble/lightbar) written by a game. */
#define SP_IOCTL_PAD_GET_FEEDBACK  SP_PAD_CTL_CODE(0x803, SP_PAD_ACCESS_ANY)

#define SP_PAD_MAX_PADS        16u
#define SP_PAD_MAX_REPORT      64u /* DS4 USB input report, including the report id byte */
#define SP_PAD_INSTANCE_ID_MAX 48u /* UTF-16 code units incl. terminator */

typedef enum SpPadProfile {
  SP_PAD_PROFILE_DS4 = 1          /* Sony DualShock 4 (USB, VID 054C PID 09CC), report layout in pad/src/Ds4.h */
  /* Stage 4: DualSense, Xbox Series (xinputhid), Switch Pro */
} SpPadProfile;

typedef struct SpPadCreateIn {
  uint32_t size;                  /* sizeof(SpPadCreateIn) */
  uint32_t version;               /* SP_PAD_IOCTL_VERSION */
  uint32_t profile;               /* SpPadProfile */
  uint32_t flags;                 /* reserved, 0 */
  uint8_t  mac[6];                /* reported through the DS4 pairing feature report; 0 = derived from the slot */
  uint8_t  reserved0[2];
  uint32_t reserved[4];
} SpPadCreateIn;

typedef struct SpPadCreateOut {
  uint32_t size;                  /* sizeof(SpPadCreateOut) */
  uint32_t pad_id;                /* handle for SUBMIT/DESTROY/GET_FEEDBACK; never 0 */
  uint16_t instance_id[SP_PAD_INSTANCE_ID_MAX]; /* VHF instance id of the HID child, NUL-terminated UTF-16;
                                                   the service uses it to stamp DEVPKEY_Device_SessionId */
} SpPadCreateOut;

typedef struct SpPadSubmitIn {
  uint32_t size;                  /* sizeof(SpPadSubmitIn) */
  uint32_t pad_id;
  uint32_t length;                /* bytes used in report[], including the report id in report[0] */
  uint32_t reserved;
  uint8_t  report[SP_PAD_MAX_REPORT];
} SpPadSubmitIn;

typedef struct SpPadDestroyIn {
  uint32_t size;                  /* sizeof(SpPadDestroyIn) */
  uint32_t pad_id;
} SpPadDestroyIn;

typedef struct SpPadFeedbackIn {
  uint32_t size;                  /* sizeof(SpPadFeedbackIn) */
  uint32_t pad_id;
} SpPadFeedbackIn;

typedef struct SpPadFeedbackOut {
  uint32_t size;                  /* sizeof(SpPadFeedbackOut) */
  uint32_t pad_id;
  uint32_t sequence;              /* increments on every output report; 0 = none received yet */
  uint32_t length;                /* bytes used in report[], including the report id */
  uint8_t  report[SP_PAD_MAX_REPORT];
} SpPadFeedbackOut;

#ifdef __cplusplus
} /* extern "C" */
static_assert(sizeof(SpPadCreateIn) == 40, "SpPadCreateIn layout");
static_assert(sizeof(SpPadCreateOut) == 104, "SpPadCreateOut layout");
static_assert(sizeof(SpPadSubmitIn) == 80, "SpPadSubmitIn layout");
static_assert(sizeof(SpPadDestroyIn) == 8, "SpPadDestroyIn layout");
static_assert(sizeof(SpPadFeedbackIn) == 8, "SpPadFeedbackIn layout");
static_assert(sizeof(SpPadFeedbackOut) == 80, "SpPadFeedbackOut layout");
#endif
