// SPDX-License-Identifier: MIT
// Minimal thread-safe logger: human-readable lines on stderr plus an optional extra sink
// (used to forward log records to the service over the control pipe).
#pragma once

#include <format>
#include <functional>
#include <string>
#include <string_view>
#include <utility>

namespace splitplay {

enum class LogLevel { trace = 0, debug = 1, info = 2, warn = 3, error = 4 };

std::string_view to_string(LogLevel level) noexcept;
bool parse_log_level(std::string_view text, LogLevel& out) noexcept;

using LogSink = std::function<void(LogLevel level, std::string_view message)>;

// Name printed in every line, e.g. "seat".
void log_set_component(std::string component);
void log_set_min_level(LogLevel level);
LogLevel log_min_level() noexcept;
// Extra sink, called for records at or above `min_level`. Pass an empty function to remove it.
// The sink is never re-entered: records logged from inside the sink only go to stderr.
void log_set_sink(LogSink sink, LogLevel min_level = LogLevel::info);

void log_write(LogLevel level, std::string_view message);

template <class... A>
void log_trace(std::format_string<A...> fmt, A&&... args) {
  if (log_min_level() <= LogLevel::trace) log_write(LogLevel::trace, std::format(fmt, std::forward<A>(args)...));
}
template <class... A>
void log_debug(std::format_string<A...> fmt, A&&... args) {
  if (log_min_level() <= LogLevel::debug) log_write(LogLevel::debug, std::format(fmt, std::forward<A>(args)...));
}
template <class... A>
void log_info(std::format_string<A...> fmt, A&&... args) {
  log_write(LogLevel::info, std::format(fmt, std::forward<A>(args)...));
}
template <class... A>
void log_warn(std::format_string<A...> fmt, A&&... args) {
  log_write(LogLevel::warn, std::format(fmt, std::forward<A>(args)...));
}
template <class... A>
void log_error(std::format_string<A...> fmt, A&&... args) {
  log_write(LogLevel::error, std::format(fmt, std::forward<A>(args)...));
}

}  // namespace splitplay
