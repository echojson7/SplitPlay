// SPDX-License-Identifier: MIT
// Control interface of the SplitPlay remote-session indirect display driver (SplitPlayIdd).
//
// Who talks to it: SplitPlay.Service (session 0, LocalSystem). The worker never opens the IDD; it only
// consumes the capture ring (native/include/splitplay/capture_ring.h) that the IDD produces.
//
// How to open it: enumerate device interfaces of SP_GUID_DEVINTERFACE_IDD (CM_Get_Device_Interface_ListW),
// one interface per remote display adapter, i.e. one per desk session. SP_IOCTL_IDD_GET_INFO returns the
// Windows session id that the instance serves, which is how the service maps an interface to a desk.
//
// Why the service must call SP_IOCTL_IDD_ATTACH_DESK: the capture-ring kernel objects are named after the
// desk id (Global\SplitPlay.CaptureRing.<desk-id>), but the driver only knows its session id. Until a desk
// is attached the driver acquires and counts frames but publishes nothing.
//
// All structures are little-endian, naturally aligned, identical on x64 and ARM64. Every input/output
// structure starts with `size` (= sizeof the structure) so that fields can be appended later.
#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define SP_IDD_IOCTL_VERSION 1u

/* Hardware id the protocol provider returns from IWRdsWddmIddProps::GetHardwareId; matches SplitPlayIdd.inf. */
#define SP_IDD_HARDWARE_ID "SplitPlay_RemoteIdd"

/* {A6FC08D4-886A-4F4C-BE08-997FC7CB75E5} */
#define SP_GUID_DEVINTERFACE_IDD_INIT \
  { 0xa6fc08d4, 0x886a, 0x4f4c, { 0xbe, 0x08, 0x99, 0x7f, 0xc7, 0xcb, 0x75, 0xe5 } }

/* CTL_CODE without pulling in winioctl.h, so that the header also compiles in portable unit tests. */
#define SP_IDD_DEVICE_TYPE 0x9A10u
#define SP_IDD_CTL_CODE(function, access) \
  ((uint32_t)(((SP_IDD_DEVICE_TYPE) << 16) | ((uint32_t)(access) << 14) | ((uint32_t)(function) << 2) | 0u /* METHOD_BUFFERED */))
#define SP_IDD_ACCESS_ANY   0u /* FILE_ANY_ACCESS */
#define SP_IDD_ACCESS_WRITE 2u /* FILE_WRITE_ACCESS */

/* out: SpIddInfo */
#define SP_IOCTL_IDD_GET_INFO            SP_IDD_CTL_CODE(0x800, SP_IDD_ACCESS_ANY)
/* in: SpIddAttachDesk. Names the capture ring; may be called again after a worker restart. */
#define SP_IOCTL_IDD_ATTACH_DESK         SP_IDD_CTL_CODE(0x801, SP_IDD_ACCESS_WRITE)
/* in: SpIddSetMode. Applies the Moonlight-requested mode through IddCxAdapterDisplayConfigUpdate. */
#define SP_IOCTL_IDD_SET_MODE            SP_IDD_CTL_CODE(0x802, SP_IDD_ACCESS_WRITE)
/* in: SpIddSetRenderAdapter. Preferred render GPU; takes effect for the next swap chain. */
#define SP_IOCTL_IDD_SET_RENDER_ADAPTER  SP_IDD_CTL_CODE(0x803, SP_IDD_ACCESS_WRITE)

#define SP_IDD_SESSION_UNKNOWN 0xFFFFFFFFu
#define SP_IDD_DESK_ID_MAX     32u /* [a-z0-9-]{1,32}, see docs/contracts/README.md */

typedef enum SpIddSessionSource {
  SP_IDD_SESSION_SOURCE_NONE = 0,        /* not known yet */
  SP_IDD_SESSION_SOURCE_INSTANCE_ID = 1, /* parsed from "...&SessionId_000N" in the device instance id */
  SP_IDD_SESSION_SOURCE_DEVPKEY = 2      /* DEVPKEY_Device_SessionId of the devnode */
} SpIddSessionSource;

typedef enum SpIddStateFlags {
  SP_IDD_STATE_ADAPTER_READY   = 0x1,   /* EvtIddCxAdapterInitFinished succeeded */
  SP_IDD_STATE_MONITOR_ARRIVED = 0x2,   /* IddCxMonitorArrival succeeded */
  SP_IDD_STATE_SWAPCHAIN       = 0x4,   /* a swap chain is assigned and the processing thread runs */
  SP_IDD_STATE_DESK_ATTACHED   = 0x8,   /* SP_IOCTL_IDD_ATTACH_DESK received */
  SP_IDD_STATE_RING_OPEN       = 0x10   /* capture ring created (Stage 3) */
} SpIddStateFlags;

typedef struct SpIddInfo {
  uint32_t size;               /* sizeof(SpIddInfo) */
  uint32_t version;            /* SP_IDD_IOCTL_VERSION */
  uint32_t session_id;         /* SP_IDD_SESSION_UNKNOWN if not derived */
  uint32_t session_source;     /* SpIddSessionSource */
  uint32_t state;              /* SpIddStateFlags */
  uint32_t iddcx_version;      /* IddCxGetVersion(), e.g. 0x1A80 */
  uint32_t width, height;      /* committed mode, 0 if none */
  uint32_t refresh_num, refresh_den;
  uint64_t frames_acquired;    /* buffers acquired from the swap chain since the driver loaded */
  uint64_t frames_unchanged;   /* acquired buffers with no dirty rects and no move regions */
  uint64_t last_present_qpc;   /* IDDCX_METADATA.PresentDisplayQPCTime of the newest frame */
  uint64_t last_acquire_qpc;   /* QPC when the newest frame was acquired */
  uint64_t render_adapter_luid;/* LowPart | (uint64_t)HighPart << 32 of the current swap chain */
  char     desk_id[SP_IDD_DESK_ID_MAX + 1]; /* NUL-terminated, empty if not attached */
  uint8_t  reserved0[7];
  uint32_t reserved[8];
} SpIddInfo;

typedef struct SpIddAttachDesk {
  uint32_t size;               /* sizeof(SpIddAttachDesk) */
  uint32_t session_id;         /* must equal the driver's session id unless that is SP_IDD_SESSION_UNKNOWN */
  char     desk_id[SP_IDD_DESK_ID_MAX + 1];
  uint8_t  reserved0[3];
} SpIddAttachDesk;

typedef struct SpIddSetMode {
  uint32_t size;               /* sizeof(SpIddSetMode) */
  uint32_t width, height;
  uint32_t refresh_num, refresh_den; /* e.g. 500/1 */
  uint32_t reserved[3];
} SpIddSetMode;

typedef struct SpIddSetRenderAdapter {
  uint32_t size;               /* sizeof(SpIddSetRenderAdapter) */
  uint32_t luid_low;
  int32_t  luid_high;
  uint32_t reserved;
} SpIddSetRenderAdapter;

#ifdef __cplusplus
} /* extern "C" */
static_assert(sizeof(SpIddInfo) == 152, "SpIddInfo layout");
static_assert(sizeof(SpIddAttachDesk) == 44, "SpIddAttachDesk layout");
static_assert(sizeof(SpIddSetMode) == 32, "SpIddSetMode layout");
static_assert(sizeof(SpIddSetRenderAdapter) == 16, "SpIddSetRenderAdapter layout");
#endif
