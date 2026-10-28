// SPDX-License-Identifier: MIT
#include "stub_pipeline.hpp"

#include <algorithm>

#include "splitplay/common/log.hpp"
#include "splitplay/common/qpc.hpp"
#include "splitplay/pipeline/null_bitstream.hpp"

namespace splitplay::stubworker {

using namespace std::chrono_literals;

std::unique_ptr<StubPipeline> StubPipeline::create(const PipelineOptions& options, trace::Recorder* recorder,
                                                   std::string* error) {
  std::unique_ptr<StubPipeline> p(new StubPipeline());
  p->options_ = options;
  p->tracer_ = std::make_unique<trace::Tracer>(options.desk, recorder);

  pipeline::SyntheticCaptureProducer::Options po;
  po.ns = options.ns;
  po.desk = options.desk;
  po.width = options.width;
  po.height = options.height;
  po.fps = options.fps;
  po.adapter = options.adapter;
  po.regenerate_at_frame = options.regenerate_at_frame;
  po.tracer = p->tracer_.get();
  p->producer_ = pipeline::SyntheticCaptureProducer::create(po, error);
  if (!p->producer_) return nullptr;

  pipeline::RingCaptureSource::Options so;
  so.ns = options.ns;
  so.desk = options.desk;
  so.tracer = p->tracer_.get();
  p->source_ = pipeline::RingCaptureSource::open(so, error);
  if (!p->source_) return nullptr;
  if (!p->source_->gpu_fence_wait()) p->producer_->set_cpu_wait_for_gpu(true);

  p->encoder_ = std::make_unique<pipeline::NullEncoder>(p->tracer_.get(), 8);
  pipeline::EncoderConfig ec;
  ec.width = options.width;
  ec.height = options.height;
  ec.fps = static_cast<uint32_t>(options.fps);
  if (!p->encoder_->configure(ec, error)) return nullptr;

  p->receiver_ = pipeline::UdpLoopbackReceiver::create(error);
  if (!p->receiver_) return nullptr;
  p->transport_ = pipeline::UdpLoopbackTransport::create({p->receiver_->port(), 1200, p->tracer_.get()}, error);
  if (!p->transport_) return nullptr;
  return p;
}

StubPipeline::~StubPipeline() { stop(); }

void StubPipeline::start() {
  if (running_.exchange(true)) return;
  StubPipeline* self = this;
  receiver_->start([self](pipeline::UdpLoopbackReceiver::CompletedFrame&& f) { self->on_received(std::move(f)); });
  consumer_ = std::thread([this] { consumer_loop(); });
  producer_->start();
}

void StubPipeline::stop() {
  if (producer_) producer_->stop();
  running_.store(false);
  if (source_) source_->interrupt();
  if (consumer_.joinable()) consumer_.join();
  if (receiver_) receiver_->stop();
}

void StubPipeline::set_fps(double fps) {
  if (producer_) producer_->set_fps(fps);
}

std::string StubPipeline::adapter_description() const { return producer_ ? producer_->device().description : ""; }

uint16_t StubPipeline::receiver_port() const { return receiver_ ? receiver_->port() : 0; }

void StubPipeline::consumer_loop() {
  SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);
  int errors_logged = 0;
  while (running_.load()) {
    pipeline::CapturedFrame frame;
    const auto status = source_->acquire(frame, 200);
    if (status == pipeline::AcquireStatus::interrupted || status == pipeline::AcquireStatus::timeout) continue;
    if (status != pipeline::AcquireStatus::ok) {
      log_error("stub pipeline: capture source lost the ring");
      std::this_thread::sleep_for(100ms);
      continue;
    }
    pipeline::EncodedFrame encoded;
    std::string err;
    const bool ok = encoder_->encode(frame, encoded, &err);
    const uint64_t wake_qpc = frame.info.wake_qpc;
    source_->release(frame);
    if (!ok) {
      std::lock_guard lock(mutex_);
      ++stats_.encode_errors;
      if (errors_logged++ < 5) log_error("stub pipeline: encode failed: {}", err);
      continue;
    }
    pipeline::SendResult sent;
    const bool sent_ok = transport_->send(encoded, sent, &err);
    std::lock_guard lock(mutex_);
    ++stats_.encoded;
    if (!sent_ok) {
      if (errors_logged++ < 5) log_error("stub pipeline: send failed: {}", err);
      continue;
    }
    ++stats_.sent;
    window_.push_back({std::chrono::steady_clock::now(), sent.bytes, qpc_to_ms(encoded.encode_done_qpc - wake_qpc)});
    while (window_.size() > 4096) window_.pop_front();
  }
}

void StubPipeline::on_received(pipeline::UdpLoopbackReceiver::CompletedFrame&& frame) {
  pipeline::NullBitstreamInfo info;
  std::string err;
  const bool ok = pipeline::verify_null_bitstream(frame.data.data(), frame.data.size(), /*expect_test_pattern=*/true,
                                                  &info, &err) &&
                  info.frame_id == frame.frame_id;
  std::lock_guard lock(mutex_);
  ++stats_.received;
  if (ok) {
    ++stats_.verified;
  } else {
    if (stats_.corrupt++ < 5) log_error("stub pipeline: frame {} failed verification: {}", frame.frame_id, err);
  }
  if (frame.frame_id <= last_received_id_) ++stats_.out_of_order;
  last_received_id_ = std::max(last_received_id_, frame.frame_id);
}

bool StubPipeline::wait_for_sent(uint64_t frames, std::chrono::milliseconds timeout) {
  const auto deadline = std::chrono::steady_clock::now() + timeout;
  for (;;) {
    {
      std::lock_guard lock(mutex_);
      if (stats_.sent >= frames) break;
    }
    if (producer_->failed() || std::chrono::steady_clock::now() >= deadline) return false;
    std::this_thread::sleep_for(10ms);
  }
  // Let the receiver drain what is in flight.
  const auto drain_deadline = std::chrono::steady_clock::now() + 2s;
  while (std::chrono::steady_clock::now() < drain_deadline) {
    {
      std::lock_guard lock(mutex_);
      if (stats_.received >= stats_.sent) break;
    }
    std::this_thread::sleep_for(10ms);
  }
  return true;
}

PipelineStats StubPipeline::stats() const {
  PipelineStats s;
  {
    std::lock_guard lock(mutex_);
    s = stats_;
  }
  s.produced = producer_->frames_published();
  s.producer_dropped = producer_->frames_dropped();
  s.producer_failed = producer_->failed();
  const auto cs = source_->stats();
  s.acquired = cs.frames_acquired;
  s.skipped = cs.frames_skipped;
  s.generation_changes = cs.generation_changes;
  s.gpu_fence_wait = source_->gpu_fence_wait();
  s.receiver = receiver_->stats();
  return s;
}

ipc::json StubPipeline::heartbeat_metrics() {
  const auto now = std::chrono::steady_clock::now();
  std::vector<double> encode_ms;
  uint64_t bytes = 0;
  PipelineStats totals = stats();
  {
    std::lock_guard lock(mutex_);
    while (!window_.empty() && now - window_.front().at > 1s) window_.pop_front();
    for (const auto& s : window_) {
      bytes += s.bytes;
      encode_ms.push_back(s.encode_ms);
    }
  }
  std::sort(encode_ms.begin(), encode_ms.end());
  auto pct = [&](double p) {
    if (encode_ms.empty()) return 0.0;
    return encode_ms[std::min(encode_ms.size() - 1, static_cast<size_t>(p * static_cast<double>(encode_ms.size() - 1) + 0.5))];
  };
  const uint64_t complete = totals.receiver.frames_complete - last_loss_complete_;
  const uint64_t incomplete = totals.receiver.frames_incomplete - last_loss_incomplete_;
  last_loss_complete_ = totals.receiver.frames_complete;
  last_loss_incomplete_ = totals.receiver.frames_incomplete;
  const double loss = complete + incomplete ? 100.0 * static_cast<double>(incomplete) / static_cast<double>(complete + incomplete) : 0.0;
  return ipc::json{{"fps", encode_ms.size()},
                   {"bitrate_kbps", bytes * 8 / 1000},
                   {"encode_ms_p50", pct(0.50)},
                   {"encode_ms_p99", pct(0.99)},
                   {"loss_pct", loss},
                   {"clients", 0},
                   {"frames_produced", totals.produced},
                   {"frames_sent", totals.sent},
                   {"frames_verified", totals.verified},
                   {"frames_corrupt", totals.corrupt}};
}

}  // namespace splitplay::stubworker
