// SPDX-License-Identifier: MIT
// SplitPlayIdd: remote-session indirect display driver (UMDF2 + IddCx).
//
// One instance of this driver runs per desk session, in its own WUDFHost process (the INF disables UMDF
// host process sharing), on the remote display adapter devnode that the OS remote-desktop stack creates for
// the session after our protocol provider (splitplay-rdsprov) answered IWRdsWddmIddProps::GetHardwareId with
// the hardware id in SplitPlayIdd.inf.
//
// Lifecycle (every IddCx callback is documented where it is implemented, in Driver.cpp):
//   DriverEntry -> EvtDeviceAdd (IddCxDeviceInitConfig, WdfDeviceCreate, IddCxDeviceInitialize)
//   -> EvtDeviceD0Entry (IddCxAdapterInitAsync with IDDCX_ADAPTER_FLAGS_REMOTE_SESSION_DRIVER)
//   -> EvtIddCxAdapterInitFinished (IddCxMonitorCreate + IddCxMonitorArrival)
//   -> mode callbacks -> EvtIddCxMonitorAssignSwapChain (swap-chain processing thread, SwapChainProcessor.cpp)
#pragma once

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <wudfwdm.h>
#include <wdf.h>
#include <iddcx.h>
#include <dxgi1_5.h>
#include <d3d11_2.h>

#include <stdint.h>

#include "splitplay/idd_ioctl.h"

struct SpIddSwapChainProcessor;

// Per-frame counters shared between the processing thread (writer) and the IOCTL handler (reader).
struct SpIddFrameStats {
  volatile LONG64 framesAcquired;
  volatile LONG64 framesUnchanged;
  volatile LONG64 lastPresentQpc;
  volatile LONG64 lastAcquireQpc;
};

// Context of the WDFDEVICE. Lives as long as the device object; zero-initialised by WDF.
struct SpIddDeviceContext {
  WDFDEVICE device;
  IDDCX_ADAPTER adapter;           // set by IddCxAdapterInitAsync
  IDDCX_MONITOR monitor;           // set by IddCxMonitorCreate
  WDFWAITLOCK lock;                // guards every non-volatile field below

  uint32_t sessionId;              // SP_IDD_SESSION_UNKNOWN until derived
  uint32_t sessionSource;          // SpIddSessionSource
  volatile LONG state;             // SpIddStateFlags
  uint32_t iddCxVersion;           // IddCxGetVersion()

  SpIddSetMode committedMode;      // last active path from EvtIddCxAdapterCommitModes, size == 0 if none
  SpIddSetMode requestedMode;      // last SP_IOCTL_IDD_SET_MODE, size == 0 if none
  bool hasPreferredRenderAdapter;
  LUID preferredRenderAdapter;
  LUID currentRenderAdapter;
  char deskId[SP_IDD_DESK_ID_MAX + 1];
  volatile LONG deskHash;          // sp_desk_hash(deskId) as stored bits, 0 until attached

  SpIddSwapChainProcessor* processor; // owned; non-null while a swap chain is assigned
  SpIddFrameStats stats;
};
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(SpIddDeviceContext, SpIddGetDeviceContext);

// Contexts of the IddCx adapter and monitor objects: back-pointers to the device context.
struct SpIddAdapterContext {
  SpIddDeviceContext* device;
};
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(SpIddAdapterContext, SpIddGetAdapterContext);

struct SpIddMonitorContext {
  SpIddDeviceContext* device;
};
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(SpIddMonitorContext, SpIddGetMonitorContext);

// SwapChainProcessor.cpp
// Creates a D3D11 device on the swap chain's render adapter and starts the processing thread. On failure the
// swap chain has already been deleted (so that the OS offers a new one) and *processor is null.
_Must_inspect_result_ NTSTATUS SpIddSwapChainProcessorStart(SpIddDeviceContext* device,
                                                            const IDARG_IN_SETSWAPCHAIN* args,
                                                            SpIddSwapChainProcessor** processor);
// Signals the thread to stop, waits for it and frees everything. Safe to call with nullptr.
void SpIddSwapChainProcessorStop(SpIddSwapChainProcessor* processor);
