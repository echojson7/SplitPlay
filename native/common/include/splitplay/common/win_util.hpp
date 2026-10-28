// SPDX-License-Identifier: MIT
// Small Win32 helpers: UTF-8/UTF-16 conversion, error formatting, RAII handles.
#pragma once

#ifdef _WIN32

#include <windows.h>

#include <string>
#include <string_view>
#include <utility>

namespace splitplay::win {

std::wstring widen(std::string_view utf8);
std::string narrow(std::wstring_view utf16);

// "message (code N)" for a Win32 error code or HRESULT.
std::string error_message(DWORD code);
std::string hresult_message(HRESULT hr);
inline std::string last_error_message() { return error_message(GetLastError()); }

// Owning HANDLE wrapper. Treats both nullptr and INVALID_HANDLE_VALUE as empty.
class UniqueHandle {
 public:
  UniqueHandle() noexcept = default;
  explicit UniqueHandle(HANDLE h) noexcept : h_(h) {}
  ~UniqueHandle() { reset(); }
  UniqueHandle(UniqueHandle&& o) noexcept : h_(std::exchange(o.h_, nullptr)) {}
  UniqueHandle& operator=(UniqueHandle&& o) noexcept {
    if (this != &o) {
      reset();
      h_ = std::exchange(o.h_, nullptr);
    }
    return *this;
  }
  UniqueHandle(const UniqueHandle&) = delete;
  UniqueHandle& operator=(const UniqueHandle&) = delete;

  HANDLE get() const noexcept { return h_; }
  explicit operator bool() const noexcept { return h_ != nullptr && h_ != INVALID_HANDLE_VALUE; }
  HANDLE release() noexcept { return std::exchange(h_, nullptr); }
  void reset(HANDLE h = nullptr) noexcept {
    if (h_ != nullptr && h_ != INVALID_HANDLE_VALUE) CloseHandle(h_);
    h_ = h;
  }

 private:
  HANDLE h_ = nullptr;
};

// Session id of the current process, or -1.
long current_session_id() noexcept;

}  // namespace splitplay::win

#endif  // _WIN32
