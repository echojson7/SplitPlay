// SPDX-License-Identifier: MIT
// Stub network stage: splits each encoded frame into UDP datagrams sent to 127.0.0.1, and a receiver that reassembles
// them. Stands in for the RTP/FEC sender until the worker (Sunshine) owns the network in Stage 3.
#pragma once

#include <atomic>
#include <cstdint>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "splitplay/common/trace.hpp"
#include "splitplay/pipeline/interfaces.hpp"

namespace splitplay::pipeline {

#pragma pack(push, 1)
struct UdpPacketHeader {
  uint32_t magic;          // kUdpPacketMagic
  uint16_t version;        // 1
  uint16_t header_bytes;   // sizeof(UdpPacketHeader)
  uint64_t frame_id;
  uint32_t frame_bytes;    // total size of the frame payload
  uint32_t offset;         // offset of this packet's payload in the frame
  uint16_t index;          // packet index within the frame
  uint16_t count;          // packets in the frame
  uint16_t payload_bytes;
  uint16_t flags;          // bit0 keyframe
  uint64_t send_qpc;       // QPC when the packet was handed to the socket
};
#pragma pack(pop)
static_assert(sizeof(UdpPacketHeader) == 40, "UdpPacketHeader layout");
inline constexpr uint32_t kUdpPacketMagic = 0x50555053u;  // 'SPUP'

// Process-wide socket library initialisation (WSAStartup on Windows). Returns false on failure.
bool init_sockets(std::string* error);

class UdpLoopbackTransport final : public ITransport {
 public:
  struct Options {
    uint16_t port = 0;              // receiver port on 127.0.0.1
    uint32_t max_payload = 1200;    // payload bytes per datagram
    const trace::Tracer* tracer = nullptr;  // stamps FIRST_PACKET / LAST_PACKET
  };
  static std::unique_ptr<UdpLoopbackTransport> create(const Options& options, std::string* error);
  ~UdpLoopbackTransport() override;

  bool send(const EncodedFrame& frame, SendResult& result, std::string* error) override;
  TransportStats stats() const override;
  std::string_view name() const override { return "udp-loopback"; }

 private:
  UdpLoopbackTransport() = default;
  Options options_;
  intptr_t socket_ = -1;
  mutable std::mutex mutex_;
  TransportStats stats_;
  std::vector<uint8_t> packet_;
};

struct ReceiverStats {
  uint64_t packets = 0;
  uint64_t bad_packets = 0;
  uint64_t duplicate_packets = 0;
  uint64_t frames_complete = 0;
  uint64_t frames_incomplete = 0;  // evicted before all packets arrived
  uint64_t frames_out_of_order = 0;
};

class UdpLoopbackReceiver {
 public:
  struct CompletedFrame {
    uint64_t frame_id = 0;
    bool keyframe = false;
    std::vector<uint8_t> data;
    uint64_t first_send_qpc = 0;
    uint64_t last_send_qpc = 0;
    uint64_t complete_qpc = 0;   // QPC when the last missing packet arrived
  };
  using FrameCallback = std::function<void(CompletedFrame&& frame)>;

  // Binds 127.0.0.1 on an ephemeral port.
  static std::unique_ptr<UdpLoopbackReceiver> create(std::string* error);
  ~UdpLoopbackReceiver();

  uint16_t port() const noexcept { return port_; }
  void start(FrameCallback callback);
  void stop();
  ReceiverStats stats() const;

 private:
  UdpLoopbackReceiver() = default;
  void run();
  void handle_packet(const uint8_t* data, size_t size);

  struct Partial {
    std::vector<uint8_t> data;
    std::vector<bool> have;
    uint32_t received = 0;
    uint16_t count = 0;
    bool keyframe = false;
    uint64_t first_send_qpc = 0;
    uint64_t last_send_qpc = 0;
  };

  intptr_t socket_ = -1;
  uint16_t port_ = 0;
  FrameCallback callback_;
  std::atomic<bool> running_{false};
  std::thread thread_;
  std::map<uint64_t, Partial> partial_;
  uint64_t newest_complete_ = 0;
  mutable std::mutex mutex_;
  ReceiverStats stats_;
};

}  // namespace splitplay::pipeline
