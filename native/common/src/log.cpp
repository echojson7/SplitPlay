// SPDX-License-Identifier: MIT
#include "splitplay/common/log.hpp"

#include <atomic>
#include <chrono>
#include <cstdio>
#include <mutex>

#ifdef _WIN32
#include <windows.h>
#else
#include <unistd.h>
#endif

namespace splitplay {
namespace {

struct LoggerState {
  std::mutex mutex;
  std::string component = "splitplay";
  LogSink sink;
  LogLevel sink_level = LogLevel::info;
};

LoggerState& state() {
  static LoggerState s;
  return s;
}

std::atomic<int> g_min_level{static_cast<int>(LogLevel::info)};
thread_local bool t_in_sink = false;

unsigned long current_pid() {
#ifdef _WIN32
  return GetCurrentProcessId();
#else
  return static_cast<unsigned long>(getpid());
#endif
}

}  // namespace

std::string_view to_string(LogLevel level) noexcept {
  switch (level) {
    case LogLevel::trace: return "trace";
    case LogLevel::debug: return "debug";
    case LogLevel::info: return "info";
    case LogLevel::warn: return "warn";
    case LogLevel::error: return "error";
  }
  return "info";
}

bool parse_log_level(std::string_view text, LogLevel& out) noexcept {
  for (auto level : {LogLevel::trace, LogLevel::debug, LogLevel::info, LogLevel::warn, LogLevel::error}) {
    if (text == to_string(level)) {
      out = level;
      return true;
    }
  }
  if (text == "warning") {
    out = LogLevel::warn;
    return true;
  }
  return false;
}

void log_set_component(std::string component) {
  std::lock_guard lock(state().mutex);
  state().component = std::move(component);
}

void log_set_min_level(LogLevel level) { g_min_level.store(static_cast<int>(level), std::memory_order_relaxed); }

LogLevel log_min_level() noexcept { return static_cast<LogLevel>(g_min_level.load(std::memory_order_relaxed)); }

void log_set_sink(LogSink sink, LogLevel min_level) {
  std::lock_guard lock(state().mutex);
  state().sink = std::move(sink);
  state().sink_level = min_level;
}

void log_write(LogLevel level, std::string_view message) {
  if (level < log_min_level()) return;
  auto& s = state();
  LogSink sink;
  {
    std::lock_guard lock(s.mutex);
    const auto now = std::chrono::floor<std::chrono::milliseconds>(std::chrono::system_clock::now());
    const std::string line =
        std::format("{:%FT%T}Z [{}] {}[{}]: {}\n", now, to_string(level), s.component, current_pid(), message);
    std::fwrite(line.data(), 1, line.size(), stderr);
    std::fflush(stderr);
    if (s.sink && level >= s.sink_level && !t_in_sink) sink = s.sink;
  }
  if (sink) {
    t_in_sink = true;
    sink(level, message);
    t_in_sink = false;
  }
}

}  // namespace splitplay
