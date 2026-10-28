// SPDX-License-Identifier: MIT
// Child side of the control IPC (docs/contracts/ipc.md): hello -> welcome -> ready, heartbeat every second,
// request dispatch, shutdown handling and pipe-break detection. The byte transport is abstract so the protocol logic
// is testable without Windows; PipeTransport (pipe_transport.hpp) is the named-pipe implementation.
#pragma once

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <thread>

#include "splitplay/common/ipc_codec.hpp"

namespace splitplay::ipc {

class ITransport {
 public:
  virtual ~ITransport() = default;
  // Blocks until bytes arrive. Returns the number of bytes read, 0 on orderly close or broken pipe,
  // or a negative value after close() was called or on error.
  virtual std::ptrdiff_t read_some(char* buffer, size_t size) = 0;
  // Writes all bytes. Called from several threads; the client serialises calls. Returns false on failure.
  virtual bool write_all(std::string_view bytes) = 0;
  // Unblocks a pending read_some and makes further I/O fail. Must be safe to call from any thread, repeatedly.
  virtual void close() = 0;
};

// Answer to a request: ok with optional data, or an error.
struct Result {
  bool ok = true;
  json data = nullptr;
  std::string code;
  std::string message;

  static Result success(json data = nullptr) { return Result{true, std::move(data), {}, {}}; }
  static Result failure(std::string code, std::string message) {
    return Result{false, nullptr, std::move(code), std::move(message)};
  }
  static Result unsupported(std::string message = "not supported by this component") {
    return failure(kErrUnsupported, std::move(message));
  }
};

struct HeartbeatInfo {
  std::string state = "idle";
  json metrics = json::object();
};

enum class ExitReason { none, shutdown, disconnected };
const char* to_string(ExitReason reason) noexcept;

class Client {
 public:
  struct Handlers {
    // Live profile changes: the initial profile from `welcome` and every `set_profile`. Return failure to reject.
    std::function<Result(const json& profile, bool initial)> on_profile;
    // Any other request with an id (pair, list_clients, unpair, send_sas, ...). Return std::nullopt for
    // "unsupported". Not called for unknown types, which are answered with unknown_type.
    std::function<std::optional<Result>(const Message& request)> on_request;
    // Called from the heartbeat thread every interval.
    std::function<HeartbeatInfo()> on_heartbeat;
    // Called once, from the reader thread, when shutdown is requested or the pipe breaks.
    std::function<void(ExitReason)> on_exit;
  };

  struct Options {
    std::chrono::milliseconds heartbeat_interval{1000};
    std::chrono::milliseconds welcome_timeout{15000};
  };

  Client(std::unique_ptr<ITransport> transport, Handlers handlers);
  Client(std::unique_ptr<ITransport> transport, Handlers handlers, Options options);
  ~Client();
  Client(const Client&) = delete;
  Client& operator=(const Client&) = delete;

  // Starts the reader, sends hello and waits for welcome. Returns the initial profile, or std::nullopt with
  // `error` set (timeout, protocol mismatch, pipe closed). Heartbeats start once welcome has arrived.
  std::optional<json> handshake(const Hello& hello, std::string* error = nullptr);

  bool send(const json& message);
  bool send_ready(const json& extra = json::object()) { return send(make_ready(extra)); }
  bool send_log(std::string_view level, std::string_view message) { return send(make_log(level, message)); }
  bool send_event(std::string_view name, const json& data = json::object()) { return send(make_event(name, data)); }

  // Blocks until shutdown is requested or the pipe breaks (or the timeout passes: returns ExitReason::none).
  ExitReason wait(std::optional<std::chrono::milliseconds> timeout = std::nullopt);
  ExitReason exit_reason() const;
  // Id of the shutdown request, already answered with ok.
  std::optional<int64_t> shutdown_id() const;

  // Stops the heartbeat and reader threads and closes the transport. Idempotent.
  void stop();

  uint64_t heartbeats_sent() const;

 private:
  void reader_loop();
  void heartbeat_loop();
  void dispatch(const Message& msg);
  void reply(int64_t id, const Result& result);
  void set_exit(ExitReason reason);

  std::unique_ptr<ITransport> transport_;
  Handlers handlers_;
  Options options_;

  std::mutex write_mutex_;
  mutable std::mutex mutex_;
  std::condition_variable cv_;
  bool welcomed_ = false;
  std::string handshake_error_;
  json profile_;
  ExitReason exit_reason_ = ExitReason::none;
  std::atomic<bool> exit_claimed_{false};
  std::optional<int64_t> shutdown_id_;
  bool stopping_ = false;
  uint64_t heartbeat_seq_ = 0;

  std::thread reader_;
  std::thread heartbeat_;
};

}  // namespace splitplay::ipc
