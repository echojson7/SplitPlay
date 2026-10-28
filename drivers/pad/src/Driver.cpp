// SPDX-License-Identifier: MIT
// SplitPlayPad: UMDF2 + Virtual HID Framework (VHF) virtual gamepad driver.
//
// Device stack (SplitPlayPad.inf, layout follows libvirtualgamepad, MIT): a root-enumerated device whose function
// driver is the in-box VHF (hidvhf.inf, vhfservice), with WUDFRd and this UMDF driver above it as an upper filter.
// For every SP_IOCTL_PAD_CREATE the driver calls VhfCreate/VhfStart on a file handle opened against its own
// stack; VHF then enumerates one HID child per pad (HID\VID_054C&PID_09CC...), which the in-box HID stack and
// games see as a DualShock 4.
//
// Ownership: a pad belongs to the file object (worker handle) that created it and is deleted in EvtFileClose,
// so a crashed worker never leaves ghost controllers.
//
// Session isolation is NOT done here. See SP_PAD stamping note in pad_ioctl.h: the service stamps
// DEVPKEY_Device_SessionId on the HID child (marked below at the point where the child appears).

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <wdf.h>
#include <vhf.h>

#include <stdint.h>

#include "splitplay/pad_ioctl.h"
#include "Ds4.h"

namespace ds4 = splitplay::pad;

static const GUID kSpPadInterfaceGuid = SP_GUID_DEVINTERFACE_PAD_INIT;
// Base container id of the HID children; the last byte is the slot index so that each pad is its own
// "physical device" for anything that groups by container. {96E72FC0-EAB7-42DB-A254-7587B5C6C781}
static const GUID kSpPadContainerBase = {0x96e72fc0, 0xeab7, 0x42db, {0xa2, 0x54, 0x75, 0x87, 0xb5, 0xc6, 0xc7, 0x81}};

enum SpPadSlotState : uint32_t {
  kSpPadSlotFree = 0,
  kSpPadSlotStarting = 1,  // reserved, VhfCreate/VhfStart in progress
  kSpPadSlotActive = 2,
  kSpPadSlotDeleting = 3,  // handle detached, VhfDelete in progress
};

struct SpPadDeviceContext;

struct SpPadSlot {
  SpPadDeviceContext* device;  // back-pointer for VHF callbacks (VhfClientContext = slot)
  uint32_t state;              // SpPadSlotState
  uint32_t generation;         // bumps on every create so that stale pad ids are rejected
  uint32_t padId;
  WDFFILEOBJECT owner;
  VHFHANDLE vhf;
  uint8_t mac[6];
  wchar_t instanceId[SP_PAD_INSTANCE_ID_MAX];

  // Latest output report written by a game (rumble/lightbar), read by SP_IOCTL_PAD_GET_FEEDBACK.
  uint32_t feedbackSequence;
  uint32_t feedbackLength;
  uint8_t feedback[SP_PAD_MAX_REPORT];
};

struct SpPadDeviceContext {
  WDFDEVICE device;
  WDFWAITLOCK lock;            // guards slots and the target fields; never held across a Vhf* call
  WDFIOTARGET vhfTarget;       // local I/O target opened by file on our own stack
  HANDLE vhfFileHandle;        // VHF_CONFIG.FileHandle, valid while vhfTarget is open
  SpPadSlot slots[SP_PAD_MAX_PADS];
};
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(SpPadDeviceContext, SpPadGetDeviceContext);

extern "C" DRIVER_INITIALIZE DriverEntry;
static EVT_WDF_DRIVER_DEVICE_ADD SpPadEvtDeviceAdd;
static EVT_WDF_DEVICE_D0_EXIT SpPadEvtDeviceD0Exit;
static EVT_WDF_DEVICE_FILE_CREATE SpPadEvtFileCreate;
static EVT_WDF_FILE_CLOSE SpPadEvtFileClose;
static EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL SpPadEvtIoDeviceControl;
static EVT_VHF_ASYNC_OPERATION SpPadEvtVhfWriteReport;
static EVT_VHF_ASYNC_OPERATION SpPadEvtVhfGetFeature;
static EVT_VHF_ASYNC_OPERATION SpPadEvtVhfSetFeature;

// ---------------------------------------------------------------------------------------------------------
// Slots
// ---------------------------------------------------------------------------------------------------------

static uint32_t SpPadMakeId(uint32_t index, uint32_t generation) {
  return ((generation & 0x00FFFFFFu) << 8) | (index + 1u); // never 0
}

// Finds the active slot `padId` owned by `owner`. Caller holds ctx->lock.
static SpPadSlot* SpPadFindOwnedSlot(SpPadDeviceContext* ctx, uint32_t padId, WDFFILEOBJECT owner) {
  const uint32_t index = (padId & 0xFFu) - 1u;
  if (padId == 0 || index >= SP_PAD_MAX_PADS) {
    return nullptr;
  }
  SpPadSlot* slot = &ctx->slots[index];
  if (slot->state != kSpPadSlotActive || slot->padId != padId || slot->owner != owner) {
    return nullptr;
  }
  return slot;
}

// "SplitPlayPad_NN": instance id of the VHF child, unique per slot.
static void SpPadFormatInstanceId(wchar_t (&out)[SP_PAD_INSTANCE_ID_MAX], uint32_t index) {
  static const wchar_t kPrefix[] = L"SplitPlayPad_";
  uint32_t n = 0;
  for (; kPrefix[n] != L'\0'; ++n) {
    out[n] = kPrefix[n];
  }
  out[n++] = static_cast<wchar_t>(L'0' + (index / 10) % 10);
  out[n++] = static_cast<wchar_t>(L'0' + index % 10);
  out[n] = L'\0';
}

// Detaches and deletes one slot's VHF device. Caller must NOT hold ctx->lock: VhfDelete(.., TRUE) waits for
// in-flight VHF callbacks, which take the lock.
static void SpPadDeleteSlot(SpPadDeviceContext* ctx, SpPadSlot* slot) {
  WdfWaitLockAcquire(ctx->lock, nullptr);
  VHFHANDLE vhf = nullptr;
  if (slot->state == kSpPadSlotActive) {
    vhf = slot->vhf;
    slot->vhf = nullptr;
    slot->state = kSpPadSlotDeleting;
  }
  WdfWaitLockRelease(ctx->lock);
  if (vhf == nullptr) {
    return;
  }

  // Removes the HID child devnode; VHF completes or cancels outstanding operations first.
  VhfDelete(vhf, TRUE);

  WdfWaitLockAcquire(ctx->lock, nullptr);
  slot->owner = nullptr;
  slot->padId = 0;
  slot->feedbackSequence = 0;
  slot->feedbackLength = 0;
  slot->state = kSpPadSlotFree;
  WdfWaitLockRelease(ctx->lock);
}

// Opens the local target on first use. It is a create against this device's own stack, so it only works once PnP
// has started the device; requests on it go to the lower drivers (VHF), not to our queue.
static NTSTATUS SpPadEnsureVhfTarget(SpPadDeviceContext* ctx, HANDLE* fileHandle) {
  WdfWaitLockAcquire(ctx->lock, nullptr);
  NTSTATUS status = STATUS_SUCCESS;
  if (ctx->vhfFileHandle == nullptr) {
    WDF_IO_TARGET_OPEN_PARAMS open;
    WDF_IO_TARGET_OPEN_PARAMS_INIT_OPEN_BY_FILE(&open, nullptr);
    status = WdfIoTargetOpen(ctx->vhfTarget, &open);
    if (NT_SUCCESS(status)) {
      ctx->vhfFileHandle = WdfIoTargetWdmGetTargetFileHandle(ctx->vhfTarget);
      if (ctx->vhfFileHandle == nullptr) {
        WdfIoTargetClose(ctx->vhfTarget);
        status = STATUS_DEVICE_NOT_READY;
      }
    }
  }
  *fileHandle = ctx->vhfFileHandle;
  WdfWaitLockRelease(ctx->lock);
  return status;
}

// ---------------------------------------------------------------------------------------------------------
// VHF callbacks (VhfClientContext = SpPadSlot*)
// ---------------------------------------------------------------------------------------------------------

// Output report from the host (game rumble/lightbar). Stored for SP_IOCTL_PAD_GET_FEEDBACK.
static void SpPadEvtVhfWriteReport(PVOID clientContext, VHFOPERATIONHANDLE operation, PVOID operationContext,
                                   PHID_XFER_PACKET packet) {
  UNREFERENCED_PARAMETER(operationContext);
  SpPadSlot* slot = static_cast<SpPadSlot*>(clientContext);
  SpPadDeviceContext* ctx = slot->device;
  NTSTATUS status = STATUS_INVALID_PARAMETER;
  if (packet != nullptr && packet->reportBuffer != nullptr && packet->reportBufferLen > 0) {
    const uint32_t length = packet->reportBufferLen < SP_PAD_MAX_REPORT ? packet->reportBufferLen : SP_PAD_MAX_REPORT;
    WdfWaitLockAcquire(ctx->lock, nullptr);
    RtlCopyMemory(slot->feedback, packet->reportBuffer, length);
    slot->feedbackLength = length;
    slot->feedbackSequence = slot->feedbackSequence + 1 == 0 ? 1 : slot->feedbackSequence + 1;
    WdfWaitLockRelease(ctx->lock);
    status = STATUS_SUCCESS;
  }
  VhfAsyncOperationComplete(operation, status);
}

// Feature report read (HidD_GetFeature): DS4 hosts read calibration, pairing and firmware info.
static void SpPadEvtVhfGetFeature(PVOID clientContext, VHFOPERATIONHANDLE operation, PVOID operationContext,
                                  PHID_XFER_PACKET packet) {
  UNREFERENCED_PARAMETER(operationContext);
  SpPadSlot* slot = static_cast<SpPadSlot*>(clientContext);
  NTSTATUS status = STATUS_INVALID_PARAMETER;
  if (packet != nullptr && packet->reportBuffer != nullptr && packet->reportBufferLen > 0) {
    uint8_t calibration[ds4::kDs4CalibrationSize];
    uint8_t pairing[ds4::kDs4PairingSize];
    uint8_t firmware[ds4::kDs4FirmwareSize];
    const uint8_t* source = nullptr;
    uint32_t size = 0;
    switch (packet->reportId) {
      case ds4::kDs4FeatureCalibrationId:
        ds4::FillCalibrationReport(calibration);
        source = calibration;
        size = sizeof(calibration);
        break;
      case ds4::kDs4FeaturePairingId:
        ds4::FillPairingReport(pairing, slot->mac); // mac is immutable while the slot is active
        source = pairing;
        size = sizeof(pairing);
        break;
      case ds4::kDs4FeatureFirmwareId:
        ds4::FillFirmwareReport(firmware);
        source = firmware;
        size = sizeof(firmware);
        break;
      default:
        break;
    }
    if (source != nullptr) {
      const uint32_t length = packet->reportBufferLen < size ? packet->reportBufferLen : size;
      RtlCopyMemory(packet->reportBuffer, source, length);
      status = STATUS_SUCCESS;
    }
  }
  VhfAsyncOperationComplete(operation, status);
}

// Feature report write (HidD_SetFeature): accepted and ignored in Stage 2.
static void SpPadEvtVhfSetFeature(PVOID clientContext, VHFOPERATIONHANDLE operation, PVOID operationContext,
                                  PHID_XFER_PACKET packet) {
  UNREFERENCED_PARAMETER(clientContext);
  UNREFERENCED_PARAMETER(operationContext);
  UNREFERENCED_PARAMETER(packet);
  VhfAsyncOperationComplete(operation, STATUS_SUCCESS);
}

// ---------------------------------------------------------------------------------------------------------
// IOCTL handlers
// ---------------------------------------------------------------------------------------------------------

static NTSTATUS SpPadHandleCreate(SpPadDeviceContext* ctx, WDFREQUEST request, WDFFILEOBJECT owner,
                                  size_t* information) {
  SpPadCreateIn* in = nullptr;
  SpPadCreateOut* out = nullptr;
  NTSTATUS status = WdfRequestRetrieveInputBuffer(request, sizeof(*in), reinterpret_cast<PVOID*>(&in), nullptr);
  if (NT_SUCCESS(status)) {
    status = WdfRequestRetrieveOutputBuffer(request, sizeof(*out), reinterpret_cast<PVOID*>(&out), nullptr);
  }
  if (!NT_SUCCESS(status)) {
    return status;
  }
  if (in->size < sizeof(*in) || in->version != SP_PAD_IOCTL_VERSION || in->profile != SP_PAD_PROFILE_DS4) {
    return STATUS_INVALID_PARAMETER;
  }

  HANDLE fileHandle = nullptr;
  status = SpPadEnsureVhfTarget(ctx, &fileHandle);
  if (!NT_SUCCESS(status)) {
    return status;
  }

  // Reserve a free slot and fully initialise it before VHF can call back into it.
  WdfWaitLockAcquire(ctx->lock, nullptr);
  SpPadSlot* slot = nullptr;
  uint32_t index = 0;
  for (; index < SP_PAD_MAX_PADS; ++index) {
    if (ctx->slots[index].state == kSpPadSlotFree) {
      slot = &ctx->slots[index];
      break;
    }
  }
  if (slot != nullptr) {
    slot->device = ctx;
    slot->state = kSpPadSlotStarting;
    slot->generation += 1;
    slot->padId = SpPadMakeId(index, slot->generation);
    slot->owner = owner;
    slot->feedbackSequence = 0;
    slot->feedbackLength = 0;
    bool zeroMac = true;
    for (uint32_t i = 0; i < 6; ++i) {
      zeroMac = zeroMac && in->mac[i] == 0;
    }
    for (uint32_t i = 0; i < 6; ++i) {
      slot->mac[i] = in->mac[i];
    }
    if (zeroMac) {
      // Locally administered unicast address, distinct per slot.
      const uint8_t derived[6] = {static_cast<uint8_t>(index), 0x50, 0x53, 0x50, 0x53, 0x02};
      for (uint32_t i = 0; i < 6; ++i) {
        slot->mac[i] = derived[i];
      }
    }
    SpPadFormatInstanceId(slot->instanceId, index);
  }
  WdfWaitLockRelease(ctx->lock);
  if (slot == nullptr) {
    return STATUS_INSUFFICIENT_RESOURCES; // SP_PAD_MAX_PADS reached
  }

  VHF_CONFIG config;
  // VHF only reads the descriptor; the cast drops const for the PUCHAR field.
  VHF_CONFIG_INIT(&config, fileHandle, static_cast<USHORT>(ds4::kDs4ReportDescriptorSize),
                  const_cast<PUCHAR>(ds4::kDs4ReportDescriptor));
  config.VhfClientContext = slot;
  config.VendorID = ds4::kDs4VendorId;
  config.ProductID = ds4::kDs4ProductId;
  config.VersionNumber = ds4::kDs4VersionNumber;
  config.ContainerID = kSpPadContainerBase;
  config.ContainerID.Data4[7] = static_cast<UCHAR>(index);
  config.InstanceID = slot->instanceId;
  config.InstanceIDLength = 0;
  while (slot->instanceId[config.InstanceIDLength / sizeof(wchar_t)] != L'\0') {
    config.InstanceIDLength = static_cast<USHORT>(config.InstanceIDLength + sizeof(wchar_t));
  }
  config.InstanceIDLength = static_cast<USHORT>(config.InstanceIDLength + sizeof(wchar_t)); // terminator
  config.EvtVhfAsyncOperationWriteReport = SpPadEvtVhfWriteReport;
  config.EvtVhfAsyncOperationGetFeature = SpPadEvtVhfGetFeature;
  config.EvtVhfAsyncOperationSetFeature = SpPadEvtVhfSetFeature;
  // No EvtVhfReadyForNextReadReport: VHF then buffers submitted input reports itself.

  VHFHANDLE vhf = nullptr;
  status = VhfCreate(&config, &vhf);
  if (NT_SUCCESS(status)) {
    WdfWaitLockAcquire(ctx->lock, nullptr);
    slot->vhf = vhf;
    WdfWaitLockRelease(ctx->lock);
    status = VhfStart(vhf);
    if (!NT_SUCCESS(status)) {
      VhfDelete(vhf, TRUE);
    }
  }
  if (!NT_SUCCESS(status)) {
    WdfWaitLockAcquire(ctx->lock, nullptr);
    slot->vhf = nullptr;
    slot->owner = nullptr;
    slot->padId = 0;
    slot->state = kSpPadSlotFree;
    WdfWaitLockRelease(ctx->lock);
    return status;
  }

  // ===== DEVPKEY_Device_SessionId STAMPING POINT ==========================================================
  // VhfStart has created the HID child "...\SplitPlayPad_NN". The driver does not know which desk session the
  // pad is for and must not decide it. The worker passes out->instance_id to the service, which finds the child
  // devnode and sets DEVPKEY_Device_SessionId = <desk session id> (CM_Set_DevNode_PropertyW), plus a HidHide
  // per-session rule as backstop (docs/research/06-review.md). Until then the pad is visible to all sessions.
  // ========================================================================================================

  WdfWaitLockAcquire(ctx->lock, nullptr);
  slot->state = kSpPadSlotActive;
  RtlZeroMemory(out, sizeof(*out));
  out->size = sizeof(*out);
  out->pad_id = slot->padId;
  for (uint32_t i = 0; i < SP_PAD_INSTANCE_ID_MAX; ++i) {
    out->instance_id[i] = static_cast<uint16_t>(slot->instanceId[i]);
  }
  WdfWaitLockRelease(ctx->lock);

  // Start from a neutral state so that hosts never see stale values.
  uint8_t neutral[ds4::kDs4InputReportSize];
  ds4::InitNeutralInputReport(neutral);
  HID_XFER_PACKET packet;
  packet.reportBuffer = neutral;
  packet.reportBufferLen = sizeof(neutral);
  packet.reportId = ds4::kDs4InputReportId;
  (void)VhfReadReportSubmit(vhf, &packet);

  *information = sizeof(*out);
  return STATUS_SUCCESS;
}

static NTSTATUS SpPadHandleSubmit(SpPadDeviceContext* ctx, WDFREQUEST request, WDFFILEOBJECT owner) {
  SpPadSubmitIn* in = nullptr;
  NTSTATUS status = WdfRequestRetrieveInputBuffer(request, sizeof(*in), reinterpret_cast<PVOID*>(&in), nullptr);
  if (!NT_SUCCESS(status)) {
    return status;
  }
  if (in->size < sizeof(*in) || in->length != ds4::kDs4InputReportSize || in->report[0] != ds4::kDs4InputReportId) {
    return STATUS_INVALID_PARAMETER;
  }

  // The owner's requests are the only users of this handle and EvtFileClose runs after all of them completed,
  // so the handle cannot be deleted while VhfReadReportSubmit runs.
  WdfWaitLockAcquire(ctx->lock, nullptr);
  SpPadSlot* slot = SpPadFindOwnedSlot(ctx, in->pad_id, owner);
  VHFHANDLE vhf = slot != nullptr ? slot->vhf : nullptr;
  WdfWaitLockRelease(ctx->lock);
  if (vhf == nullptr) {
    return STATUS_INVALID_HANDLE;
  }

  HID_XFER_PACKET packet;
  packet.reportBuffer = in->report; // VHF copies the report before returning
  packet.reportBufferLen = in->length;
  packet.reportId = in->report[0];
  return VhfReadReportSubmit(vhf, &packet);
}

static NTSTATUS SpPadHandleDestroy(SpPadDeviceContext* ctx, WDFREQUEST request, WDFFILEOBJECT owner) {
  SpPadDestroyIn* in = nullptr;
  NTSTATUS status = WdfRequestRetrieveInputBuffer(request, sizeof(*in), reinterpret_cast<PVOID*>(&in), nullptr);
  if (!NT_SUCCESS(status)) {
    return status;
  }
  WdfWaitLockAcquire(ctx->lock, nullptr);
  SpPadSlot* slot = in->size >= sizeof(*in) ? SpPadFindOwnedSlot(ctx, in->pad_id, owner) : nullptr;
  WdfWaitLockRelease(ctx->lock);
  if (slot == nullptr) {
    return STATUS_INVALID_HANDLE;
  }
  SpPadDeleteSlot(ctx, slot);
  return STATUS_SUCCESS;
}

static NTSTATUS SpPadHandleGetFeedback(SpPadDeviceContext* ctx, WDFREQUEST request, WDFFILEOBJECT owner,
                                       size_t* information) {
  SpPadFeedbackIn* in = nullptr;
  SpPadFeedbackOut* out = nullptr;
  NTSTATUS status = WdfRequestRetrieveInputBuffer(request, sizeof(*in), reinterpret_cast<PVOID*>(&in), nullptr);
  if (NT_SUCCESS(status)) {
    status = WdfRequestRetrieveOutputBuffer(request, sizeof(*out), reinterpret_cast<PVOID*>(&out), nullptr);
  }
  if (!NT_SUCCESS(status)) {
    return status;
  }
  const uint32_t padId = in->pad_id; // METHOD_BUFFERED: in and out share one buffer, read before writing

  WdfWaitLockAcquire(ctx->lock, nullptr);
  SpPadSlot* slot = SpPadFindOwnedSlot(ctx, padId, owner);
  if (slot != nullptr) {
    RtlZeroMemory(out, sizeof(*out));
    out->size = sizeof(*out);
    out->pad_id = padId;
    out->sequence = slot->feedbackSequence;
    out->length = slot->feedbackLength;
    RtlCopyMemory(out->report, slot->feedback, slot->feedbackLength);
  }
  WdfWaitLockRelease(ctx->lock);
  if (slot == nullptr) {
    return STATUS_INVALID_HANDLE;
  }
  *information = sizeof(*out);
  return STATUS_SUCCESS;
}

// Default (sequential) queue: only our private IOCTLs arrive here. VHF's own traffic uses the local file target
// and goes to the lower drivers directly.
static void SpPadEvtIoDeviceControl(WDFQUEUE queue, WDFREQUEST request, size_t outputLength, size_t inputLength,
                                    ULONG ioControlCode) {
  UNREFERENCED_PARAMETER(outputLength);
  UNREFERENCED_PARAMETER(inputLength);
  SpPadDeviceContext* ctx = SpPadGetDeviceContext(WdfIoQueueGetDevice(queue));
  const WDFFILEOBJECT owner = WdfRequestGetFileObject(request);
  if (owner == nullptr) {
    WdfRequestComplete(request, STATUS_INVALID_HANDLE);
    return;
  }

  NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;
  size_t information = 0;
  switch (ioControlCode) {
    case SP_IOCTL_PAD_CREATE:
      status = SpPadHandleCreate(ctx, request, owner, &information);
      break;
    case SP_IOCTL_PAD_SUBMIT:
      status = SpPadHandleSubmit(ctx, request, owner);
      break;
    case SP_IOCTL_PAD_DESTROY:
      status = SpPadHandleDestroy(ctx, request, owner);
      break;
    case SP_IOCTL_PAD_GET_FEEDBACK:
      status = SpPadHandleGetFeedback(ctx, request, owner, &information);
      break;
    default:
      break;
  }
  WdfRequestCompleteWithInformation(request, status, information);
}

// ---------------------------------------------------------------------------------------------------------
// WDF driver, device and file callbacks
// ---------------------------------------------------------------------------------------------------------

// Creates from the worker are completed here: the filter owns these file objects, VHF never sees them.
static void SpPadEvtFileCreate(WDFDEVICE device, WDFREQUEST request, WDFFILEOBJECT fileObject) {
  UNREFERENCED_PARAMETER(device);
  UNREFERENCED_PARAMETER(fileObject);
  WdfRequestComplete(request, STATUS_SUCCESS);
}

// Last reference to a worker handle is gone (normal close or process exit): delete every pad it owned.
static void SpPadEvtFileClose(WDFFILEOBJECT fileObject) {
  if (fileObject == nullptr) {
    return; // the local VHF target's own file (UmdfFileObjectPolicy = AllowNullAndUnknownFileObjects)
  }
  SpPadDeviceContext* ctx = SpPadGetDeviceContext(WdfFileObjectGetDevice(fileObject));
  for (uint32_t i = 0; i < SP_PAD_MAX_PADS; ++i) {
    WdfWaitLockAcquire(ctx->lock, nullptr);
    const bool owned = ctx->slots[i].state == kSpPadSlotActive && ctx->slots[i].owner == fileObject;
    WdfWaitLockRelease(ctx->lock);
    if (owned) {
      SpPadDeleteSlot(ctx, &ctx->slots[i]);
    }
  }
}

// Leaving D0 (disable, removal, sleep): remove all pads and close the local target. Workers re-create pads
// after the device returns; Stage 4 adds transparent re-creation across sleep.
static NTSTATUS SpPadEvtDeviceD0Exit(WDFDEVICE device, WDF_POWER_DEVICE_STATE targetState) {
  UNREFERENCED_PARAMETER(targetState);
  SpPadDeviceContext* ctx = SpPadGetDeviceContext(device);
  for (uint32_t i = 0; i < SP_PAD_MAX_PADS; ++i) {
    SpPadDeleteSlot(ctx, &ctx->slots[i]);
  }
  WdfWaitLockAcquire(ctx->lock, nullptr);
  if (ctx->vhfFileHandle != nullptr) {
    WdfIoTargetClose(ctx->vhfTarget);
    ctx->vhfFileHandle = nullptr;
  }
  WdfWaitLockRelease(ctx->lock);
  return STATUS_SUCCESS;
}

static NTSTATUS SpPadEvtDeviceAdd(WDFDRIVER driver, PWDFDEVICE_INIT deviceInit) {
  UNREFERENCED_PARAMETER(driver);

  // Upper filter above VHF: VHF stays the function driver and power-policy owner of the stack.
  WdfFdoInitSetFilter(deviceInit);

  WDF_FILEOBJECT_CONFIG fileConfig;
  WDF_FILEOBJECT_CONFIG_INIT(&fileConfig, SpPadEvtFileCreate, SpPadEvtFileClose, WDF_NO_EVENT_CALLBACK);
  WdfDeviceInitSetFileObjectConfig(deviceInit, &fileConfig, WDF_NO_OBJECT_ATTRIBUTES);

  WDF_PNPPOWER_EVENT_CALLBACKS pnp;
  WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&pnp);
  pnp.EvtDeviceD0Exit = SpPadEvtDeviceD0Exit;
  WdfDeviceInitSetPnpPowerEventCallbacks(deviceInit, &pnp);

  WDF_OBJECT_ATTRIBUTES attributes;
  WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, SpPadDeviceContext);
  WDFDEVICE device = nullptr;
  NTSTATUS status = WdfDeviceCreate(&deviceInit, &attributes, &device);
  if (!NT_SUCCESS(status)) {
    return status;
  }
  SpPadDeviceContext* ctx = SpPadGetDeviceContext(device);
  ctx->device = device;

  status = WdfWaitLockCreate(WDF_NO_OBJECT_ATTRIBUTES, &ctx->lock);
  if (!NT_SUCCESS(status)) {
    return status;
  }

  WDF_OBJECT_ATTRIBUTES targetAttributes;
  WDF_OBJECT_ATTRIBUTES_INIT(&targetAttributes);
  targetAttributes.ParentObject = device;
  status = WdfIoTargetCreate(device, &targetAttributes, &ctx->vhfTarget);
  if (!NT_SUCCESS(status)) {
    return status;
  }

  // The worker opens this interface. The device SDDL (INF) limits it to SYSTEM and Administrators.
  status = WdfDeviceCreateDeviceInterface(device, &kSpPadInterfaceGuid, nullptr);
  if (!NT_SUCCESS(status)) {
    return status;
  }

  WDF_IO_QUEUE_CONFIG queueConfig;
  WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchSequential);
  queueConfig.EvtIoDeviceControl = SpPadEvtIoDeviceControl;
  // Power-managed (not the filter default): no IOCTL runs while EvtDeviceD0Exit deletes the pads, since every
  // request completes synchronously and the framework drains the queue before leaving D0.
  queueConfig.PowerManaged = WdfTrue;
  return WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, nullptr);
}

extern "C" NTSTATUS DriverEntry(PDRIVER_OBJECT driverObject, PUNICODE_STRING registryPath) {
  WDF_DRIVER_CONFIG config;
  WDF_DRIVER_CONFIG_INIT(&config, SpPadEvtDeviceAdd);
  return WdfDriverCreate(driverObject, registryPath, WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
}

static_assert(ds4::kDs4InputReportSize <= SP_PAD_MAX_REPORT, "DS4 input report must fit the IOCTL buffer");
