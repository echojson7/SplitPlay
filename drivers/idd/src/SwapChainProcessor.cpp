// SPDX-License-Identifier: MIT
// SplitPlayIdd: swap-chain processing thread.
//
// One thread per assigned swap chain. It acquires every buffer DWM presents, stamps QPC, counts it and (from
// Stage 3) copies it into the capture ring for the desk worker. It must never block DWM: if the ring has no free
// slot the frame is dropped (capture_ring.h), and the buffer is always handed back promptly.

#include "Driver.h"

#include <avrt.h>

#include "splitplay/capture_ring.h"
#include "Trace.h"

struct SpIddSwapChainProcessor {
  SpIddDeviceContext* device;
  IDDCX_SWAPCHAIN swapChain;
  HANDLE newFrameEvent;   // owned by IddCx (IDARG_IN_SETSWAPCHAIN.hNextSurfaceAvailable), never closed here
  HANDLE terminateEvent;  // owned
  HANDLE thread;          // owned
  LUID renderAdapter;
  ID3D11Device* d3dDevice;
  ID3D11DeviceContext* d3dContext;
  uint64_t frameId;       // capture_ring.h SpCaptureSlot.frame_id, also the trace id
};

template <typename T>
static void SpIddRelease(T*& p) {
  if (p != nullptr) {
    p->Release();
    p = nullptr;
  }
}

// D3D11 device on the adapter DWM renders the session on. The capture-ring textures (Stage 3) are created on
// this device, and the worker opens them on the same adapter (SpCaptureRingHeader.adapter_luid).
static HRESULT SpIddCreateD3DDevice(SpIddSwapChainProcessor* p) {
  IDXGIFactory5* factory = nullptr;
  HRESULT hr = CreateDXGIFactory2(0, __uuidof(IDXGIFactory5), reinterpret_cast<void**>(&factory));
  if (FAILED(hr)) {
    return hr;
  }
  IDXGIAdapter1* adapter = nullptr;
  hr = factory->EnumAdapterByLuid(p->renderAdapter, __uuidof(IDXGIAdapter1), reinterpret_cast<void**>(&adapter));
  SpIddRelease(factory);
  if (FAILED(hr)) {
    return hr;
  }
  static const D3D_FEATURE_LEVEL kLevels[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
  hr = D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, kLevels,
                         ARRAYSIZE(kLevels), D3D11_SDK_VERSION, &p->d3dDevice, nullptr, &p->d3dContext);
  SpIddRelease(adapter);
  return hr;
}

// ===== CAPTURE RING HOOK (Stage 3) ==========================================================================
// This is where the acquired DWM surface is copied into the desk's capture ring
// (native/include/splitplay/capture_ring.h, producer side):
//   1. pick a slot with consumer_busy == 0; none free -> ++frames_dropped, return false (never wait)
//   2. ID3D11DeviceContext::CopyResource(slot texture, surface); signal the shared fence with ++fence_value
//   3. fill the slot from `stamp` (frame_id, present_qpc, acquire_qpc, dirty, cursor) + fence_value/generation
//   4. publish_qpc = QPC; store latest_slot / latest_frame_id with release semantics; SetEvent(FrameReady)
// Requires SP_IDD_STATE_RING_OPEN (created on SP_IOCTL_IDD_ATTACH_DESK). Stage 2: nothing is published.
static bool SpIddPublishToCaptureRing(SpIddSwapChainProcessor* p, IDXGIResource* surface, const SpCaptureSlot& stamp) {
  UNREFERENCED_PARAMETER(p);
  UNREFERENCED_PARAMETER(surface);
  UNREFERENCED_PARAMETER(stamp);
  return false;
}
// ============================================================================================================

static void SpIddRunCore(SpIddSwapChainProcessor* p) {
  IDXGIDevice* dxgiDevice = nullptr;
  HRESULT hr = p->d3dDevice->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void**>(&dxgiDevice));
  if (FAILED(hr)) {
    SP_IDD_LOG("IddQueryDxgiDevice", hr);
    return;
  }

  // Hand our device to IddCx: buffers are delivered as surfaces opened on this device.
  IDARG_IN_SWAPCHAINSETDEVICE setDevice = {};
  setDevice.pDevice = dxgiDevice;
  hr = IddCxSwapChainSetDevice(p->swapChain, &setDevice);
  if (FAILED(hr)) {
    SP_IDD_LOG("IddSwapChainSetDevice", hr);
    SpIddRelease(dxgiDevice);
    return;
  }

  // IddCx 1.6+: realtime GPU priority for the copy, so game GPU work cannot delay the frame
  // (docs/research/10-maxout-gpu.md section 1). Absent on IddCx 1.4/1.5; skipped there.
  if (IDD_IS_FUNCTION_AVAILABLE(IddCxSetRealtimeGPUPriority)) {
    IDARG_IN_SETREALTIMEGPUPRIORITY priority = {};
    priority.pDevice = dxgiDevice;
    hr = IddCxSetRealtimeGPUPriority(p->swapChain, &priority);
    SP_IDD_LOG("IddSetRealtimeGpuPriority", hr);
  }
  SpIddRelease(dxgiDevice);

  SpIddFrameStats& stats = p->device->stats;
  for (;;) {
    if (WaitForSingleObject(p->terminateEvent, 0) == WAIT_OBJECT_0) {
      break;
    }

    IDARG_OUT_RELEASEANDACQUIREBUFFER buffer = {};
    hr = IddCxSwapChainReleaseAndAcquireBuffer(p->swapChain, &buffer);
    if (hr == E_PENDING) {
      // No new frame yet: wait for DWM or for Stop(). The timeout only bounds how long a missed signal costs.
      HANDLE waits[] = {p->newFrameEvent, p->terminateEvent};
      const DWORD result = WaitForMultipleObjects(ARRAYSIZE(waits), waits, FALSE, 16);
      if (result == WAIT_OBJECT_0 || result == WAIT_TIMEOUT) {
        continue;
      }
      break; // terminate, or the wait failed
    }
    if (FAILED(hr)) {
      // The swap chain was abandoned (mode change, adapter loss): leave, the OS assigns a new one.
      SP_IDD_LOG("IddReleaseAndAcquireBuffer", hr);
      break;
    }

    LARGE_INTEGER acquireQpc;
    QueryPerformanceCounter(&acquireQpc);
    const uint64_t frameId = ++p->frameId;
    const IDDCX_METADATA& meta = buffer.MetaData;
    const bool unchanged = meta.DirtyRectCount == 0 && meta.MoveRegionCount == 0;

    InterlockedIncrement64(&stats.framesAcquired);
    if (unchanged) {
      InterlockedIncrement64(&stats.framesUnchanged);
    }
    InterlockedExchange64(&stats.lastPresentQpc, static_cast<LONG64>(meta.PresentDisplayQPCTime));
    InterlockedExchange64(&stats.lastAcquireQpc, acquireQpc.QuadPart);

    const uint32_t deskHash = static_cast<uint32_t>(InterlockedOr(&p->device->deskHash, 0));
    SP_IDD_TRACE_FRAME(frameId, meta.PresentDisplayQPCTime, SP_STAGE_PRESENT, deskHash);
    SP_IDD_TRACE_FRAME(frameId, static_cast<uint64_t>(acquireQpc.QuadPart), SP_STAGE_IDD_ACQUIRE, deskHash);

    SpCaptureSlot stamp = {};
    stamp.frame_id = frameId;
    stamp.present_qpc = meta.PresentDisplayQPCTime;
    stamp.acquire_qpc = static_cast<uint64_t>(acquireQpc.QuadPart);
    stamp.dirty = unchanged ? 0u : 1u;
    if (SpIddPublishToCaptureRing(p, meta.pSurface, stamp)) {
      LARGE_INTEGER publishQpc;
      QueryPerformanceCounter(&publishQpc);
      SP_IDD_TRACE_FRAME(frameId, static_cast<uint64_t>(publishQpc.QuadPart), SP_STAGE_IDD_PUBLISH, deskHash);
    }

    // We own one reference on the surface until the next ReleaseAndAcquire; drop it before reporting.
    if (meta.pSurface != nullptr) {
      meta.pSurface->Release();
    }
    hr = IddCxSwapChainFinishedProcessingFrame(p->swapChain);
    if (FAILED(hr)) {
      SP_IDD_LOG("IddFinishedProcessingFrame", hr);
      break;
    }
  }
}

static DWORD WINAPI SpIddProcessorThread(LPVOID parameter) {
  SpIddSwapChainProcessor* p = static_cast<SpIddSwapChainProcessor*>(parameter);

  // MMCSS keeps the thread scheduled promptly next to games; "DisplayPostProcessing" is the task class
  // intended for display pipelines.
  DWORD taskIndex = 0;
  HANDLE avTask = AvSetMmThreadCharacteristicsW(L"DisplayPostProcessing", &taskIndex);

  SpIddRunCore(p);

  // Always delete the swap chain when processing ends, so that the OS provides a new one if the monitor is
  // still active (e.g. after a mode change).
  WdfObjectDelete(reinterpret_cast<WDFOBJECT>(p->swapChain));
  p->swapChain = nullptr;

  if (avTask != nullptr) {
    AvRevertMmThreadCharacteristics(avTask);
  }
  return 0;
}

static void SpIddFreeProcessor(SpIddSwapChainProcessor* p) {
  SpIddRelease(p->d3dContext);
  SpIddRelease(p->d3dDevice);
  if (p->thread != nullptr) {
    CloseHandle(p->thread);
  }
  if (p->terminateEvent != nullptr) {
    CloseHandle(p->terminateEvent);
  }
  HeapFree(GetProcessHeap(), 0, p);
}

NTSTATUS SpIddSwapChainProcessorStart(SpIddDeviceContext* device, const IDARG_IN_SETSWAPCHAIN* args,
                                      SpIddSwapChainProcessor** processor) {
  *processor = nullptr;
  auto* p = static_cast<SpIddSwapChainProcessor*>(HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(SpIddSwapChainProcessor)));
  if (p == nullptr) {
    WdfObjectDelete(reinterpret_cast<WDFOBJECT>(args->hSwapChain));
    return STATUS_INSUFFICIENT_RESOURCES;
  }
  p->device = device;
  p->swapChain = args->hSwapChain;
  p->newFrameEvent = args->hNextSurfaceAvailable;
  p->renderAdapter = args->RenderAdapterLuid;

  HRESULT hr = SpIddCreateD3DDevice(p);
  if (FAILED(hr)) {
    SP_IDD_LOG("IddCreateD3DDevice", hr);
    // Deleting the swap chain tells the OS to generate a new one and try again.
    WdfObjectDelete(reinterpret_cast<WDFOBJECT>(p->swapChain));
    SpIddFreeProcessor(p);
    return STATUS_UNSUCCESSFUL;
  }

  p->terminateEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
  if (p->terminateEvent != nullptr) {
    p->thread = CreateThread(nullptr, 0, SpIddProcessorThread, p, 0, nullptr);
  }
  if (p->thread == nullptr) {
    WdfObjectDelete(reinterpret_cast<WDFOBJECT>(p->swapChain));
    SpIddFreeProcessor(p);
    return STATUS_INSUFFICIENT_RESOURCES;
  }
  *processor = p;
  return STATUS_SUCCESS;
}

void SpIddSwapChainProcessorStop(SpIddSwapChainProcessor* processor) {
  if (processor == nullptr) {
    return;
  }
  SetEvent(processor->terminateEvent);
  WaitForSingleObject(processor->thread, INFINITE);
  SpIddFreeProcessor(processor);
}

// Contract headers must keep the layout the worker was built against.
static_assert(SP_CAPTURE_SLOTS >= 2, "capture ring needs at least double buffering");
static_assert(sizeof(SpCaptureSlot) == 128, "SpCaptureSlot layout (capture_ring.h)");
