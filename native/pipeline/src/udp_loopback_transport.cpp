// SPDX-License-Identifier: MIT
#ifdef _WIN32
#include <winsock2.h>
#include <ws2tcpip.h>
#else
#include <arpa/inet.h>
#include <netinet/in.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <unistd.h>
#include <cerrno>
#include <cstring>
#endif

#include "splitplay/pipeline/udp_loopback_transport.hpp"

#include <algorithm>
#include <cstring>
#include <format>
#include <optional>

#include "splitplay/common/log.hpp"
#include "splitplay/common/qpc.hpp"

namespace splitplay::pipeline {

namespace {

#ifdef _WIN32
using socket_t = SOCKET;
constexpr socket_t kInvalidSocket = INVALID_SOCKET;
void close_socket(socket_t s) { closesocket(s); }
std::string socket_error() { return std::format("socket error {}", WSAGetLastError()); }
#else
using socket_t = int;
constexpr socket_t kInvalidSocket = -1;
void close_socket(socket_t s) { ::close(s); }
std::string socket_error() { return std::format("socket error {} ({})", errno, std::strerror(errno)); }
#endif

socket_t to_socket(intptr_t s) { return static_cast<socket_t>(s); }

sockaddr_in loopback_addr(uint16_t port) {
  sockaddr_in addr{};
  addr.sin_family = AF_INET;
  addr.sin_port = htons(port);
  addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
  return addr;
}

void set_buffer(socket_t s, int option, int bytes) {
  setsockopt(s, SOL_SOCKET, option, reinterpret_cast<const char*>(&bytes), sizeof(bytes));
}

}  // namespace

bool init_sockets(std::string* error) {
#ifdef _WIN32
  static const int rc = [] {
    WSADATA data;
    return WSAStartup(MAKEWORD(2, 2), &data);
  }();
  if (rc != 0) {
    if (error) *error = std::format("WSAStartup failed: {}", rc);
    return false;
  }
#else
  (void)error;
#endif
  return true;
}

// ---- sender ----------------------------------------------------------------------------------------

std::unique_ptr<UdpLoopbackTransport> UdpLoopbackTransport::create(const Options& options, std::string* error) {
  if (!init_sockets(error)) return nullptr;
  if (options.port == 0 || options.max_payload == 0 || options.max_payload > 60000) {
    if (error) *error = "invalid UDP transport options";
    return nullptr;
  }
  socket_t s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
  if (s == kInvalidSocket) {
    if (error) *error = "socket() failed: " + socket_error();
    return nullptr;
  }
  set_buffer(s, SO_SNDBUF, 4 * 1024 * 1024);
  sockaddr_in addr = loopback_addr(options.port);
  // connect() fixes the destination so send() can be used and ICMP errors surface.
  if (connect(s, reinterpret_cast<const sockaddr*>(&addr), sizeof(addr)) != 0) {
    if (error) *error = "connect() failed: " + socket_error();
    close_socket(s);
    return nullptr;
  }
  std::unique_ptr<UdpLoopbackTransport> t(new UdpLoopbackTransport());
  t->options_ = options;
  t->socket_ = static_cast<intptr_t>(s);
  t->packet_.resize(sizeof(UdpPacketHeader) + options.max_payload);
  return t;
}

UdpLoopbackTransport::~UdpLoopbackTransport() {
  if (socket_ != -1) close_socket(to_socket(socket_));
}

bool UdpLoopbackTransport::send(const EncodedFrame& frame, SendResult& result, std::string* error) {
  std::lock_guard lock(mutex_);
  result = SendResult{};
  const size_t total = frame.bitstream.size();
  const uint32_t mp = options_.max_payload;
  const size_t count = std::max<size_t>(1, (total + mp - 1) / mp);
  if (count > 0xFFFF || total > 0xFFFFFFFFu) {
    if (error) *error = "frame too large for the UDP stub transport";
    ++stats_.send_errors;
    return false;
  }
  for (size_t i = 0; i < count; ++i) {
    const size_t offset = i * mp;
    const size_t len = std::min<size_t>(mp, total - std::min(total, offset));
    UdpPacketHeader h{};
    h.magic = kUdpPacketMagic;
    h.version = 1;
    h.header_bytes = sizeof(UdpPacketHeader);
    h.frame_id = frame.frame_id;
    h.frame_bytes = static_cast<uint32_t>(total);
    h.offset = static_cast<uint32_t>(offset);
    h.index = static_cast<uint16_t>(i);
    h.count = static_cast<uint16_t>(count);
    h.payload_bytes = static_cast<uint16_t>(len);
    h.flags = frame.keyframe ? 1 : 0;
    h.send_qpc = qpc_now();
    std::memcpy(packet_.data(), &h, sizeof(h));
    if (len) std::memcpy(packet_.data() + sizeof(h), frame.bitstream.data() + offset, len);
    const int sent = ::send(to_socket(socket_), reinterpret_cast<const char*>(packet_.data()),
                            static_cast<int>(sizeof(h) + len), 0);
    if (sent < 0) {
      ++stats_.send_errors;
      if (error) *error = "send() failed: " + socket_error();
      return false;
    }
    if (i == 0) {
      result.first_packet_qpc = h.send_qpc;
      if (options_.tracer) options_.tracer->stamp(frame.frame_id, SP_STAGE_FIRST_PACKET, h.send_qpc);
    }
    result.last_packet_qpc = h.send_qpc;
    ++result.packets;
    result.bytes += static_cast<uint64_t>(sent);
  }
  if (options_.tracer) options_.tracer->stamp(frame.frame_id, SP_STAGE_LAST_PACKET, result.last_packet_qpc);
  ++stats_.frames_sent;
  stats_.packets_sent += result.packets;
  stats_.bytes_sent += result.bytes;
  return true;
}

TransportStats UdpLoopbackTransport::stats() const {
  std::lock_guard lock(mutex_);
  return stats_;
}

// ---- receiver --------------------------------------------------------------------------------------

std::unique_ptr<UdpLoopbackReceiver> UdpLoopbackReceiver::create(std::string* error) {
  if (!init_sockets(error)) return nullptr;
  socket_t s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
  if (s == kInvalidSocket) {
    if (error) *error = "socket() failed: " + socket_error();
    return nullptr;
  }
  set_buffer(s, SO_RCVBUF, 8 * 1024 * 1024);
  sockaddr_in addr = loopback_addr(0);
  if (bind(s, reinterpret_cast<const sockaddr*>(&addr), sizeof(addr)) != 0) {
    if (error) *error = "bind() failed: " + socket_error();
    close_socket(s);
    return nullptr;
  }
  sockaddr_in bound{};
#ifdef _WIN32
  int len = sizeof(bound);
#else
  socklen_t len = sizeof(bound);
#endif
  if (getsockname(s, reinterpret_cast<sockaddr*>(&bound), &len) != 0) {
    if (error) *error = "getsockname() failed: " + socket_error();
    close_socket(s);
    return nullptr;
  }
#ifdef _WIN32
  DWORD timeout_ms = 100;
  setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&timeout_ms), sizeof(timeout_ms));
#else
  timeval tv{0, 100 * 1000};
  setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));
#endif
  std::unique_ptr<UdpLoopbackReceiver> r(new UdpLoopbackReceiver());
  r->socket_ = static_cast<intptr_t>(s);
  r->port_ = ntohs(bound.sin_port);
  return r;
}

UdpLoopbackReceiver::~UdpLoopbackReceiver() {
  stop();
  if (socket_ != -1) close_socket(to_socket(socket_));
}

void UdpLoopbackReceiver::start(FrameCallback callback) {
  callback_ = std::move(callback);
  running_.store(true);
  thread_ = std::thread([this] { run(); });
}

void UdpLoopbackReceiver::stop() {
  running_.store(false);
  if (thread_.joinable()) thread_.join();
}

ReceiverStats UdpLoopbackReceiver::stats() const {
  std::lock_guard lock(mutex_);
  return stats_;
}

void UdpLoopbackReceiver::run() {
  std::vector<uint8_t> buffer(65536);
  while (running_.load()) {
    const auto n = recv(to_socket(socket_), reinterpret_cast<char*>(buffer.data()), static_cast<int>(buffer.size()), 0);
    if (n <= 0) continue;  // timeout (or transient error): re-check running_
    handle_packet(buffer.data(), static_cast<size_t>(n));
  }
}

void UdpLoopbackReceiver::handle_packet(const uint8_t* data, size_t size) {
  std::optional<CompletedFrame> done;
  {
    std::lock_guard lock(mutex_);
    ++stats_.packets;
    UdpPacketHeader h;
    if (size < sizeof(h)) {
      ++stats_.bad_packets;
      return;
    }
    std::memcpy(&h, data, sizeof(h));
    if (h.magic != kUdpPacketMagic || h.version != 1 || h.header_bytes != sizeof(h) || h.count == 0 ||
        h.index >= h.count || sizeof(h) + h.payload_bytes != size ||
        static_cast<uint64_t>(h.offset) + h.payload_bytes > h.frame_bytes) {
      ++stats_.bad_packets;
      return;
    }
    if (h.frame_id <= newest_complete_ && partial_.find(h.frame_id) == partial_.end()) {
      // Late packet of a frame already completed or evicted.
      ++stats_.duplicate_packets;
      return;
    }
    Partial& p = partial_[h.frame_id];
    if (p.count == 0) {
      p.count = h.count;
      p.data.resize(h.frame_bytes);
      p.have.assign(h.count, false);
      p.keyframe = (h.flags & 1) != 0;
      p.first_send_qpc = h.send_qpc;
    }
    if (p.count != h.count || p.data.size() != h.frame_bytes) {
      ++stats_.bad_packets;
      return;
    }
    if (p.have[h.index]) {
      ++stats_.duplicate_packets;
      return;
    }
    p.have[h.index] = true;
    ++p.received;
    p.first_send_qpc = std::min(p.first_send_qpc, h.send_qpc);
    p.last_send_qpc = std::max(p.last_send_qpc, h.send_qpc);
    if (h.payload_bytes) std::memcpy(p.data.data() + h.offset, data + sizeof(h), h.payload_bytes);

    if (p.received == p.count) {
      CompletedFrame f;
      f.frame_id = h.frame_id;
      f.keyframe = p.keyframe;
      f.data = std::move(p.data);
      f.first_send_qpc = p.first_send_qpc;
      f.last_send_qpc = p.last_send_qpc;
      f.complete_qpc = qpc_now();
      partial_.erase(h.frame_id);
      if (h.frame_id < newest_complete_) ++stats_.frames_out_of_order;
      newest_complete_ = std::max(newest_complete_, h.frame_id);
      ++stats_.frames_complete;
      // Evict partial frames that are far behind the newest complete one.
      while (!partial_.empty() && partial_.begin()->first + 64 < newest_complete_) {
        partial_.erase(partial_.begin());
        ++stats_.frames_incomplete;
      }
      done = std::move(f);
    }
  }
  if (done && callback_) callback_(std::move(*done));
}

}  // namespace splitplay::pipeline
