// SPDX-License-Identifier: MIT
#include "splitplay/pipeline/null_encoder.hpp"

#include "splitplay/common/qpc.hpp"
#include "splitplay/common/win_util.hpp"
#include "splitplay/pipeline/null_bitstream.hpp"

namespace splitplay::pipeline {

bool NullEncoder::configure(const EncoderConfig& config, std::string* error) {
  (void)error;
  config_ = config;
  keyframe_.store(true);
  return true;
}

bool NullEncoder::encode(const CapturedFrame& frame, EncodedFrame& out, std::string* error) {
  if (!frame.texture || !frame.device || !frame.context) {
    if (error) *error = "null encoder: frame has no texture";
    return false;
  }
  D3D11_TEXTURE2D_DESC desc{};
  frame.texture->GetDesc(&desc);
  if (desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM) {
    if (error) *error = "null encoder: only BGRA8 frames are supported";
    return false;
  }
  if (!staging_ || staging_device_ != frame.device || staging_desc_.Width != desc.Width ||
      staging_desc_.Height != desc.Height) {
    D3D11_TEXTURE2D_DESC sd = desc;
    sd.Usage = D3D11_USAGE_STAGING;
    sd.BindFlags = 0;
    sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    sd.MiscFlags = 0;
    staging_.Reset();
    const HRESULT hr = frame.device->CreateTexture2D(&sd, nullptr, &staging_);
    if (FAILED(hr)) {
      if (error) *error = "null encoder: CreateTexture2D(staging) failed: " + win::hresult_message(hr);
      return false;
    }
    staging_device_ = frame.device;
    staging_desc_ = sd;
  }
  frame.context->CopyResource(staging_.Get(), frame.texture);
  D3D11_MAPPED_SUBRESOURCE mapped{};
  const HRESULT hr = frame.context->Map(staging_.Get(), 0, D3D11_MAP_READ, 0, &mapped);
  if (FAILED(hr)) {
    if (error) *error = "null encoder: Map failed: " + win::hresult_message(hr);
    return false;
  }
  out.convert_done_qpc = qpc_now();
  if (tracer_) tracer_->stamp(frame.info.frame_id, SP_STAGE_CONVERT_DONE, out.convert_done_qpc);

  out.frame_id = frame.info.frame_id;
  out.keyframe = keyframe_.exchange(false);
  out.bitstream = build_null_bitstream(frame.info.frame_id, desc.Width, desc.Height,
                                       static_cast<const uint8_t*>(mapped.pData), mapped.RowPitch, out.keyframe, step_,
                                       &out.content_hash);
  frame.context->Unmap(staging_.Get(), 0);
  out.encode_done_qpc = qpc_now();
  if (tracer_) tracer_->stamp(frame.info.frame_id, SP_STAGE_ENCODE_DONE, out.encode_done_qpc);
  return true;
}

}  // namespace splitplay::pipeline
