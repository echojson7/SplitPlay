// SPDX-License-Identifier: MIT
#include "splitplay/common/win_util.hpp"

#include <cstdint>
#include <format>

namespace splitplay::win {

std::wstring widen(std::string_view utf8) {
  if (utf8.empty()) return {};
  const int n = MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), nullptr, 0);
  std::wstring out(static_cast<size_t>(n), L'\0');
  MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), out.data(), n);
  return out;
}

std::string narrow(std::wstring_view utf16) {
  if (utf16.empty()) return {};
  const int n =
      WideCharToMultiByte(CP_UTF8, 0, utf16.data(), static_cast<int>(utf16.size()), nullptr, 0, nullptr, nullptr);
  std::string out(static_cast<size_t>(n), '\0');
  WideCharToMultiByte(CP_UTF8, 0, utf16.data(), static_cast<int>(utf16.size()), out.data(), n, nullptr, nullptr);
  return out;
}

namespace {

std::string system_message(DWORD code) {
  wchar_t* buffer = nullptr;
  const DWORD len = FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM |
                                       FORMAT_MESSAGE_IGNORE_INSERTS,
                                   nullptr, code, 0, reinterpret_cast<wchar_t*>(&buffer), 0, nullptr);
  std::string text;
  if (len != 0 && buffer != nullptr) {
    text = narrow(std::wstring_view(buffer, len));
    while (!text.empty() && (text.back() == '\n' || text.back() == '\r' || text.back() == ' ' || text.back() == '.'))
      text.pop_back();
  }
  if (buffer) LocalFree(buffer);
  if (text.empty()) text = "unknown error";
  return text;
}

}  // namespace

std::string error_message(DWORD code) { return std::format("{} (code {})", system_message(code), code); }

std::string hresult_message(HRESULT hr) {
  return std::format("{} (hr 0x{:08x})", system_message(static_cast<DWORD>(hr)), static_cast<uint32_t>(hr));
}

long current_session_id() noexcept {
  DWORD session = 0;
  if (!ProcessIdToSessionId(GetCurrentProcessId(), &session)) return -1;
  return static_cast<long>(session);
}

}  // namespace splitplay::win
