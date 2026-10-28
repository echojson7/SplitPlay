// SPDX-License-Identifier: MIT
// Test producer for the capture ring: plays the role of the SplitPlay IDD. Creates the ring section, the FrameReady
// event, SP_CAPTURE_SLOTS named shared textures and a named shared fence per generation on the chosen adapter, and
// publishes a moving test pattern at a fixed rate.
#pragma once

#ifdef _WIN32

#include <atomic>
#include <cstdint>
#include <memory>
#include <string>
#include <thread>
#include <vector>

#include "splitplay/common/capture_ring.hpp"
#include "splitplay/common/object_names.hpp"
#include "splitplay/common/shared_memory.hpp"
#include "splitplay/common/trace.hpp"
#include "splitplay/pipeline/d3d_util.hpp"

namespace splitplay::pipeline {

class SyntheticCaptureProducer {
 public:
  struct Options {
    ObjectNamespace ns = ObjectNamespace::local;
    std::string desk = "selftest";
    uint32_t width = 640;
    uint32_t height = 360;
    double fps = 120.0;
    AdapterChoice adapter = AdapterChoice::automatic;
    uint64_t max_frames = 0;             // 0 = unlimited
    uint64_t regenerate_at_frame = 0;    // recreate the textures (new generation) at this frame id; 0 = never
    bool cpu_wait_for_gpu = false;       // wait for the copy on the CPU before publishing (consumers without fences)
    const trace::Tracer* tracer = nullptr;
  };

  static std::unique_ptr<SyntheticCaptureProducer> create(const Options& options, std::string* error);
  ~SyntheticCaptureProducer();

  void start();
  void stop();
  void set_fps(double fps) noexcept { fps_.store(fps > 0 ? fps : 1.0); }
  void set_cpu_wait_for_gpu(bool on) noexcept { cpu_wait_.store(on); }

  uint64_t frames_published() const noexcept { return published_.load(); }
  uint64_t frames_dropped() const noexcept { return dropped_.load(); }
  uint32_t generation() const noexcept { return generation_; }
  const D3DDevice& device() const noexcept { return device_; }
  bool failed() const noexcept { return failed_.load(); }

 private:
  SyntheticCaptureProducer() = default;
  bool create_generation(uint32_t generation, std::string* error);
  bool produce(uint64_t frame_id);
  void run();

  Options options_;
  D3DDevice device_;
  std::unique_ptr<shm::CaptureRingMapping> ring_;
  std::unique_ptr<capture::Producer> producer_;
  uint32_t generation_ = 0;
  ComPtr<ID3D11Texture2D> textures_[SP_CAPTURE_SLOTS];
  win::UniqueHandle texture_handles_[SP_CAPTURE_SLOTS];
  ComPtr<ID3D11Fence> fence_;
  win::UniqueHandle fence_handle_;
  uint64_t fence_value_ = 0;
  win::UniqueHandle fence_event_;
  std::vector<uint8_t> pixels_;

  win::UniqueHandle stop_event_;
  win::UniqueHandle timer_;
  std::atomic<double> fps_{120.0};
  std::atomic<bool> cpu_wait_{false};
  std::atomic<uint64_t> published_{0};
  std::atomic<uint64_t> dropped_{0};
  std::atomic<bool> failed_{false};
  std::thread thread_;
};

}  // namespace splitplay::pipeline

#endif  // _WIN32
