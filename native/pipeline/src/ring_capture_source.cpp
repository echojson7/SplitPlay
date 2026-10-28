// SPDX-License-Identifier: MIT
#include "splitplay/pipeline/ring_capture_source.hpp"

#include <chrono>
#include <thread>

#include "splitplay/common/log.hpp"
#include "splitplay/common/qpc.hpp"
#include "splitplay/common/shared_atomic.hpp"

namespace splitplay::pipeline {

std::unique_ptr<RingCaptureSource> RingCaptureSource::open(const Options& options, std::string* error) {
  std::unique_ptr<RingCaptureSource> s(new RingCaptureSource());
  s->options_ = options;
  s->interrupt_event_.reset(CreateEventW(nullptr, FALSE, FALSE, nullptr));

  // The producer may still be starting: retry until the ring exists and announces a texture generation.
  const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(options.open_timeout_ms);
  std::string err;
  for (;;) {
    s->ring_ = shm::CaptureRingMapping::open(options.ns, options.desk, &err);
    if (s->ring_ && shm::load_acquire(s->ring_->header()->generation) != 0) break;
    if (s->ring_) err = "capture ring has no texture generation yet";
    s->ring_.reset();
    if (std::chrono::steady_clock::now() >= deadline) {
      if (error) *error = err;
      return nullptr;
    }
    std::this_thread::sleep_for(std::chrono::milliseconds(20));
  }
  SpCaptureRingHeader* h = s->ring_->header();
  shm::store_release(h->consumer_pid, GetCurrentProcessId());
  s->consumer_ = std::make_unique<capture::Consumer>(h);

  auto dev = create_device_for_luid(h->adapter_luid, error);
  if (!dev) return nullptr;
  s->device_ = std::move(*dev);

  D3D11_QUERY_DESC qd{D3D11_QUERY_EVENT, 0};
  s->device_.device->CreateQuery(&qd, &s->query_);

  if (!s->open_generation(shm::load_acquire(h->generation), error)) return nullptr;
  log_info("capture ring consumer: {}x{} format {} on \"{}\", fence wait {}", h->width, h->height, h->pixel_format,
           s->device_.description, s->fence_ ? "on the GPU" : "unavailable (producer waits on the CPU)");
  return s;
}

RingCaptureSource::~RingCaptureSource() {
  if (ring_) shm::store_release(ring_->header()->consumer_pid, 0u);
}

bool RingCaptureSource::open_generation(uint32_t generation, std::string* error) {
  ComPtr<ID3D11Texture2D> textures[SP_CAPTURE_SLOTS];
  for (uint32_t slot = 0; slot < SP_CAPTURE_SLOTS; ++slot) {
    const std::wstring name = win::widen(frame_texture_name(options_.ns, options_.desk, generation, slot));
    HRESULT hr = device_.device->OpenSharedResourceByName(
        name.c_str(), DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, IID_PPV_ARGS(&textures[slot]));
    if (FAILED(hr)) {
      if (error) *error = "OpenSharedResourceByName(" + win::narrow(name) + ") failed: " + win::hresult_message(hr);
      return false;
    }
  }
  std::string fence_error;
  fence_ = open_shared_fence_by_name(
      device_, win::widen(frame_fence_name(options_.ns, options_.desk, generation)), &fence_error);
  if (!fence_ && !fence_warning_logged_) {
    log_warn("capture ring consumer: cannot open the producer fence ({}); continuing without GPU fence waits",
             fence_error);
    fence_warning_logged_ = true;
  }
  for (uint32_t slot = 0; slot < SP_CAPTURE_SLOTS; ++slot) textures_[slot] = std::move(textures[slot]);
  if (generation_ != 0) {
    std::lock_guard lock(stats_mutex_);
    ++stats_.generation_changes;
  }
  generation_ = generation;
  return true;
}

AcquireStatus RingCaptureSource::acquire(CapturedFrame& frame, uint32_t timeout_ms) {
  const uint64_t start = qpc_now();
  const uint64_t budget = qpc_from_us(static_cast<double>(timeout_ms) * 1000.0);
  for (;;) {
    const uint64_t elapsed = qpc_now() - start;
    const DWORD wait_ms = elapsed >= budget ? 0 : static_cast<DWORD>(qpc_to_ms(budget - elapsed) + 0.5);
    HANDLE handles[2] = {ring_->frame_ready->handle(), interrupt_event_.get()};
    const DWORD rc = WaitForMultipleObjects(2, handles, FALSE, wait_ms);
    if (rc == WAIT_OBJECT_0 + 1) return AcquireStatus::interrupted;
    if (rc == WAIT_TIMEOUT) return AcquireStatus::timeout;
    if (rc != WAIT_OBJECT_0) return AcquireStatus::error;
    const uint64_t wake_qpc = qpc_now();

    capture::AcquiredSlot slot;
    if (!consumer_->try_acquire(slot)) {
      std::lock_guard lock(stats_mutex_);
      ++stats_.spurious_wakeups;
      continue;
    }
    if (slot.info.generation != generation_) {
      std::string err;
      if (!open_generation(slot.info.generation, &err)) {
        consumer_->release(slot.slot);
        log_error("capture ring consumer: {}", err);
        return AcquireStatus::lost;
      }
    }
    if (fence_) device_.context->Wait(fence_.Get(), slot.info.fence_value);

    SpCaptureRingHeader* h = ring_->header();
    frame.device = device_.device.Get();
    frame.context = device_.context.Get();
    frame.texture = textures_[slot.slot].Get();
    FrameInfo& fi = frame.info;
    fi.frame_id = slot.info.frame_id;
    fi.generation = slot.info.generation;
    fi.slot = slot.slot;
    fi.width = h->width;
    fi.height = h->height;
    fi.pixel_format = h->pixel_format;
    fi.color_space = h->color_space;
    fi.dirty = slot.info.dirty != 0;
    fi.present_qpc = slot.info.present_qpc;
    fi.acquire_qpc = slot.info.acquire_qpc;
    fi.publish_qpc = slot.info.publish_qpc;
    fi.wake_qpc = wake_qpc;
    fi.skipped = slot.skipped;
    fi.cursor = slot.info.cursor;
    if (options_.tracer) options_.tracer->stamp(fi.frame_id, SP_STAGE_WORKER_WAKE, wake_qpc);
    {
      std::lock_guard lock(stats_mutex_);
      ++stats_.frames_acquired;
      stats_.frames_skipped += slot.skipped;
      stats_.producer_dropped = shm::load_relaxed(h->frames_dropped);
    }
    return AcquireStatus::ok;
  }
}

void RingCaptureSource::release(CapturedFrame& frame) {
  if (frame.texture == nullptr) return;
  // The slot may only be reused once our GPU work that reads it has finished.
  if (query_) {
    device_.context->End(query_.Get());
    device_.context->Flush();
    BOOL done = FALSE;
    while (device_.context->GetData(query_.Get(), &done, sizeof(done), 0) == S_FALSE) std::this_thread::yield();
  }
  consumer_->release(frame.info.slot);
  frame.texture = nullptr;
}

void RingCaptureSource::interrupt() { SetEvent(interrupt_event_.get()); }

CaptureStats RingCaptureSource::stats() const {
  std::lock_guard lock(stats_mutex_);
  return stats_;
}

}  // namespace splitplay::pipeline
