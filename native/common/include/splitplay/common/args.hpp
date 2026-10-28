// SPDX-License-Identifier: MIT
// Tiny command-line parser: `--name value`, `--name=value` and boolean `--flag`.
#pragma once

#include <cstdint>
#include <initializer_list>
#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace splitplay {

class Args {
 public:
  // `flags` lists the options that take no value. Every other `--option` consumes the next argument.
  // Returns std::nullopt and sets `error` on malformed input.
  static std::optional<Args> parse(int argc, const char* const* argv, std::initializer_list<std::string_view> flags,
                                   std::string* error);

  bool has(std::string_view name) const;
  std::optional<std::string> get(std::string_view name) const;
  std::string get_or(std::string_view name, std::string_view fallback) const;
  // Parses a decimal integer; returns std::nullopt when absent, `false` in `ok` when malformed.
  std::optional<int64_t> get_int(std::string_view name, bool* ok = nullptr) const;

  // Names of options that are not in `known` (for "unknown option" diagnostics).
  std::vector<std::string> unknown(std::initializer_list<std::string_view> known) const;

  const std::vector<std::string>& positional() const { return positional_; }

 private:
  std::map<std::string, std::string, std::less<>> values_;
  std::vector<std::string> positional_;
};

}  // namespace splitplay
