// SPDX-License-Identifier: MIT
// Consumer of the capture ring (capture_ring.h): event-driven acquisition of the latest frame, GPU wait on the
// producer's shared fence, re-opening textures when the generation changes.
#pragma once

#ifdef _WIN32

#include <atomic>
#include <memory>
#include <mutex>
#include <string>

#include "splitplay/common/capture_ring.hpp"
#include "splitplay/common/object_names.hpp"
#include "splitplay/common/shared_memory.hpp"
#include "splitplay/common/trace.hpp"
#include "splitplay/pipeline/d3d_util.hpp"
#include "splitplay/pipeline/interfaces.hpp"

namespace splitplay::pipeline {

class RingCaptureSource final : public ICaptureSource {
 public:
  struct Options {
    ObjectNamespace ns = ObjectNamespace::global;
    std::string desk;
    uint32_t open_timeout_ms = 5000;   // wait this long for the producer to create the ring and its first generation
    const trace::Tracer* tracer = nullptr;  // stamps WORKER_WAKE
  };

  static std::unique_ptr<RingCaptureSource> open(const Options& options, std::string* error);
  ~RingCaptureSource() override;

  AcquireStatus acquire(CapturedFrame& frame, uint32_t timeout_ms) override;
  void release(CapturedFrame& frame) override;
  void interrupt() override;
  CaptureStats stats() const override;
  std::string_view name() const override { return "capture-ring"; }

  // False if the producer's fence could not be opened (no D3D12 on the adapter): frames are then not GPU-ordered
  // after the producer's copy and the producer must wait for its copy on the CPU before publishing.
  bool gpu_fence_wait() const noexcept { return fence_ != nullptr; }
  const D3DDevice& device() const noexcept { return device_; }
  SpCaptureRingHeader* header() const noexcept { return ring_->header(); }

 private:
  RingCaptureSource() = default;
  bool open_generation(uint32_t generation, std::string* error);

  Options options_;
  std::unique_ptr<shm::CaptureRingMapping> ring_;
  std::unique_ptr<capture::Consumer> consumer_;
  D3DDevice device_;
  uint32_t generation_ = 0;
  ComPtr<ID3D11Texture2D> textures_[SP_CAPTURE_SLOTS];
  ComPtr<ID3D11Fence> fence_;
  bool fence_warning_logged_ = false;
  ComPtr<ID3D11Query> query_;
  win::UniqueHandle interrupt_event_;
  mutable std::mutex stats_mutex_;
  CaptureStats stats_;
};

}  // namespace splitplay::pipeline

#endif  // _WIN32
