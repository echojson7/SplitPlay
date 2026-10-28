// SPDX-License-Identifier: MIT
// In-memory duplex byte pipe implementing ipc::ITransport, plus a "service end" helper that reads and writes JSON
// lines with timeouts. Lets the IPC client be tested without Windows named pipes.
#pragma once

#include <chrono>
#include <condition_variable>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <utility>

#include "splitplay/common/ipc_client.hpp"

namespace sp_test {

struct Channel {
  std::mutex mutex;
  std::condition_variable cv;
  std::string data;
  bool closed = false;
};

class MemTransport final : public splitplay::ipc::ITransport {
 public:
  MemTransport(std::shared_ptr<Channel> in, std::shared_ptr<Channel> out) : in_(std::move(in)), out_(std::move(out)) {}
  ~MemTransport() override { close(); }

  std::ptrdiff_t read_some(char* buffer, size_t size) override {
    return read_for(buffer, size, std::chrono::hours(24));
  }
  // 0 on close, -1 on timeout.
  std::ptrdiff_t read_for(char* buffer, size_t size, std::chrono::milliseconds timeout) {
    std::unique_lock lock(in_->mutex);
    if (!in_->cv.wait_for(lock, timeout, [&] { return !in_->data.empty() || in_->closed; })) return -1;
    if (in_->data.empty()) return 0;
    const size_t n = std::min(size, in_->data.size());
    in_->data.copy(buffer, n);
    in_->data.erase(0, n);
    return static_cast<std::ptrdiff_t>(n);
  }
  bool write_all(std::string_view bytes) override {
    std::lock_guard lock(out_->mutex);
    if (out_->closed) return false;
    out_->data.append(bytes);
    out_->cv.notify_all();
    return true;
  }
  // Closing either end breaks the pipe in both directions.
  void close() override {
    for (auto* ch : {in_.get(), out_.get()}) {
      std::lock_guard lock(ch->mutex);
      ch->closed = true;
      ch->cv.notify_all();
    }
  }

 private:
  std::shared_ptr<Channel> in_, out_;
};

inline std::pair<std::unique_ptr<MemTransport>, std::unique_ptr<MemTransport>> make_mem_pipe() {
  auto a = std::make_shared<Channel>();
  auto b = std::make_shared<Channel>();
  return {std::make_unique<MemTransport>(a, b), std::make_unique<MemTransport>(b, a)};
}

// Reads JSON lines from any transport with a timeout (the transport must support read_for or be wrapped).
template <class Transport>
class LineEnd {
 public:
  explicit LineEnd(Transport* t) : t_(t) {}

  std::optional<splitplay::ipc::Message> next(std::chrono::milliseconds timeout) {
    const auto deadline = std::chrono::steady_clock::now() + timeout;
    for (;;) {
      if (!pending_.empty()) {
        std::string line = std::move(pending_.front());
        pending_.erase(pending_.begin());
        if (auto m = splitplay::ipc::parse_line(line)) return m;
        continue;
      }
      const auto left = std::chrono::duration_cast<std::chrono::milliseconds>(deadline - std::chrono::steady_clock::now());
      if (left.count() <= 0) return std::nullopt;
      char buf[4096];
      const auto n = t_->read_for(buf, sizeof(buf), left);
      if (n <= 0) {
        if (n == 0) closed_ = true;
        return std::nullopt;
      }
      for (auto& l : framer_.push(std::string_view(buf, static_cast<size_t>(n)))) pending_.push_back(std::move(l));
    }
  }

  // Next message of the given type, skipping others (e.g. heartbeats, logs).
  std::optional<splitplay::ipc::Message> next_of(std::string_view type, std::chrono::milliseconds timeout) {
    const auto deadline = std::chrono::steady_clock::now() + timeout;
    for (;;) {
      const auto left = std::chrono::duration_cast<std::chrono::milliseconds>(deadline - std::chrono::steady_clock::now());
      if (left.count() <= 0) return std::nullopt;
      auto m = next(left);
      if (!m) return std::nullopt;
      if (m->type == type) return m;
    }
  }

  // Next result with the given id.
  std::optional<splitplay::ipc::Message> result(int64_t id, std::chrono::milliseconds timeout) {
    const auto deadline = std::chrono::steady_clock::now() + timeout;
    for (;;) {
      const auto left = std::chrono::duration_cast<std::chrono::milliseconds>(deadline - std::chrono::steady_clock::now());
      if (left.count() <= 0) return std::nullopt;
      auto m = next_of("result", left);
      if (!m) return std::nullopt;
      if (m->id && *m->id == id) return m;
    }
  }

  bool send(const splitplay::ipc::json& msg) {
    auto line = splitplay::ipc::encode_line(msg);
    return line && t_->write_all(*line);
  }

  bool closed() const { return closed_; }

 private:
  Transport* t_;
  splitplay::ipc::LineFramer framer_;
  std::vector<std::string> pending_;
  bool closed_ = false;
};

}  // namespace sp_test
