// SPDX-License-Identifier: MIT
#include "wts_session.hpp"

#include <windows.h>

#include <wtsapi32.h>

#include "splitplay/common/win_util.hpp"

namespace splitplay::seat::wts {

namespace {

// wtsapi32.dll is resolved at run time: the statically linked WinPR (FreeRDP) defines functions with the same names
// (forwarders to wtsapi32.dll), which would clash with the import library at link time.
struct Api {
  decltype(&::WTSEnumerateSessionsW) enumerate_sessions = nullptr;
  decltype(&::WTSQuerySessionInformationW) query_information = nullptr;
  decltype(&::WTSFreeMemory) free_memory = nullptr;
  decltype(&::WTSLogoffSession) logoff_session = nullptr;
  bool ok = false;
};

template <class F>
F proc(HMODULE m, const char* name) {
  return reinterpret_cast<F>(reinterpret_cast<void*>(GetProcAddress(m, name)));
}

const Api& api() {
  static const Api instance = [] {
    Api a;
    HMODULE m = LoadLibraryExW(L"wtsapi32.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!m) return a;
    a.enumerate_sessions = proc<decltype(a.enumerate_sessions)>(m, "WTSEnumerateSessionsW");
    a.query_information = proc<decltype(a.query_information)>(m, "WTSQuerySessionInformationW");
    a.free_memory = proc<decltype(a.free_memory)>(m, "WTSFreeMemory");
    a.logoff_session = proc<decltype(a.logoff_session)>(m, "WTSLogoffSession");
    a.ok = a.enumerate_sessions && a.query_information && a.free_memory && a.logoff_session;
    return a;
  }();
  return instance;
}

std::wstring query_string(DWORD id, WTS_INFO_CLASS info) {
  LPWSTR buffer = nullptr;
  DWORD bytes = 0;
  if (!api().query_information(WTS_CURRENT_SERVER_HANDLE, id, info, &buffer, &bytes) || !buffer) return {};
  std::wstring value(buffer);
  api().free_memory(buffer);
  return value;
}

bool iequals(std::wstring_view a, std::wstring_view b) {
  if (a.size() != b.size()) return false;
  if (a.empty()) return true;
  return CompareStringOrdinal(a.data(), static_cast<int>(a.size()), b.data(), static_cast<int>(b.size()), TRUE) ==
         CSTR_EQUAL;
}

}  // namespace

bool SessionInfo::active() const noexcept { return state == WTSActive; }

std::vector<SessionInfo> list_sessions(std::string* error) {
  std::vector<SessionInfo> out;
  if (!api().ok) {
    if (error) *error = "wtsapi32.dll is not available";
    return out;
  }
  PWTS_SESSION_INFOW sessions = nullptr;
  DWORD count = 0;
  if (!api().enumerate_sessions(WTS_CURRENT_SERVER_HANDLE, 0, 1, &sessions, &count)) {
    if (error) *error = "WTSEnumerateSessions: " + win::last_error_message();
    return out;
  }
  out.reserve(count);
  for (DWORD i = 0; i < count; ++i) {
    SessionInfo s;
    s.id = sessions[i].SessionId;
    s.state = sessions[i].State;
    s.user = query_string(s.id, WTSUserName);
    s.domain = query_string(s.id, WTSDomainName);
    s.client_name = query_string(s.id, WTSClientName);
    out.push_back(std::move(s));
  }
  api().free_memory(sessions);
  return out;
}

std::optional<uint32_t> find_active_session(std::wstring_view user, std::wstring_view client_name) {
  std::optional<uint32_t> by_user;
  for (const SessionInfo& s : list_sessions()) {
    if (!s.active() || s.user.empty() || !iequals(s.user, user)) continue;
    if (!client_name.empty() && iequals(s.client_name, client_name)) return s.id;
    if (!by_user || s.id > *by_user) by_user = s.id;
  }
  return by_user;
}

std::optional<SessionInfo> query_session(uint32_t id) {
  if (!api().ok) return std::nullopt;
  LPWSTR raw = nullptr;
  DWORD bytes = 0;
  if (!api().query_information(WTS_CURRENT_SERVER_HANDLE, id, WTSConnectState, &raw, &bytes) || !raw)
    return std::nullopt;
  SessionInfo s;
  s.id = id;
  s.state = *reinterpret_cast<const WTS_CONNECTSTATE_CLASS*>(raw);
  api().free_memory(raw);
  s.user = query_string(id, WTSUserName);
  s.domain = query_string(id, WTSDomainName);
  s.client_name = query_string(id, WTSClientName);
  return s;
}

bool logoff(uint32_t id, std::string* error) {
  if (!api().ok) {
    if (error) *error = "wtsapi32.dll is not available";
    return false;
  }
  if (api().logoff_session(WTS_CURRENT_SERVER_HANDLE, id, FALSE)) return true;
  if (error) *error = "WTSLogoffSession: " + win::last_error_message();
  return false;
}

const char* state_name(int state) noexcept {
  switch (static_cast<WTS_CONNECTSTATE_CLASS>(state)) {
    case WTSActive: return "active";
    case WTSConnected: return "connected";
    case WTSConnectQuery: return "connect_query";
    case WTSShadow: return "shadow";
    case WTSDisconnected: return "disconnected";
    case WTSIdle: return "idle";
    case WTSListen: return "listen";
    case WTSReset: return "reset";
    case WTSDown: return "down";
    case WTSInit: return "init";
  }
  return "unknown";
}

}  // namespace splitplay::seat::wts
