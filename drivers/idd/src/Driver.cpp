// SPDX-License-Identifier: MIT
// SplitPlayIdd: driver entry, device setup and all IddCx callbacks except swap-chain processing.
//
// Structure follows Microsoft's IddSampleDriver (Windows-driver-samples/video/IndirectDisplay); the code is
// written from scratch for SplitPlay. Differences from the sample that matter:
//   - remote-session driver (IDDCX_ADAPTER_FLAGS_REMOTE_SESSION_DRIVER), no EDID, no console monitors;
//   - built against IddCx 1.10 headers with IDDCX_MINIMUM_VERSION_REQUIRED = 4, i.e. loads on IddCx 1.4+;
//   - modes up to 500 Hz; the service picks the active mode via SP_IOCTL_IDD_SET_MODE;
//   - the session id comes from the device instance id (docs/research/06-review.md, finding iv).

#include "Driver.h"

#include <devpkey.h>
#include <TraceLoggingProvider.h>

#include "ModeList.h"
#include "SessionId.h"
#include "Trace.h"

// Shared "SplitPlay" provider, GUID fixed by native/include/splitplay/trace.h.
TRACELOGGING_DEFINE_PROVIDER(g_SpTraceProvider, SP_TRACE_PROVIDER_NAME,
                             (0x6b2e3f5a, 0x9c41, 0x4d7b, 0xa1, 0xe2, 0x53, 0x50, 0x4c, 0x41, 0x59, 0x31));

static const GUID kSpIddInterfaceGuid = SP_GUID_DEVINTERFACE_IDD_INIT;

// Base for the monitor container id; the last four bytes carry the session id so that the container is
// stable for a session but distinct between sessions. {FDC5F7C8-00E4-4AA3-A843-C32631C99636}
static const GUID kSpIddMonitorContainerBase = {0xfdc5f7c8, 0x00e4, 0x4aa3, {0xa8, 0x43, 0xc3, 0x26, 0x31, 0xc9, 0x96, 0x36}};

// The adapter exposes exactly one monitor: one desk = one session = one display.
static constexpr UINT kSpIddMonitorCount = 1;

// Nominal physical size reported for the EDID-less monitor (24" 16:9), in millimetres.
static constexpr UINT kSpIddPhysicalWidthMm = 527;
static constexpr UINT kSpIddPhysicalHeightMm = 296;

extern "C" DRIVER_INITIALIZE DriverEntry;
static EVT_WDF_DRIVER_DEVICE_ADD SpIddEvtDeviceAdd;
static EVT_WDF_DRIVER_UNLOAD SpIddEvtDriverUnload;
static EVT_WDF_DEVICE_D0_ENTRY SpIddEvtDeviceD0Entry;
static EVT_WDF_OBJECT_CONTEXT_CLEANUP SpIddEvtDeviceCleanup;

static EVT_IDD_CX_DEVICE_IO_CONTROL SpIddEvtDeviceIoControl;
static EVT_IDD_CX_ADAPTER_INIT_FINISHED SpIddEvtAdapterInitFinished;
static EVT_IDD_CX_ADAPTER_COMMIT_MODES SpIddEvtAdapterCommitModes;
static EVT_IDD_CX_PARSE_MONITOR_DESCRIPTION SpIddEvtParseMonitorDescription;
static EVT_IDD_CX_MONITOR_GET_DEFAULT_DESCRIPTION_MODES SpIddEvtMonitorGetDefaultModes;
static EVT_IDD_CX_MONITOR_QUERY_TARGET_MODES SpIddEvtMonitorQueryModes;
static EVT_IDD_CX_MONITOR_ASSIGN_SWAPCHAIN SpIddEvtMonitorAssignSwapChain;
static EVT_IDD_CX_MONITOR_UNASSIGN_SWAPCHAIN SpIddEvtMonitorUnassignSwapChain;
static EVT_IDD_CX_MONITOR_GET_PHYSICAL_SIZE SpIddEvtMonitorGetPhysicalSize;

// ---------------------------------------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------------------------------------

// Fills a progressive DISPLAYCONFIG_VIDEO_SIGNAL_INFO. Monitor modes use vSyncFreqDivider 0, target modes 1
// (IDDCX_TARGET_MODE: "vSyncFreqDivider cannot be zero"; the desktop update rate is vSyncFreq / divider).
static void SpIddFillSignalInfo(DISPLAYCONFIG_VIDEO_SIGNAL_INFO* info, const splitplay::idd::Mode& mode,
                                bool monitorMode) {
  RtlZeroMemory(info, sizeof(*info));
  info->totalSize.cx = info->activeSize.cx = mode.width;
  info->totalSize.cy = info->activeSize.cy = mode.height;
  info->AdditionalSignalInfo.videoStandard = 255; // D3DKMDT_VSS_OTHER
  info->AdditionalSignalInfo.vSyncFreqDivider = monitorMode ? 0 : 1;
  info->vSyncFreq.Numerator = mode.refreshHz;
  info->vSyncFreq.Denominator = 1;
  info->hSyncFreq.Numerator = mode.refreshHz * mode.height;
  info->hSyncFreq.Denominator = 1;
  info->scanLineOrdering = DISPLAYCONFIG_SCANLINE_ORDERING_PROGRESSIVE;
  info->pixelRate = static_cast<UINT64>(mode.refreshHz) * mode.width * mode.height;
}

static void SpIddFillMonitorModes(IDDCX_MONITOR_MODE* modes) {
  for (UINT i = 0; i < splitplay::idd::kModeCount; ++i) {
    modes[i].Size = IDD_STRUCTURE_SIZE(IDDCX_MONITOR_MODE);
    modes[i].Origin = IDDCX_MONITOR_MODE_ORIGIN_DRIVER;
    SpIddFillSignalInfo(&modes[i].MonitorVideoSignalInfo, splitplay::idd::ModeAt(i), true);
  }
}

// Derives the Windows session id this adapter instance serves. Primary source is the device instance id
// ("...&SessionId_000N"); DEVPKEY_Device_SessionId is the fallback. Runs in EvtDeviceAdd, before the device
// object exists, so it queries through the WDFDEVICE_INIT.
static void SpIddDeriveSessionId(PWDFDEVICE_INIT deviceInit, uint32_t* sessionId, uint32_t* source) {
  *sessionId = SP_IDD_SESSION_UNKNOWN;
  *source = SP_IDD_SESSION_SOURCE_NONE;

  WDF_DEVICE_PROPERTY_DATA property;
  WDFMEMORY memory = nullptr;
  DEVPROPTYPE type = DEVPROP_TYPE_EMPTY;

  WDF_DEVICE_PROPERTY_DATA_INIT(&property, &DEVPKEY_Device_InstanceId);
  NTSTATUS status = WdfFdoInitAllocAndQueryPropertyEx(deviceInit, &property, PagedPool, WDF_NO_OBJECT_ATTRIBUTES,
                                                      &memory, &type);
  if (NT_SUCCESS(status)) {
    size_t bytes = 0;
    const wchar_t* instanceId = static_cast<const wchar_t*>(WdfMemoryGetBuffer(memory, &bytes));
    uint32_t parsed = SP_IDD_SESSION_UNKNOWN;
    // The buffer is a NUL-terminated DEVPROP_TYPE_STRING; refuse anything else.
    if (type == DEVPROP_TYPE_STRING && bytes >= sizeof(wchar_t) && instanceId[bytes / sizeof(wchar_t) - 1] == L'\0' &&
        splitplay::idd::ParseSessionIdFromInstanceId(instanceId, &parsed)) {
      *sessionId = parsed;
      *source = SP_IDD_SESSION_SOURCE_INSTANCE_ID;
    }
    WdfObjectDelete(memory);
    memory = nullptr;
  }
  if (*source != SP_IDD_SESSION_SOURCE_NONE) {
    return;
  }

  ULONG value = 0;
  ULONG required = 0;
  WDF_DEVICE_PROPERTY_DATA_INIT(&property, &DEVPKEY_Device_SessionId);
  status = WdfFdoInitQueryPropertyEx(deviceInit, &property, sizeof(value), &value, &required, &type);
  if (NT_SUCCESS(status) && type == DEVPROP_TYPE_UINT32) {
    *sessionId = value;
    *source = SP_IDD_SESSION_SOURCE_DEVPKEY;
  }
}

// Asks IddCx to apply `mode` to our monitor. Remote-session drivers set the session's display configuration
// themselves (ChangeDisplaySettings is ignored for WDDM remote sessions, docs/research/01).
static NTSTATUS SpIddApplyDisplayConfig(SpIddDeviceContext* ctx, const SpIddSetMode& mode) {
  if (ctx->adapter == nullptr || ctx->monitor == nullptr) {
    return STATUS_DEVICE_NOT_READY;
  }
  IDDCX_DISPLAYCONFIGPATH path = {};
  path.Size = IDD_STRUCTURE_SIZE(IDDCX_DISPLAYCONFIGPATH);
  path.MonitorObject = ctx->monitor;
  path.Position.x = 0;
  path.Position.y = 0;
  path.Resolution.cx = mode.width;
  path.Resolution.cy = mode.height;
  path.Rotation = DISPLAYCONFIG_ROTATION_IDENTITY;
  path.RefreshRate.Numerator = mode.refresh_num;
  path.RefreshRate.Denominator = mode.refresh_den;
  path.VSyncFreqDivider = 1;
  path.MonitorScaleFactor = 100; // DPI hint, 100..500; the seat's DesktopScaleFactor wins in practice
  path.PhysicalWidthOverride = 0;
  path.PhysicalHeightOverride = 0;

  IDARG_IN_ADAPTERDISPLAYCONFIGUPDATE args = {};
  args.PathCount = 1;
  args.pPaths = &path;
  const NTSTATUS status = IddCxAdapterDisplayConfigUpdate(ctx->adapter, &args);
  SP_IDD_LOG("IddDisplayConfigUpdate", status);
  return status;
}

// ---------------------------------------------------------------------------------------------------------
// WDF driver and device callbacks
// ---------------------------------------------------------------------------------------------------------

extern "C" NTSTATUS DriverEntry(PDRIVER_OBJECT driverObject, PUNICODE_STRING registryPath) {
  TraceLoggingRegister(g_SpTraceProvider);

  WDF_DRIVER_CONFIG config;
  WDF_DRIVER_CONFIG_INIT(&config, SpIddEvtDeviceAdd);
  config.EvtDriverUnload = SpIddEvtDriverUnload;

  const NTSTATUS status = WdfDriverCreate(driverObject, registryPath, WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
  SP_IDD_LOG("IddDriverEntry", status);
  if (!NT_SUCCESS(status)) {
    TraceLoggingUnregister(g_SpTraceProvider);
  }
  return status;
}

static void SpIddEvtDriverUnload(WDFDRIVER driver) {
  UNREFERENCED_PARAMETER(driver);
  TraceLoggingUnregister(g_SpTraceProvider);
}

static NTSTATUS SpIddEvtDeviceAdd(WDFDRIVER driver, PWDFDEVICE_INIT deviceInit) {
  UNREFERENCED_PARAMETER(driver);

  uint32_t sessionId = SP_IDD_SESSION_UNKNOWN;
  uint32_t sessionSource = SP_IDD_SESSION_SOURCE_NONE;
  SpIddDeriveSessionId(deviceInit, &sessionId, &sessionSource);

  WDF_PNPPOWER_EVENT_CALLBACKS pnp;
  WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&pnp);
  pnp.EvtDeviceD0Entry = SpIddEvtDeviceD0Entry;
  WdfDeviceInitSetPnpPowerEventCallbacks(deviceInit, &pnp);

  IDD_CX_CLIENT_CONFIG iddConfig;
  IDD_CX_CLIENT_CONFIG_INIT(&iddConfig);
  iddConfig.EvtIddCxDeviceIoControl = SpIddEvtDeviceIoControl;
  iddConfig.EvtIddCxAdapterInitFinished = SpIddEvtAdapterInitFinished;
  iddConfig.EvtIddCxAdapterCommitModes = SpIddEvtAdapterCommitModes;
  iddConfig.EvtIddCxParseMonitorDescription = SpIddEvtParseMonitorDescription;
  iddConfig.EvtIddCxMonitorGetDefaultDescriptionModes = SpIddEvtMonitorGetDefaultModes;
  iddConfig.EvtIddCxMonitorQueryTargetModes = SpIddEvtMonitorQueryModes;
  iddConfig.EvtIddCxMonitorAssignSwapChain = SpIddEvtMonitorAssignSwapChain;
  iddConfig.EvtIddCxMonitorUnassignSwapChain = SpIddEvtMonitorUnassignSwapChain;
  // Only called for remote drivers whose monitor has no description (our case: no EDID).
  iddConfig.EvtIddCxMonitorGetPhysicalSize = SpIddEvtMonitorGetPhysicalSize;
  // Not implemented on purpose: I2C, gamma (GammaSupport = NONE), OPM, and every IddCx 1.10 "*2" callback.
  // HDR (IDDCX_ADAPTER_FLAGS_CAN_PROCESS_FP16) needs the 1.10 set and comes with the HDR work in Stage 4.

  NTSTATUS status = IddCxDeviceInitConfig(deviceInit, &iddConfig);
  if (!NT_SUCCESS(status)) {
    SP_IDD_LOG("IddDeviceInitConfig", status);
    return status;
  }

  WDF_OBJECT_ATTRIBUTES attributes;
  WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, SpIddDeviceContext);
  attributes.EvtCleanupCallback = SpIddEvtDeviceCleanup;

  WDFDEVICE device = nullptr;
  status = WdfDeviceCreate(&deviceInit, &attributes, &device);
  if (!NT_SUCCESS(status)) {
    SP_IDD_LOG("IddDeviceCreate", status);
    return status;
  }

  SpIddDeviceContext* ctx = SpIddGetDeviceContext(device);
  ctx->device = device;
  ctx->sessionId = sessionId;
  ctx->sessionSource = sessionSource;

  status = WdfWaitLockCreate(WDF_NO_OBJECT_ATTRIBUTES, &ctx->lock);
  if (!NT_SUCCESS(status)) {
    return status;
  }

  // The service finds this adapter instance through the interface and maps it to a desk via GET_INFO.
  status = WdfDeviceCreateDeviceInterface(device, &kSpIddInterfaceGuid, nullptr);
  if (!NT_SUCCESS(status)) {
    SP_IDD_LOG("IddCreateDeviceInterface", status);
    return status;
  }

  status = IddCxDeviceInitialize(device);
  TraceLoggingWrite(g_SpTraceProvider, "IddDeviceAdd", TraceLoggingLevel(WINEVENT_LEVEL_INFO),
                    TraceLoggingHexInt32(status, "status"), TraceLoggingUInt32(sessionId, "session_id"),
                    TraceLoggingUInt32(sessionSource, "session_source"));
  return status;
}

static void SpIddEvtDeviceCleanup(WDFOBJECT object) {
  SpIddDeviceContext* ctx = SpIddGetDeviceContext(object);
  // The swap chain is normally unassigned before the device goes away; this is the backstop.
  SpIddSwapChainProcessorStop(ctx->processor);
  ctx->processor = nullptr;
}

// D0 entry: the device is powered and IddCx is ready, so this is where the adapter is created.
static NTSTATUS SpIddEvtDeviceD0Entry(WDFDEVICE device, WDF_POWER_DEVICE_STATE previousState) {
  UNREFERENCED_PARAMETER(previousState);
  SpIddDeviceContext* ctx = SpIddGetDeviceContext(device);
  if (ctx->adapter != nullptr) {
    return STATUS_SUCCESS; // D0 re-entry after a low-power state: the adapter survives
  }

  IDARG_OUT_GETVERSION version = {};
  if (NT_SUCCESS(IddCxGetVersion(&version))) {
    ctx->iddCxVersion = version.IddCxVersion;
  }

  IDDCX_ENDPOINT_VERSION endpointVersion = {};
  endpointVersion.Size = IDD_STRUCTURE_SIZE(IDDCX_ENDPOINT_VERSION);
  endpointVersion.MajorVer = 0;
  endpointVersion.MinorVer = 1;
  endpointVersion.Build = 0;

  IDDCX_ADAPTER_CAPS caps = {};
  caps.Size = IDD_STRUCTURE_SIZE(IDDCX_ADAPTER_CAPS);
  // REMOTE_SESSION_DRIVER: this adapter serves monitors of one remote session, never the console.
  //   IddCxAdapterInitAsync fails unless the OS remote-desktop stack created the devnode for a session.
  // USE_SMALLEST_MODE: required for remote drivers (desktop surface = exactly the active mode).
  caps.Flags = static_cast<IDDCX_ADAPTER_FLAGS>(IDDCX_ADAPTER_FLAGS_REMOTE_SESSION_DRIVER |
                                                IDDCX_ADAPTER_FLAGS_USE_SMALLEST_MODE);
  caps.MaxMonitorsSupported = kSpIddMonitorCount;
  caps.MaxDisplayPipelineRate = 0;           // no bandwidth budget: the encoder, not a link, is the limit
  caps.StaticDesktopReencodeFrameCount = 0;  // the worker repeats frames itself (capture ring, encode-on-arrival)
  caps.EndPointDiagnostics.Size = IDD_STRUCTURE_SIZE(IDDCX_ENDPOINT_DIAGNOSTIC_INFO);
  // NETWORK_OTHER is rejected for remote drivers on 26100 (docs/research/06-review.md, finding iii).
  caps.EndPointDiagnostics.TransmissionType = IDDCX_TRANSMISSION_TYPE_OTHER;
  caps.EndPointDiagnostics.GammaSupport = IDDCX_FEATURE_IMPLEMENTATION_NONE;
  caps.EndPointDiagnostics.pEndPointFriendlyName = L"SplitPlay Desk Display";
  caps.EndPointDiagnostics.pEndPointModelName = L"SplitPlay Remote IDD";
  caps.EndPointDiagnostics.pEndPointManufacturerName = L"SplitPlay";
  caps.EndPointDiagnostics.pHardwareVersion = &endpointVersion;
  caps.EndPointDiagnostics.pFirmwareVersion = &endpointVersion;

  WDF_OBJECT_ATTRIBUTES attributes;
  WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, SpIddAdapterContext);

  IDARG_IN_ADAPTER_INIT init = {};
  init.WdfDevice = device;
  init.pCaps = &caps;
  init.ObjectAttributes = &attributes;

  IDARG_OUT_ADAPTER_INIT out = {};
  const NTSTATUS status = IddCxAdapterInitAsync(&init, &out);
  SP_IDD_LOG("IddAdapterInitAsync", status);
  if (NT_SUCCESS(status)) {
    ctx->adapter = out.AdapterObject;
    SpIddGetAdapterContext(out.AdapterObject)->device = ctx;
  }
  return status;
}

// ---------------------------------------------------------------------------------------------------------
// IddCx adapter callbacks
// ---------------------------------------------------------------------------------------------------------

// EvtIddCxAdapterInitFinished: IddCx finished the asynchronous IddCxAdapterInitAsync. On success the driver
// may set the preferred render adapter and report monitors. For a remote driver the OS created this adapter for
// one session, so the one monitor is reported immediately; no hot-plug emulation is needed.
static NTSTATUS SpIddEvtAdapterInitFinished(IDDCX_ADAPTER adapter, const IDARG_IN_ADAPTER_INIT_FINISHED* args) {
  SpIddDeviceContext* ctx = SpIddGetAdapterContext(adapter)->device;
  SP_IDD_LOG("IddAdapterInitFinished", args->AdapterInitStatus);
  if (!NT_SUCCESS(args->AdapterInitStatus)) {
    return STATUS_SUCCESS; // nothing to do; the OS tears the device down
  }
  InterlockedOr(&ctx->state, SP_IDD_STATE_ADAPTER_READY);

  // Render GPU preference must be set before the monitor arrives to affect the first swap chain
  // (docs/research/10-maxout-gpu.md section 3). Stage 2: only set if the service already supplied one.
  WdfWaitLockAcquire(ctx->lock, nullptr);
  const bool hasPreferred = ctx->hasPreferredRenderAdapter;
  const LUID preferred = ctx->preferredRenderAdapter;
  WdfWaitLockRelease(ctx->lock);
  if (hasPreferred) {
    IDARG_IN_ADAPTERSETRENDERADAPTER render = {};
    render.PreferredRenderAdapter = preferred;
    IddCxAdapterSetRenderAdapter(adapter, &render);
  }

  GUID container = kSpIddMonitorContainerBase;
  container.Data4[4] = static_cast<UCHAR>(ctx->sessionId >> 24);
  container.Data4[5] = static_cast<UCHAR>(ctx->sessionId >> 16);
  container.Data4[6] = static_cast<UCHAR>(ctx->sessionId >> 8);
  container.Data4[7] = static_cast<UCHAR>(ctx->sessionId);

  IDDCX_MONITOR_INFO info = {};
  info.Size = IDD_STRUCTURE_SIZE(IDDCX_MONITOR_INFO);
  // TODO(stage3 lab): confirm which connector type the remote path accepts; HDMI is what the sample uses.
  info.MonitorType = DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI;
  info.ConnectorIndex = 0;
  // No EDID: "IDDCX_MONITOR_DESCRIPTION_TYPE_EDID with zero description size". The OS then asks for modes
  // through EvtIddCxMonitorGetDefaultDescriptionModes and for the size through EvtIddCxMonitorGetPhysicalSize.
  info.MonitorDescription.Size = IDD_STRUCTURE_SIZE(IDDCX_MONITOR_DESCRIPTION);
  info.MonitorDescription.Type = IDDCX_MONITOR_DESCRIPTION_TYPE_EDID;
  info.MonitorDescription.DataSize = 0;
  info.MonitorDescription.pData = nullptr;
  info.MonitorContainerId = container;

  WDF_OBJECT_ATTRIBUTES attributes;
  WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, SpIddMonitorContext);

  IDARG_IN_MONITORCREATE create = {};
  create.ObjectAttributes = &attributes;
  create.pMonitorInfo = &info;

  IDARG_OUT_MONITORCREATE created = {};
  NTSTATUS status = IddCxMonitorCreate(adapter, &create, &created);
  SP_IDD_LOG("IddMonitorCreate", status);
  if (!NT_SUCCESS(status)) {
    return status;
  }
  SpIddGetMonitorContext(created.MonitorObject)->device = ctx;
  ctx->monitor = created.MonitorObject;

  IDARG_OUT_MONITORARRIVAL arrival = {};
  status = IddCxMonitorArrival(created.MonitorObject, &arrival);
  SP_IDD_LOG("IddMonitorArrival", status);
  if (NT_SUCCESS(status)) {
    InterlockedOr(&ctx->state, SP_IDD_STATE_MONITOR_ARRIVED);
  }
  return status;
}

// EvtIddCxAdapterCommitModes: the OS committed a new set of paths (modes) for the adapter. The driver records
// the active mode; the swap chain for it is delivered separately through EvtIddCxMonitorAssignSwapChain.
static NTSTATUS SpIddEvtAdapterCommitModes(IDDCX_ADAPTER adapter, const IDARG_IN_COMMITMODES* args) {
  SpIddDeviceContext* ctx = SpIddGetAdapterContext(adapter)->device;
  SpIddSetMode committed = {};
  for (UINT i = 0; i < args->PathCount; ++i) {
    const IDDCX_PATH& path = args->pPaths[i];
    if ((path.Flags & IDDCX_PATH_FLAGS_ACTIVE) == 0) {
      continue;
    }
    committed.size = sizeof(committed);
    committed.width = path.TargetVideoSignalInfo.activeSize.cx;
    committed.height = path.TargetVideoSignalInfo.activeSize.cy;
    committed.refresh_num = path.TargetVideoSignalInfo.vSyncFreq.Numerator;
    committed.refresh_den = path.TargetVideoSignalInfo.vSyncFreq.Denominator;
  }
  WdfWaitLockAcquire(ctx->lock, nullptr);
  ctx->committedMode = committed;
  WdfWaitLockRelease(ctx->lock);
  TraceLoggingWrite(g_SpTraceProvider, "IddCommitModes", TraceLoggingLevel(WINEVENT_LEVEL_INFO),
                    TraceLoggingUInt32(committed.width, "width"), TraceLoggingUInt32(committed.height, "height"),
                    TraceLoggingUInt32(committed.refresh_num, "refresh_num"),
                    TraceLoggingUInt32(committed.refresh_den, "refresh_den"));
  return STATUS_SUCCESS;
}

// ---------------------------------------------------------------------------------------------------------
// IddCx mode callbacks
// ---------------------------------------------------------------------------------------------------------

// EvtIddCxParseMonitorDescription: the OS asks the driver to turn a monitor description (EDID) into monitor
// modes. We never supply an EDID, so this is only reached defensively; answer with the driver mode list.
// Protocol: called first with a zero-sized buffer to learn the count, then with a buffer of that size.
static NTSTATUS SpIddEvtParseMonitorDescription(const IDARG_IN_PARSEMONITORDESCRIPTION* in,
                                                IDARG_OUT_PARSEMONITORDESCRIPTION* out) {
  out->MonitorModeBufferOutputCount = splitplay::idd::kModeCount;
  out->PreferredMonitorModeIdx = splitplay::idd::kPreferredModeIndex;
  if (in->MonitorModeBufferInputCount < splitplay::idd::kModeCount) {
    return in->MonitorModeBufferInputCount > 0 ? STATUS_BUFFER_TOO_SMALL : STATUS_SUCCESS;
  }
  SpIddFillMonitorModes(in->pMonitorModes);
  return STATUS_SUCCESS;
}

// EvtIddCxMonitorGetDefaultDescriptionModes: modes for a monitor without a description, which is our only case.
// Same two-call protocol as above. These are the monitor modes the OS intersects with the target modes.
static NTSTATUS SpIddEvtMonitorGetDefaultModes(IDDCX_MONITOR monitor, const IDARG_IN_GETDEFAULTDESCRIPTIONMODES* in,
                                               IDARG_OUT_GETDEFAULTDESCRIPTIONMODES* out) {
  UNREFERENCED_PARAMETER(monitor);
  out->DefaultMonitorModeBufferOutputCount = splitplay::idd::kModeCount;
  out->PreferredMonitorModeIdx = splitplay::idd::kPreferredModeIndex;
  if (in->DefaultMonitorModeBufferInputCount < splitplay::idd::kModeCount) {
    return in->DefaultMonitorModeBufferInputCount > 0 ? STATUS_BUFFER_TOO_SMALL : STATUS_SUCCESS;
  }
  SpIddFillMonitorModes(in->pDefaultMonitorModes);
  return STATUS_SUCCESS;
}

// EvtIddCxMonitorQueryTargetModes: the modes the driver itself can process (here: all of them, up to 500 Hz).
// RequiredBandwidth is checked against IDDCX_ADAPTER_CAPS.MaxDisplayPipelineRate, which we leave unlimited.
static NTSTATUS SpIddEvtMonitorQueryModes(IDDCX_MONITOR monitor, const IDARG_IN_QUERYTARGETMODES* in,
                                          IDARG_OUT_QUERYTARGETMODES* out) {
  UNREFERENCED_PARAMETER(monitor);
  out->TargetModeBufferOutputCount = splitplay::idd::kModeCount;
  if (in->TargetModeBufferInputCount < splitplay::idd::kModeCount) {
    return in->TargetModeBufferInputCount > 0 ? STATUS_BUFFER_TOO_SMALL : STATUS_SUCCESS;
  }
  for (UINT i = 0; i < splitplay::idd::kModeCount; ++i) {
    IDDCX_TARGET_MODE& mode = in->pTargetModes[i];
    mode.Size = IDD_STRUCTURE_SIZE(IDDCX_TARGET_MODE);
    SpIddFillSignalInfo(&mode.TargetVideoSignalInfo.targetVideoSignalInfo, splitplay::idd::ModeAt(i), false);
    mode.RequiredBandwidth = mode.TargetVideoSignalInfo.targetVideoSignalInfo.pixelRate;
  }
  return STATUS_SUCCESS;
}

// EvtIddCxMonitorGetPhysicalSize: physical size of an EDID-less remote monitor, in millimetres. Returning
// STATUS_NO_DATA_DETECTED would also be valid; a fixed 24" size keeps DPI decisions predictable.
static NTSTATUS SpIddEvtMonitorGetPhysicalSize(IDDCX_MONITOR monitor, IDARG_OUT_MONITORGETPHYSICALSIZE* out) {
  UNREFERENCED_PARAMETER(monitor);
  out->PhysicalWidth = kSpIddPhysicalWidthMm;
  out->PhysicalHeight = kSpIddPhysicalHeightMm;
  return STATUS_SUCCESS;
}

// ---------------------------------------------------------------------------------------------------------
// IddCx swap-chain callbacks
// ---------------------------------------------------------------------------------------------------------

// EvtIddCxMonitorAssignSwapChain: DWM of the session will now present the desktop into this swap chain. The
// driver must start consuming buffers promptly: IddCx resets the driver if a frame is not finished in 10 s.
static NTSTATUS SpIddEvtMonitorAssignSwapChain(IDDCX_MONITOR monitor, const IDARG_IN_SETSWAPCHAIN* args) {
  SpIddDeviceContext* ctx = SpIddGetMonitorContext(monitor)->device;

  SpIddSwapChainProcessorStop(ctx->processor); // defensive: IddCx unassigns before assigning a new one
  ctx->processor = nullptr;

  SpIddSwapChainProcessor* processor = nullptr;
  const NTSTATUS status = SpIddSwapChainProcessorStart(ctx, args, &processor);
  SP_IDD_LOG("IddAssignSwapChain", status);
  if (!NT_SUCCESS(status)) {
    // The processor already deleted the swap chain, which makes the OS create a new one and assign it again
    // (same contract as the IddSampleDriver). The callback itself succeeded.
    return STATUS_SUCCESS;
  }
  WdfWaitLockAcquire(ctx->lock, nullptr);
  ctx->currentRenderAdapter = args->RenderAdapterLuid;
  WdfWaitLockRelease(ctx->lock);
  ctx->processor = processor;
  InterlockedOr(&ctx->state, SP_IDD_STATE_SWAPCHAIN);
  return STATUS_SUCCESS;
}

// EvtIddCxMonitorUnassignSwapChain: the swap chain is going away (mode change, session disconnect, adapter
// change). The driver must stop using it before returning.
static NTSTATUS SpIddEvtMonitorUnassignSwapChain(IDDCX_MONITOR monitor) {
  SpIddDeviceContext* ctx = SpIddGetMonitorContext(monitor)->device;
  SpIddSwapChainProcessorStop(ctx->processor);
  ctx->processor = nullptr;
  InterlockedAnd(&ctx->state, ~static_cast<LONG>(SP_IDD_STATE_SWAPCHAIN));
  SP_IDD_LOG("IddUnassignSwapChain", STATUS_SUCCESS);
  return STATUS_SUCCESS;
}

// ---------------------------------------------------------------------------------------------------------
// IOCTL interface (drivers/idd/include/splitplay/idd_ioctl.h)
// ---------------------------------------------------------------------------------------------------------

static NTSTATUS SpIddHandleGetInfo(SpIddDeviceContext* ctx, WDFREQUEST request, size_t* information) {
  SpIddInfo* info = nullptr;
  NTSTATUS status = WdfRequestRetrieveOutputBuffer(request, sizeof(SpIddInfo), reinterpret_cast<PVOID*>(&info), nullptr);
  if (!NT_SUCCESS(status)) {
    return status;
  }
  RtlZeroMemory(info, sizeof(*info));
  info->size = sizeof(*info);
  info->version = SP_IDD_IOCTL_VERSION;
  info->state = static_cast<uint32_t>(InterlockedOr(&ctx->state, 0));
  info->iddcx_version = ctx->iddCxVersion;
  info->frames_acquired = static_cast<uint64_t>(InterlockedOr64(&ctx->stats.framesAcquired, 0));
  info->frames_unchanged = static_cast<uint64_t>(InterlockedOr64(&ctx->stats.framesUnchanged, 0));
  info->last_present_qpc = static_cast<uint64_t>(InterlockedOr64(&ctx->stats.lastPresentQpc, 0));
  info->last_acquire_qpc = static_cast<uint64_t>(InterlockedOr64(&ctx->stats.lastAcquireQpc, 0));

  WdfWaitLockAcquire(ctx->lock, nullptr);
  info->session_id = ctx->sessionId;
  info->session_source = ctx->sessionSource;
  info->width = ctx->committedMode.width;
  info->height = ctx->committedMode.height;
  info->refresh_num = ctx->committedMode.refresh_num;
  info->refresh_den = ctx->committedMode.refresh_den;
  info->render_adapter_luid = static_cast<uint64_t>(ctx->currentRenderAdapter.LowPart) |
                              (static_cast<uint64_t>(static_cast<uint32_t>(ctx->currentRenderAdapter.HighPart)) << 32);
  RtlCopyMemory(info->desk_id, ctx->deskId, sizeof(info->desk_id));
  WdfWaitLockRelease(ctx->lock);

  *information = sizeof(*info);
  return STATUS_SUCCESS;
}

static NTSTATUS SpIddHandleAttachDesk(SpIddDeviceContext* ctx, WDFREQUEST request) {
  SpIddAttachDesk* in = nullptr;
  NTSTATUS status = WdfRequestRetrieveInputBuffer(request, sizeof(SpIddAttachDesk), reinterpret_cast<PVOID*>(&in), nullptr);
  if (!NT_SUCCESS(status)) {
    return status;
  }
  if (in->size < sizeof(SpIddAttachDesk) || !splitplay::idd::IsValidDeskId(in->desk_id, sizeof(in->desk_id))) {
    return STATUS_INVALID_PARAMETER;
  }

  WdfWaitLockAcquire(ctx->lock, nullptr);
  if (ctx->sessionId != SP_IDD_SESSION_UNKNOWN && in->session_id != ctx->sessionId) {
    status = STATUS_OBJECT_NAME_NOT_FOUND; // the service addressed the wrong adapter instance
  } else {
    RtlCopyMemory(ctx->deskId, in->desk_id, sizeof(ctx->deskId));
    ctx->deskId[SP_IDD_DESK_ID_MAX] = '\0';
    InterlockedExchange(&ctx->deskHash, static_cast<LONG>(sp_desk_hash(ctx->deskId)));
    InterlockedOr(&ctx->state, SP_IDD_STATE_DESK_ATTACHED);
    // ===== CAPTURE RING HOOK (Stage 3): create Global\SplitPlay.CaptureRing.<desk-id>, FrameReady event,
    // slot textures and fence here (capture_ring.h "The producer owns all resources"), then set
    // SP_IDD_STATE_RING_OPEN. Re-attaching the same desk keeps the ring; a different desk recreates it.
  }
  WdfWaitLockRelease(ctx->lock);
  return status;
}

static NTSTATUS SpIddHandleSetMode(SpIddDeviceContext* ctx, WDFREQUEST request) {
  SpIddSetMode* in = nullptr;
  NTSTATUS status = WdfRequestRetrieveInputBuffer(request, sizeof(SpIddSetMode), reinterpret_cast<PVOID*>(&in), nullptr);
  if (!NT_SUCCESS(status)) {
    return status;
  }
  if (in->size < sizeof(SpIddSetMode) ||
      !splitplay::idd::IsSupported(in->width, in->height, in->refresh_num, in->refresh_den)) {
    return STATUS_INVALID_PARAMETER;
  }
  const SpIddSetMode mode = *in;
  WdfWaitLockAcquire(ctx->lock, nullptr);
  ctx->requestedMode = mode;
  WdfWaitLockRelease(ctx->lock);
  return SpIddApplyDisplayConfig(ctx, mode);
}

static NTSTATUS SpIddHandleSetRenderAdapter(SpIddDeviceContext* ctx, WDFREQUEST request) {
  SpIddSetRenderAdapter* in = nullptr;
  NTSTATUS status =
      WdfRequestRetrieveInputBuffer(request, sizeof(SpIddSetRenderAdapter), reinterpret_cast<PVOID*>(&in), nullptr);
  if (!NT_SUCCESS(status)) {
    return status;
  }
  if (in->size < sizeof(SpIddSetRenderAdapter)) {
    return STATUS_INVALID_PARAMETER;
  }
  LUID luid;
  luid.LowPart = in->luid_low;
  luid.HighPart = in->luid_high;

  WdfWaitLockAcquire(ctx->lock, nullptr);
  ctx->hasPreferredRenderAdapter = true;
  ctx->preferredRenderAdapter = luid;
  WdfWaitLockRelease(ctx->lock);

  if (ctx->adapter != nullptr) {
    // A preference, not a guarantee: the OS may pick another adapter (e.g. the preferred one is stopped).
    // The service verifies the result through SpIddInfo.render_adapter_luid.
    IDARG_IN_ADAPTERSETRENDERADAPTER render = {};
    render.PreferredRenderAdapter = luid;
    IddCxAdapterSetRenderAdapter(ctx->adapter, &render);
  }
  return STATUS_SUCCESS;
}

// EvtIddCxDeviceIoControl: IddCx owns the device's I/O queue and forwards every device IOCTL here. Requests
// come from the service through SP_GUID_DEVINTERFACE_IDD; the device's SDDL (INF) limits who can open it.
static void SpIddEvtDeviceIoControl(WDFDEVICE device, WDFREQUEST request, size_t outputLength, size_t inputLength,
                                    ULONG ioControlCode) {
  UNREFERENCED_PARAMETER(outputLength);
  UNREFERENCED_PARAMETER(inputLength);
  SpIddDeviceContext* ctx = SpIddGetDeviceContext(device);

  NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;
  size_t information = 0;
  switch (ioControlCode) {
    case SP_IOCTL_IDD_GET_INFO:
      status = SpIddHandleGetInfo(ctx, request, &information);
      break;
    case SP_IOCTL_IDD_ATTACH_DESK:
      status = SpIddHandleAttachDesk(ctx, request);
      break;
    case SP_IOCTL_IDD_SET_MODE:
      status = SpIddHandleSetMode(ctx, request);
      break;
    case SP_IOCTL_IDD_SET_RENDER_ADAPTER:
      status = SpIddHandleSetRenderAdapter(ctx, request);
      break;
    default:
      break;
  }
  WdfRequestCompleteWithInformation(request, status, information);
}
