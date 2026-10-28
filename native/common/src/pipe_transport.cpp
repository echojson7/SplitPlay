// SPDX-License-Identifier: MIT
#include "splitplay/common/pipe_transport.hpp"

#include <algorithm>
#include <thread>

#include "splitplay/common/ipc_codec.hpp"

namespace splitplay::ipc {

namespace {

// Waits for an overlapped operation or the cancel event. Returns true if the operation completed (successfully or
// not, check GetOverlappedResult), false if it was cancelled or timed out (the I/O is cancelled and drained).
bool wait_overlapped(HANDLE file, OVERLAPPED& ov, HANDLE cancel, DWORD timeout_ms) {
  HANDLE handles[2] = {ov.hEvent, cancel};
  const DWORD rc = WaitForMultipleObjects(cancel ? 2 : 1, handles, FALSE, timeout_ms);
  if (rc == WAIT_OBJECT_0) return true;
  CancelIoEx(file, &ov);
  DWORD ignored = 0;
  GetOverlappedResult(file, &ov, &ignored, TRUE);  // wait until the cancelled I/O no longer uses `ov`
  return false;
}

}  // namespace

bool PipeTransport::init_events(std::string* error) {
  read_event_.reset(CreateEventW(nullptr, TRUE, FALSE, nullptr));
  write_event_.reset(CreateEventW(nullptr, TRUE, FALSE, nullptr));
  cancel_event_.reset(CreateEventW(nullptr, TRUE, FALSE, nullptr));
  if (!read_event_ || !write_event_ || !cancel_event_) {
    if (error) *error = "CreateEvent failed: " + win::last_error_message();
    return false;
  }
  return true;
}

std::unique_ptr<PipeTransport> PipeTransport::connect(const std::string& pipe_name, std::chrono::milliseconds timeout,
                                                      std::string* error) {
  std::unique_ptr<PipeTransport> t(new PipeTransport());
  if (!t->init_events(error)) return nullptr;
  const std::wstring name = win::widen(pipe_name);
  const auto deadline = std::chrono::steady_clock::now() + timeout;
  for (;;) {
    // SECURITY_IDENTIFICATION: the service may identify, but not impersonate, the child.
    HANDLE h = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
                           FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
    if (h != INVALID_HANDLE_VALUE) {
      t->pipe_.reset(h);
      DWORD mode = PIPE_READMODE_BYTE;
      SetNamedPipeHandleState(h, &mode, nullptr, nullptr);
      return t;
    }
    const DWORD err = GetLastError();
    const auto now = std::chrono::steady_clock::now();
    if (now >= deadline || (err != ERROR_PIPE_BUSY && err != ERROR_FILE_NOT_FOUND)) {
      if (error) *error = "cannot open " + pipe_name + ": " + win::error_message(err);
      return nullptr;
    }
    if (err == ERROR_PIPE_BUSY) {
      const auto left = std::chrono::duration_cast<std::chrono::milliseconds>(deadline - now).count();
      WaitNamedPipeW(name.c_str(), static_cast<DWORD>(std::max<long long>(1, std::min<long long>(left, 1000))));
    } else {
      std::this_thread::sleep_for(std::chrono::milliseconds(50));
    }
  }
}

std::unique_ptr<PipeTransport> PipeTransport::adopt(HANDLE pipe, std::string* error) {
  std::unique_ptr<PipeTransport> t(new PipeTransport());
  t->pipe_.reset(pipe);
  if (!t->init_events(error)) return nullptr;
  return t;
}

PipeTransport::~PipeTransport() { close(); }

std::ptrdiff_t PipeTransport::read_some(char* buffer, size_t size) {
  if (!pipe_ || WaitForSingleObject(cancel_event_.get(), 0) == WAIT_OBJECT_0) return -1;
  OVERLAPPED ov{};
  ov.hEvent = read_event_.get();
  ResetEvent(ov.hEvent);
  DWORD n = 0;
  const DWORD want = static_cast<DWORD>(std::min<size_t>(size, 1u << 20));
  if (!ReadFile(pipe_.get(), buffer, want, &n, &ov)) {
    const DWORD err = GetLastError();
    if (err == ERROR_BROKEN_PIPE || err == ERROR_PIPE_NOT_CONNECTED || err == ERROR_NO_DATA) return 0;
    if (err != ERROR_IO_PENDING) return -1;
    if (!wait_overlapped(pipe_.get(), ov, cancel_event_.get(), INFINITE)) return -1;
    if (!GetOverlappedResult(pipe_.get(), &ov, &n, FALSE)) {
      const DWORD err2 = GetLastError();
      if (err2 == ERROR_MORE_DATA) return static_cast<std::ptrdiff_t>(n);  // message-mode server; bytes are valid
      if (err2 == ERROR_BROKEN_PIPE || err2 == ERROR_PIPE_NOT_CONNECTED) return 0;
      return -1;
    }
  }
  if (n == 0) {
    // A zero-byte read on a byte-mode pipe only happens for zero-length writes; treat as "try again".
    return read_some(buffer, size);
  }
  return static_cast<std::ptrdiff_t>(n);
}

bool PipeTransport::write_all(std::string_view bytes) {
  std::lock_guard lock(write_mutex_);
  while (!bytes.empty()) {
    if (!pipe_ || WaitForSingleObject(cancel_event_.get(), 0) == WAIT_OBJECT_0) return false;
    OVERLAPPED ov{};
    ov.hEvent = write_event_.get();
    ResetEvent(ov.hEvent);
    DWORD n = 0;
    const DWORD chunk = static_cast<DWORD>(std::min<size_t>(bytes.size(), 64 * 1024));
    if (!WriteFile(pipe_.get(), bytes.data(), chunk, &n, &ov)) {
      if (GetLastError() != ERROR_IO_PENDING) return false;
      if (!wait_overlapped(pipe_.get(), ov, cancel_event_.get(), static_cast<DWORD>(write_timeout_.count())))
        return false;
      if (!GetOverlappedResult(pipe_.get(), &ov, &n, FALSE)) return false;
    }
    if (n == 0) return false;
    bytes.remove_prefix(n);
  }
  return true;
}

void PipeTransport::close() {
  if (cancel_event_) SetEvent(cancel_event_.get());
  if (pipe_) CancelIoEx(pipe_.get(), nullptr);
}

std::unique_ptr<PipeServer> PipeServer::create(const std::string& pipe_name, std::string* error) {
  const std::wstring name = win::widen(pipe_name);
  HANDLE h = CreateNamedPipeW(name.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
                              PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1,
                              static_cast<DWORD>(kMaxLineBytes), static_cast<DWORD>(kMaxLineBytes), 0, nullptr);
  if (h == INVALID_HANDLE_VALUE) {
    if (error) *error = "CreateNamedPipe " + pipe_name + " failed: " + win::last_error_message();
    return nullptr;
  }
  auto s = std::make_unique<PipeServer>();
  s->pipe_.reset(h);
  return s;
}

std::unique_ptr<PipeTransport> PipeServer::accept(std::chrono::milliseconds timeout, std::string* error) {
  win::UniqueHandle ev(CreateEventW(nullptr, TRUE, FALSE, nullptr));
  OVERLAPPED ov{};
  ov.hEvent = ev.get();
  if (!ConnectNamedPipe(pipe_.get(), &ov)) {
    const DWORD err = GetLastError();
    if (err == ERROR_IO_PENDING) {
      if (!wait_overlapped(pipe_.get(), ov, nullptr, static_cast<DWORD>(timeout.count()))) {
        if (error) *error = "timed out waiting for the client to connect";
        return nullptr;
      }
      DWORD ignored = 0;
      if (!GetOverlappedResult(pipe_.get(), &ov, &ignored, FALSE)) {
        if (error) *error = "ConnectNamedPipe failed: " + win::last_error_message();
        return nullptr;
      }
    } else if (err != ERROR_PIPE_CONNECTED) {
      if (error) *error = "ConnectNamedPipe failed: " + win::error_message(err);
      return nullptr;
    }
  }
  return PipeTransport::adopt(pipe_.release(), error);
}

}  // namespace splitplay::ipc
