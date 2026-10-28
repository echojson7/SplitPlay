// SPDX-License-Identifier: MIT
// Stage interfaces of the video/input pipeline (ARCHITECTURE.md §9): capture -> encoder -> transport, and the input
// sink. Stage 2 ships stub implementations (RingCaptureSource, NullEncoder, UdpLoopbackTransport, SendInputSink);
// Stage 3/4 add DDA/IDD capture, NVENC and the RTP/FEC sender behind the same interfaces.
#pragma once

#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

#include "splitplay/capture_ring.h"
#include "splitplay/input_ring.h"

// D3D11 types are only referenced by pointer so this header stays portable.
struct ID3D11Device;
struct ID3D11DeviceContext;
struct ID3D11Texture2D;

namespace splitplay::pipeline {

// ---- capture --------------------------------------------------------------------------------------
struct FrameInfo {
  uint64_t frame_id = 0;      // assigned by the producer, also the trace id
  uint32_t generation = 0;
  uint32_t slot = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t pixel_format = SP_PIXEL_BGRA8;
  uint32_t color_space = SP_COLOR_SRGB;
  bool dirty = true;
  uint64_t present_qpc = 0;
  uint64_t acquire_qpc = 0;
  uint64_t publish_qpc = 0;
  uint64_t wake_qpc = 0;      // consumer woke on FrameReady
  uint64_t skipped = 0;       // frames published since the previous acquire that were never seen
  SpCursorState cursor{};
};

// A frame on the GPU, valid between ICaptureSource::acquire() and release(). The texture is already ordered after
// the producer's fence on `context` (GPU-side wait), so it can be read immediately.
struct CapturedFrame {
  FrameInfo info;
  ID3D11Device* device = nullptr;
  ID3D11DeviceContext* context = nullptr;
  ID3D11Texture2D* texture = nullptr;
};

enum class AcquireStatus {
  ok,           // `frame` is filled and must be released
  timeout,      // no new frame within the timeout
  interrupted,  // interrupt() was called
  lost,         // the producer went away or the ring became invalid; reopen the source
  error,
};

struct CaptureStats {
  uint64_t frames_acquired = 0;
  uint64_t frames_skipped = 0;    // superseded before the consumer got to them
  uint64_t spurious_wakeups = 0;
  uint64_t generation_changes = 0;
  uint64_t producer_dropped = 0;  // header.frames_dropped (no free slot on the producer side)
};

class ICaptureSource {
 public:
  virtual ~ICaptureSource() = default;
  // Waits for the next frame (event driven, never polls on a timer).
  virtual AcquireStatus acquire(CapturedFrame& frame, uint32_t timeout_ms) = 0;
  // Returns the slot to the producer once the GPU no longer reads the texture.
  virtual void release(CapturedFrame& frame) = 0;
  // Unblocks a pending acquire() from another thread.
  virtual void interrupt() = 0;
  virtual CaptureStats stats() const = 0;
  virtual std::string_view name() const = 0;
};

// ---- encoder --------------------------------------------------------------------------------------
struct EncoderConfig {
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t fps = 60;
  uint32_t bitrate_kbps = 20000;
  std::string codec = "hevc";
  uint32_t dpb_frames = 16;
};

struct EncodedFrame {
  uint64_t frame_id = 0;
  bool keyframe = false;
  uint64_t content_hash = 0;      // stub encoders: hash of the source pixels, for end-to-end verification
  uint64_t convert_done_qpc = 0;
  uint64_t encode_done_qpc = 0;
  std::vector<uint8_t> bitstream;
};

class IEncoder {
 public:
  virtual ~IEncoder() = default;
  virtual bool configure(const EncoderConfig& config, std::string* error) = 0;
  virtual bool encode(const CapturedFrame& frame, EncodedFrame& out, std::string* error) = 0;
  virtual void request_keyframe() = 0;
  virtual std::string_view name() const = 0;
};

// ---- transport ------------------------------------------------------------------------------------
struct SendResult {
  uint32_t packets = 0;
  uint64_t bytes = 0;
  uint64_t first_packet_qpc = 0;
  uint64_t last_packet_qpc = 0;
};

struct TransportStats {
  uint64_t frames_sent = 0;
  uint64_t packets_sent = 0;
  uint64_t bytes_sent = 0;
  uint64_t send_errors = 0;
};

class ITransport {
 public:
  virtual ~ITransport() = default;
  virtual bool send(const EncodedFrame& frame, SendResult& result, std::string* error) = 0;
  virtual TransportStats stats() const = 0;
  virtual std::string_view name() const = 0;
};

// ---- input ----------------------------------------------------------------------------------------
// Receives Moonlight keyboard/mouse input already translated to SpInputEvent. Implementations: SendInput (MVP)
// and the RDP sink that forwards to splitplay-seat over the input ring (Stage 4).
class IInputSink {
 public:
  virtual ~IInputSink() = default;
  virtual bool inject(const SpInputEvent& event) = 0;
  // Client disconnected or lost focus: release every held key and button.
  virtual void release_all() = 0;
  // True if relative mouse motion should go through this sink (otherwise the caller keeps it on SendInput).
  virtual bool supports_relative_mouse() const = 0;
  virtual std::string_view name() const = 0;
};

}  // namespace splitplay::pipeline
