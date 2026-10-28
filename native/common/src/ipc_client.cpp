// SPDX-License-Identifier: MIT
#include "splitplay/common/ipc_client.hpp"

#include <array>
#include <vector>

#include "splitplay/common/log.hpp"
#include "splitplay/common/version.hpp"

namespace splitplay::ipc {

const char* to_string(ExitReason reason) noexcept {
  switch (reason) {
    case ExitReason::none: return "none";
    case ExitReason::shutdown: return "shutdown";
    case ExitReason::disconnected: return "disconnected";
  }
  return "none";
}

Client::Client(std::unique_ptr<ITransport> transport, Handlers handlers)
    : Client(std::move(transport), std::move(handlers), Options{}) {}

Client::Client(std::unique_ptr<ITransport> transport, Handlers handlers, Options options)
    : transport_(std::move(transport)), handlers_(std::move(handlers)), options_(options) {}

Client::~Client() { stop(); }

std::optional<json> Client::handshake(const Hello& hello, std::string* error) {
  reader_ = std::thread([this] { reader_loop(); });
  if (!send(make_hello(hello))) {
    if (error) *error = "failed to send hello";
    return std::nullopt;
  }
  std::unique_lock lock(mutex_);
  const bool done = cv_.wait_for(lock, options_.welcome_timeout, [&] {
    return welcomed_ || !handshake_error_.empty() || exit_reason_ != ExitReason::none;
  });
  if (!done) {
    if (error) *error = "timed out waiting for welcome";
    return std::nullopt;
  }
  if (!welcomed_) {
    if (error) *error = !handshake_error_.empty() ? handshake_error_ : std::string("pipe closed before welcome");
    return std::nullopt;
  }
  json profile = profile_;
  lock.unlock();
  heartbeat_ = std::thread([this] { heartbeat_loop(); });
  return profile;
}

bool Client::send(const json& message) {
  auto line = encode_line(message);
  if (!line) {
    log_warn("ipc: dropping oversized outgoing message of type {}", message.value("type", std::string("?")));
    return false;
  }
  std::lock_guard lock(write_mutex_);
  return transport_->write_all(*line);
}

void Client::reply(int64_t id, const Result& result) {
  send(result.ok ? make_result_ok(id, result.data) : make_result_error(id, result.code, result.message));
}

void Client::set_exit(ExitReason reason) {
  // The first reason wins. on_exit runs before wait() returns, so callers observe its effects.
  if (exit_claimed_.exchange(true)) return;
  if (handlers_.on_exit) handlers_.on_exit(reason);
  {
    std::lock_guard lock(mutex_);
    exit_reason_ = reason;
  }
  cv_.notify_all();
}

void Client::reader_loop() {
  LineFramer framer;
  std::array<char, 16 * 1024> buffer{};
  for (;;) {
    const std::ptrdiff_t n = transport_->read_some(buffer.data(), buffer.size());
    if (n <= 0) {
      bool stopping;
      {
        std::lock_guard lock(mutex_);
        stopping = stopping_;
      }
      if (!stopping) log_warn("ipc: control pipe closed by the service");
      set_exit(ExitReason::disconnected);
      return;
    }
    const size_t oversized_before = framer.oversized_lines();
    std::vector<std::string> lines = framer.push(std::string_view(buffer.data(), static_cast<size_t>(n)));
    // Control messages are small and rare: wipe every raw copy, because `welcome` carries the RDP password.
    secure_wipe(buffer.data(), static_cast<size_t>(n));
    for (std::string& line : lines) {
      std::string err;
      auto msg = parse_line(line, &err);
      secure_wipe(line);
      if (!msg) {
        log_warn("ipc: ignoring malformed message: {}", err);
        continue;
      }
      dispatch(*msg);
    }
    if (framer.oversized_lines() != oversized_before) log_warn("ipc: discarded a line longer than 64 KiB");
  }
}

void Client::dispatch(Message& msg) {
  const std::string& type = msg.type;

  if (type == "welcome") {
    const auto protocol = get_int(msg.body, "protocol");
    if (!protocol || *protocol != kIpcProtocolVersion) {
      {
        std::lock_guard lock(mutex_);
        handshake_error_ = "unsupported protocol version in welcome";
      }
      cv_.notify_all();
      return;
    }
    if (handlers_.on_welcome) handlers_.on_welcome(msg.body);
    const json* profile = get_object(msg.body, "profile");
    json p = profile ? *profile : json::object();
    if (handlers_.on_profile) {
      Result r = handlers_.on_profile(p, true);
      if (!r.ok) log_warn("ipc: initial profile rejected: {}", r.message);
    }
    {
      std::lock_guard lock(mutex_);
      profile_ = std::move(p);
      welcomed_ = true;
    }
    cv_.notify_all();
    return;
  }

  if (type == "result") return;  // answers to child requests; the child sends none yet

  if (type == "shutdown") {
    if (msg.id) {
      {
        std::lock_guard lock(mutex_);
        shutdown_id_ = msg.id;
      }
      reply(*msg.id, Result::success());
    }
    log_info("ipc: shutdown requested by the service");
    set_exit(ExitReason::shutdown);
    return;
  }

  if (type == "set_profile") {
    if (!msg.id) return;
    const json* profile = get_object(msg.body, "profile");
    if (!profile) {
      reply(*msg.id, Result::failure(kErrBadRequest, "set_profile needs a \"profile\" object"));
      return;
    }
    Result r = handlers_.on_profile ? handlers_.on_profile(*profile, false) : Result::success();
    if (r.ok) {
      std::lock_guard lock(mutex_);
      for (auto it = profile->begin(); it != profile->end(); ++it) profile_[it.key()] = it.value();
    }
    reply(*msg.id, r);
    return;
  }

  if (!is_known_service_message(type)) {
    if (msg.id) reply(*msg.id, Result::failure(kErrUnknownType, "unknown message type \"" + type + "\""));
    return;
  }

  if (!msg.id) return;  // known request types without an id: nothing to answer
  std::optional<Result> r;
  if (handlers_.on_request) {
    try {
      r = handlers_.on_request(msg);
    } catch (const std::exception& e) {
      r = Result::failure(kErrInternal, e.what());
    }
  }
  reply(*msg.id, r ? *r : Result::unsupported("\"" + type + "\" is not supported by this component"));
}

void Client::heartbeat_loop() {
  for (;;) {
    {
      std::unique_lock lock(mutex_);
      if (cv_.wait_for(lock, options_.heartbeat_interval, [&] { return stopping_ || exit_reason_ != ExitReason::none; }))
        return;
    }
    HeartbeatInfo info = handlers_.on_heartbeat ? handlers_.on_heartbeat() : HeartbeatInfo{};
    uint64_t seq;
    {
      std::lock_guard lock(mutex_);
      seq = ++heartbeat_seq_;
    }
    if (!send(make_heartbeat(seq, info.state, info.metrics))) {
      log_warn("ipc: heartbeat write failed");
      transport_->close();  // the reader then reports the disconnect
      return;
    }
  }
}

ExitReason Client::wait(std::optional<std::chrono::milliseconds> timeout) {
  std::unique_lock lock(mutex_);
  auto pred = [&] { return exit_reason_ != ExitReason::none; };
  if (timeout) {
    cv_.wait_for(lock, *timeout, pred);
  } else {
    cv_.wait(lock, pred);
  }
  return exit_reason_;
}

ExitReason Client::exit_reason() const {
  std::lock_guard lock(mutex_);
  return exit_reason_;
}

std::optional<int64_t> Client::shutdown_id() const {
  std::lock_guard lock(mutex_);
  return shutdown_id_;
}

uint64_t Client::heartbeats_sent() const {
  std::lock_guard lock(mutex_);
  return heartbeat_seq_;
}

void Client::stop() {
  {
    std::lock_guard lock(mutex_);
    stopping_ = true;
  }
  cv_.notify_all();
  if (heartbeat_.joinable()) heartbeat_.join();
  if (transport_) transport_->close();
  if (reader_.joinable()) reader_.join();
}

}  // namespace splitplay::ipc
