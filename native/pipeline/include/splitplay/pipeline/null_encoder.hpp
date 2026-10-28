// SPDX-License-Identifier: MIT
// Stub encoder: reads the captured texture back (stands in for colour conversion + NVENC), hashes it and emits a
// NullBitstream (header + thumbnail) instead of a real bitstream.
#pragma once

#ifdef _WIN32

#include <atomic>

#include "splitplay/common/trace.hpp"
#include "splitplay/pipeline/d3d_util.hpp"
#include "splitplay/pipeline/interfaces.hpp"

namespace splitplay::pipeline {

class NullEncoder final : public IEncoder {
 public:
  explicit NullEncoder(const trace::Tracer* tracer = nullptr, uint32_t thumbnail_step = 8)
      : tracer_(tracer), step_(thumbnail_step) {}

  bool configure(const EncoderConfig& config, std::string* error) override;
  bool encode(const CapturedFrame& frame, EncodedFrame& out, std::string* error) override;
  void request_keyframe() override { keyframe_.store(true); }
  std::string_view name() const override { return "null"; }

 private:
  const trace::Tracer* tracer_;
  uint32_t step_;
  EncoderConfig config_;
  std::atomic<bool> keyframe_{true};
  ComPtr<ID3D11Texture2D> staging_;
  ID3D11Device* staging_device_ = nullptr;
  D3D11_TEXTURE2D_DESC staging_desc_{};
};

}  // namespace splitplay::pipeline

#endif  // _WIN32
