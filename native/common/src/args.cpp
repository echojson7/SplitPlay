// SPDX-License-Identifier: MIT
#include "splitplay/common/args.hpp"

#include <algorithm>
#include <charconv>

namespace splitplay {

std::optional<Args> Args::parse(int argc, const char* const* argv, std::initializer_list<std::string_view> flags,
                                std::string* error) {
  Args args;
  for (int i = 1; i < argc; ++i) {
    std::string_view arg = argv[i] ? argv[i] : "";
    if (arg.size() < 3 || arg.substr(0, 2) != "--") {
      args.positional_.emplace_back(arg);
      continue;
    }
    arg.remove_prefix(2);
    std::string name;
    std::string value;
    if (auto eq = arg.find('='); eq != std::string_view::npos) {
      name = std::string(arg.substr(0, eq));
      value = std::string(arg.substr(eq + 1));
    } else {
      name = std::string(arg);
      const bool is_flag = std::find(flags.begin(), flags.end(), std::string_view(name)) != flags.end();
      if (!is_flag) {
        if (i + 1 >= argc || argv[i + 1] == nullptr) {
          if (error) *error = "option --" + name + " needs a value";
          return std::nullopt;
        }
        value = argv[++i];
      } else {
        value = "1";
      }
    }
    args.values_[name] = value;
  }
  return args;
}

bool Args::has(std::string_view name) const { return values_.find(name) != values_.end(); }

std::optional<std::string> Args::get(std::string_view name) const {
  auto it = values_.find(name);
  if (it == values_.end()) return std::nullopt;
  return it->second;
}

std::string Args::get_or(std::string_view name, std::string_view fallback) const {
  auto v = get(name);
  return v ? *v : std::string(fallback);
}

std::optional<int64_t> Args::get_int(std::string_view name, bool* ok) const {
  if (ok) *ok = true;
  auto v = get(name);
  if (!v) return std::nullopt;
  int64_t out = 0;
  const char* begin = v->data();
  const char* end = v->data() + v->size();
  auto [ptr, ec] = std::from_chars(begin, end, out);
  if (ec != std::errc() || ptr != end || v->empty()) {
    if (ok) *ok = false;
    return std::nullopt;
  }
  return out;
}

std::vector<std::string> Args::unknown(std::initializer_list<std::string_view> known) const {
  std::vector<std::string> out;
  for (const auto& [name, value] : values_) {
    (void)value;
    if (std::find(known.begin(), known.end(), std::string_view(name)) == known.end()) out.push_back(name);
  }
  return out;
}

}  // namespace splitplay
