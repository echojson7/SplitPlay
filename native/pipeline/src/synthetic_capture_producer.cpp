// SPDX-License-Identifier: MIT
#include "splitplay/pipeline/synthetic_capture_producer.hpp"

#include <cmath>
#include <format>

#include "splitplay/common/log.hpp"
#include "splitplay/common/qpc.hpp"
#include "splitplay/pipeline/test_pattern.hpp"

#ifndef CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
#define CREATE_WAITABLE_TIMER_HIGH_RESOLUTION 0x00000002
#endif

namespace splitplay::pipeline {

std::unique_ptr<SyntheticCaptureProducer> SyntheticCaptureProducer::create(const Options& options, std::string* error) {
  if (options.width == 0 || options.height == 0 || options.width > 8192 || options.height > 8192) {
    if (error) *error = "invalid test frame size";
    return nullptr;
  }
  std::unique_ptr<SyntheticCaptureProducer> p(new SyntheticCaptureProducer());
  p->options_ = options;
  p->fps_.store(options.fps > 0 ? options.fps : 60.0);
  p->cpu_wait_.store(options.cpu_wait_for_gpu);

  auto dev = create_device(options.adapter, error);
  if (!dev) return nullptr;
  p->device_ = std::move(*dev);

  capture::RingFormat format;
  format.width = options.width;
  format.height = options.height;
  format.refresh_num = static_cast<uint32_t>(std::lround(p->fps_.load()));
  format.refresh_den = 1;
  format.pixel_format = SP_PIXEL_BGRA8;
  format.color_space = SP_COLOR_SRGB;
  format.adapter_luid = p->device_.luid;
  p->ring_ = shm::CaptureRingMapping::create(options.ns, options.desk, format, error);
  if (!p->ring_) return nullptr;
  p->producer_ = std::make_unique<capture::Producer>(p->ring_->header());

  p->pixels_.resize(static_cast<size_t>(options.width) * options.height * 4u);
  p->fence_event_.reset(CreateEventW(nullptr, FALSE, FALSE, nullptr));
  p->stop_event_.reset(CreateEventW(nullptr, TRUE, FALSE, nullptr));
  p->timer_.reset(CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS));
  if (!p->timer_) p->timer_.reset(CreateWaitableTimerW(nullptr, FALSE, nullptr));
  if (!p->fence_event_ || !p->stop_event_ || !p->timer_) {
    if (error) *error = "CreateEvent/CreateWaitableTimer failed: " + win::last_error_message();
    return nullptr;
  }
  if (!p->create_generation(1, error)) return nullptr;
  log_info("synthetic producer: {}x{} @ {} fps on \"{}\" (LUID 0x{:016x}{}), ring {}", options.width, options.height,
           p->fps_.load(), p->device_.description, p->device_.luid, p->device_.software ? ", software" : "",
           p->ring_->section->name());
  return p;
}

SyntheticCaptureProducer::~SyntheticCaptureProducer() { stop(); }

bool SyntheticCaptureProducer::create_generation(uint32_t generation, std::string* error) {
  D3D11_TEXTURE2D_DESC desc{};
  desc.Width = options_.width;
  desc.Height = options_.height;
  desc.MipLevels = 1;
  desc.ArraySize = 1;
  desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
  desc.SampleDesc.Count = 1;
  desc.Usage = D3D11_USAGE_DEFAULT;
  desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
  desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_SHARED_NTHANDLE;

  ComPtr<ID3D11Texture2D> textures[SP_CAPTURE_SLOTS];
  win::UniqueHandle handles[SP_CAPTURE_SLOTS];
  for (uint32_t slot = 0; slot < SP_CAPTURE_SLOTS; ++slot) {
    HRESULT hr = device_.device->CreateTexture2D(&desc, nullptr, &textures[slot]);
    if (FAILED(hr)) {
      if (error) *error = "CreateTexture2D (shared NT handle) failed: " + win::hresult_message(hr);
      return false;
    }
    ComPtr<IDXGIResource1> resource;
    hr = textures[slot].As(&resource);
    HANDLE h = nullptr;
    const std::wstring name = win::widen(frame_texture_name(options_.ns, options_.desk, generation, slot));
    if (SUCCEEDED(hr))
      hr = resource->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, name.c_str(),
                                        &h);
    if (FAILED(hr)) {
      if (error) *error = "CreateSharedHandle(" + win::narrow(name) + ") failed: " + win::hresult_message(hr);
      return false;
    }
    handles[slot].reset(h);
  }

  ComPtr<ID3D11Fence> fence;
  HRESULT hr = device_.device->CreateFence(0, D3D11_FENCE_FLAG_SHARED, IID_PPV_ARGS(&fence));
  if (FAILED(hr)) {
    if (error) *error = "CreateFence(shared) failed: " + win::hresult_message(hr);
    return false;
  }
  const std::wstring fence_name = win::widen(frame_fence_name(options_.ns, options_.desk, generation));
  HANDLE fh = nullptr;
  hr = fence->CreateSharedHandle(nullptr, GENERIC_ALL, fence_name.c_str(), &fh);
  if (FAILED(hr)) {
    if (error) *error = "ID3D11Fence::CreateSharedHandle(" + win::narrow(fence_name) + ") failed: " +
                        win::hresult_message(hr);
    return false;
  }

  for (uint32_t slot = 0; slot < SP_CAPTURE_SLOTS; ++slot) {
    textures_[slot] = std::move(textures[slot]);
    texture_handles_[slot] = std::move(handles[slot]);
  }
  fence_ = std::move(fence);
  fence_handle_.reset(fh);
  fence_value_ = 0;
  generation_ = generation;
  producer_->set_generation(generation, options_.width, options_.height);
  return true;
}

bool SyntheticCaptureProducer::produce(uint64_t frame_id) {
  const uint64_t present_qpc = qpc_now();  // the synthetic "vsync"
  if (options_.regenerate_at_frame != 0 && frame_id == options_.regenerate_at_frame) {
    std::string err;
    if (!create_generation(generation_ + 1, &err)) {
      log_error("synthetic producer: generation change failed: {}", err);
      return false;
    }
    log_info("synthetic producer: textures recreated, generation {}", generation_);
  }

  auto slot = producer_->begin_frame();
  if (!slot) {
    ++dropped_;
    return true;  // every slot busy: drop, never block (like the IDD)
  }
  const uint64_t acquire_qpc = qpc_now();
  if (options_.tracer) {
    options_.tracer->stamp(frame_id, SP_STAGE_PRESENT, present_qpc);
    options_.tracer->stamp(frame_id, SP_STAGE_IDD_ACQUIRE, acquire_qpc);
  }

  const size_t pitch = static_cast<size_t>(options_.width) * 4u;
  fill_pattern(frame_id, options_.width, options_.height, pixels_.data(), pitch);
  device_.context->UpdateSubresource(textures_[*slot].Get(), 0, nullptr, pixels_.data(), static_cast<UINT>(pitch), 0);
  const uint64_t fence_value = ++fence_value_;
  HRESULT hr = device_.context->Signal(fence_.Get(), fence_value);
  if (FAILED(hr)) {
    log_error("synthetic producer: fence Signal failed: {}", win::hresult_message(hr));
    return false;
  }
  device_.context->Flush();
  if (cpu_wait_.load()) {
    if (SUCCEEDED(fence_->SetEventOnCompletion(fence_value, fence_event_.get())))
      WaitForSingleObject(fence_event_.get(), 5000);
  }

  capture::PublishInfo info;
  info.frame_id = frame_id;
  info.present_qpc = present_qpc;
  info.acquire_qpc = acquire_qpc;
  info.fence_value = fence_value;
  info.generation = generation_;
  info.dirty = 1;
  // A synthetic cursor circling the centre of the frame.
  const double angle = static_cast<double>(frame_id) * 0.05;
  info.cursor.x = static_cast<int32_t>(options_.width / 2 + std::cos(angle) * options_.height / 4);
  info.cursor.y = static_cast<int32_t>(options_.height / 2 + std::sin(angle) * options_.height / 4);
  info.cursor.visible = 1;
  info.cursor.shape_id = 1;
  info.cursor.width = 32;
  info.cursor.height = 32;
  info.publish_qpc = qpc_now();
  if (options_.tracer) options_.tracer->stamp(frame_id, SP_STAGE_IDD_PUBLISH, info.publish_qpc);
  producer_->publish(*slot, info);
  producer_->heartbeat(info.publish_qpc);
  ring_->frame_ready->set();
  ++published_;
  return true;
}

void SyntheticCaptureProducer::run() {
  SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);
  uint64_t next = qpc_now();
  for (uint64_t frame_id = 1; options_.max_frames == 0 || frame_id <= options_.max_frames; ++frame_id) {
    if (!produce(frame_id)) {
      failed_.store(true);
      return;
    }
    next += static_cast<uint64_t>(static_cast<double>(qpc_frequency()) / fps_.load());
    const uint64_t now = qpc_now();
    if (next <= now) {
      next = now;  // running late: do not try to catch up with a burst
      if (WaitForSingleObject(stop_event_.get(), 0) == WAIT_OBJECT_0) return;
      continue;
    }
    LARGE_INTEGER due;
    due.QuadPart = -static_cast<LONGLONG>(qpc_to_us(next - now) * 10.0);  // relative, 100 ns units
    if (due.QuadPart == 0) due.QuadPart = -1;
    SetWaitableTimer(timer_.get(), &due, 0, nullptr, nullptr, FALSE);
    HANDLE handles[2] = {stop_event_.get(), timer_.get()};
    if (WaitForMultipleObjects(2, handles, FALSE, INFINITE) == WAIT_OBJECT_0) return;
  }
}

void SyntheticCaptureProducer::start() {
  if (thread_.joinable()) return;
  ResetEvent(stop_event_.get());
  thread_ = std::thread([this] { run(); });
}

void SyntheticCaptureProducer::stop() {
  if (stop_event_) SetEvent(stop_event_.get());
  if (thread_.joinable()) thread_.join();
}

}  // namespace splitplay::pipeline
