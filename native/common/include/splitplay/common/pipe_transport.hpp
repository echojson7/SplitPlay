// SPDX-License-Identifier: MIT
// Named-pipe transport for the control IPC (Windows). Overlapped I/O so that a blocked read never stalls writes
// from the heartbeat thread, and close() can cancel a pending read.
#pragma once

#ifdef _WIN32

#include <chrono>
#include <memory>
#include <mutex>
#include <string>

#include "splitplay/common/ipc_client.hpp"
#include "splitplay/common/win_util.hpp"

namespace splitplay::ipc {

class PipeTransport final : public ITransport {
 public:
  // Child side: connects to an existing pipe server, retrying until `timeout` (the service creates the pipe before
  // starting the child, but a busy pipe or a restart race is tolerated).
  static std::unique_ptr<PipeTransport> connect(const std::string& pipe_name, std::chrono::milliseconds timeout,
                                                std::string* error);
  // Takes ownership of an already connected pipe handle opened with FILE_FLAG_OVERLAPPED (server side in tests).
  static std::unique_ptr<PipeTransport> adopt(HANDLE pipe, std::string* error);

  ~PipeTransport() override;

  std::ptrdiff_t read_some(char* buffer, size_t size) override;
  bool write_all(std::string_view bytes) override;
  void close() override;

  // Timeout for one write; a service that stops reading must not hang the child forever.
  void set_write_timeout(std::chrono::milliseconds timeout) { write_timeout_ = timeout; }
  HANDLE handle() const noexcept { return pipe_.get(); }

 private:
  PipeTransport() = default;
  bool init_events(std::string* error);

  win::UniqueHandle pipe_;
  win::UniqueHandle read_event_;
  win::UniqueHandle write_event_;
  win::UniqueHandle cancel_event_;
  std::mutex write_mutex_;
  std::chrono::milliseconds write_timeout_{5000};
};

// Minimal single-instance pipe server, used by tests and tools that play the service.
class PipeServer {
 public:
  // Creates `\\.\pipe\...` with byte mode, overlapped, one instance, default security.
  static std::unique_ptr<PipeServer> create(const std::string& pipe_name, std::string* error);
  // Waits for the client to connect and returns the connected transport (the server object is consumed).
  std::unique_ptr<PipeTransport> accept(std::chrono::milliseconds timeout, std::string* error);

 private:
  win::UniqueHandle pipe_;
};

}  // namespace splitplay::ipc

#endif  // _WIN32
