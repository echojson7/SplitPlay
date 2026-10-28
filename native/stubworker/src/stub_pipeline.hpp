// SPDX-License-Identifier: MIT
// The Stage 2 stub pipeline: SyntheticCaptureProducer -> capture ring -> RingCaptureSource -> NullEncoder ->
// UdpLoopbackTransport -> UdpLoopbackReceiver (verifies every frame against the test pattern).
#pragma once

#include <atomic>
#include <chrono>
#include <cstdint>
#include <deque>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "splitplay/common/ipc_codec.hpp"
#include "splitplay/common/trace.hpp"
#include "splitplay/pipeline/null_encoder.hpp"
#include "splitplay/pipeline/ring_capture_source.hpp"
#include "splitplay/pipeline/sendinput_sink.hpp"
#include "splitplay/pipeline/synthetic_capture_producer.hpp"
#include "splitplay/pipeline/udp_loopback_transport.hpp"

namespace splitplay::stubworker {

struct PipelineOptions {
  ObjectNamespace ns = ObjectNamespace::local;
  std::string desk = "selftest";
  uint32_t width = 640;
  uint32_t height = 360;
  double fps = 120.0;
  pipeline::AdapterChoice adapter = pipeline::AdapterChoice::automatic;
  uint64_t regenerate_at_frame = 0;  // exercise a texture generation change
};

struct PipelineStats {
  uint64_t produced = 0;
  uint64_t producer_dropped = 0;   // no free slot
  uint64_t acquired = 0;
  uint64_t skipped = 0;            // superseded before the consumer saw them
  uint64_t encoded = 0;
  uint64_t encode_errors = 0;
  uint64_t sent = 0;
  uint64_t received = 0;
  uint64_t verified = 0;
  uint64_t corrupt = 0;
  uint64_t out_of_order = 0;
  uint64_t generation_changes = 0;
  pipeline::ReceiverStats receiver;
  bool gpu_fence_wait = false;
  bool producer_failed = false;
};

class StubPipeline {
 public:
  static std::unique_ptr<StubPipeline> create(const PipelineOptions& options, trace::Recorder* recorder,
                                              std::string* error);
  ~StubPipeline();

  void start();
  void stop();
  void set_fps(double fps);

  // Waits until `frames` frames were sent, then gives the receiver a moment to drain. False on timeout.
  bool wait_for_sent(uint64_t frames, std::chrono::milliseconds timeout);

  PipelineStats stats() const;
  // Heartbeat metrics over the last second (fps, bitrate_kbps, encode_ms_p50/p99, loss_pct, clients, ...).
  ipc::json heartbeat_metrics();
  std::string adapter_description() const;
  uint16_t receiver_port() const;

 private:
  StubPipeline() = default;
  void consumer_loop();
  void on_received(pipeline::UdpLoopbackReceiver::CompletedFrame&& frame);

  PipelineOptions options_;
  std::unique_ptr<trace::Tracer> tracer_;
  std::unique_ptr<pipeline::SyntheticCaptureProducer> producer_;
  std::unique_ptr<pipeline::RingCaptureSource> source_;
  std::unique_ptr<pipeline::NullEncoder> encoder_;
  std::unique_ptr<pipeline::UdpLoopbackReceiver> receiver_;
  std::unique_ptr<pipeline::UdpLoopbackTransport> transport_;

  std::atomic<bool> running_{false};
  std::thread consumer_;

  mutable std::mutex mutex_;
  PipelineStats stats_;
  uint64_t last_received_id_ = 0;
  struct Sample {
    std::chrono::steady_clock::time_point at;
    uint64_t bytes;
    double encode_ms;
  };
  std::deque<Sample> window_;  // frames sent in the last second
  uint64_t last_loss_complete_ = 0, last_loss_incomplete_ = 0;
};

}  // namespace splitplay::stubworker
