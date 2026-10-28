// SPDX-License-Identifier: MIT
// WTS helpers for the seat: find the desk user's active session, query a session, log it off.
// The header does not include <wtsapi32.h>: WinPR (FreeRDP) declares its own WTS API with the same names, so the
// FreeRDP connector must only see these plain types.
#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace splitplay::seat::wts {

struct SessionInfo {
  uint32_t id = 0;
  int state = -1;  // WTS_CONNECTSTATE_CLASS
  std::wstring user;
  std::wstring domain;
  std::wstring client_name;

  bool active() const noexcept;
};

// All sessions (WTSEnumerateSessions) with user, domain and client name. Empty on failure (error set).
std::vector<SessionInfo> list_sessions(std::string* error = nullptr);

// The session to report for a desk: WTSActive, user name equal to `user` (case-insensitive). When several match,
// the one whose client name is `client_name` wins; otherwise the highest id.
std::optional<uint32_t> find_active_session(std::wstring_view user, std::wstring_view client_name);

// Info for one session id, or std::nullopt if it does not exist.
std::optional<SessionInfo> query_session(uint32_t id);

// WTSLogoffSession without waiting. False with `error` set on failure.
bool logoff(uint32_t id, std::string* error);

// WTSDisconnectSession without waiting (tests use it to simulate a lost connection).
bool disconnect(uint32_t id, std::string* error);

const char* state_name(int state) noexcept;

}  // namespace splitplay::seat::wts
