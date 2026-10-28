// SPDX-License-Identifier: MIT
#include "splitplay/common/ipc_codec.hpp"

#include <array>
#include <cstdint>

namespace splitplay::ipc {

std::optional<Message> parse_line(std::string_view line, std::string* error) {
  auto fail = [&](const char* msg) -> std::optional<Message> {
    if (error) *error = msg;
    return std::nullopt;
  };
  while (!line.empty() && (line.back() == '\n' || line.back() == '\r')) line.remove_suffix(1);
  if (line.empty()) return fail("empty line");
  if (line.size() >= kMaxLineBytes) return fail("line too long");
  json body = json::parse(line.begin(), line.end(), nullptr, /*allow_exceptions=*/false);
  if (body.is_discarded()) return fail("invalid JSON");
  if (!body.is_object()) return fail("message is not a JSON object");
  auto type = body.find("type");
  if (type == body.end() || !type->is_string()) return fail("message has no string \"type\"");
  Message msg;
  msg.type = type->get<std::string>();
  if (auto id = body.find("id"); id != body.end()) {
    // Note: nlohmann's is_number_integer() is also true for unsigned values, so test unsigned first.
    if (id->is_number_unsigned()) {
      const uint64_t v = id->get<uint64_t>();
      if (v > static_cast<uint64_t>(INT64_MAX)) return fail("\"id\" out of range");
      msg.id = static_cast<int64_t>(v);
    } else if (id->is_number_integer()) {
      msg.id = id->get<int64_t>();
    } else if (!id->is_null()) {
      return fail("\"id\" is not an integer");
    }
  }
  msg.body = std::move(body);
  return msg;
}

std::optional<std::string> encode_line(const json& message) {
  std::string line = message.dump(-1, ' ', /*ensure_ascii=*/false, json::error_handler_t::replace);
  line.push_back('\n');
  if (line.size() > kMaxLineBytes) return std::nullopt;
  return line;
}

std::vector<std::string> LineFramer::push(std::string_view bytes) {
  std::vector<std::string> lines;
  while (!bytes.empty()) {
    const size_t nl = bytes.find('\n');
    const std::string_view chunk = nl == std::string_view::npos ? bytes : bytes.substr(0, nl);
    if (!discarding_) {
      if (buffer_.size() + chunk.size() >= kMaxLineBytes) {
        // Too long: drop what we have and skip to the next newline.
        buffer_.clear();
        discarding_ = true;
        ++oversized_;
      } else {
        buffer_.append(chunk);
      }
    }
    if (nl == std::string_view::npos) break;
    if (!discarding_) {
      if (!buffer_.empty() && buffer_.back() == '\r') buffer_.pop_back();
      if (!buffer_.empty()) lines.push_back(std::move(buffer_));
      buffer_.clear();
    }
    discarding_ = false;
    bytes.remove_prefix(nl + 1);
  }
  return lines;
}

bool is_known_service_message(std::string_view type) noexcept {
  static constexpr std::array<std::string_view, 8> kTypes = {"welcome", "set_profile", "pair",     "list_clients",
                                                             "unpair",  "send_sas",    "shutdown", "result"};
  for (auto t : kTypes)
    if (t == type) return true;
  return false;
}

json make_hello(const Hello& hello) {
  return json{{"type", "hello"},     {"protocol", 1},          {"role", hello.role},      {"desk", hello.desk},
              {"pid", hello.pid},    {"version", hello.version}, {"session", hello.session}};
}

json make_ready(const json& extra) {
  json msg = json::object();
  if (extra.is_object()) msg = extra;
  msg["type"] = "ready";
  return msg;
}

json make_heartbeat(uint64_t seq, std::string_view state, const json& metrics) {
  return json{{"type", "heartbeat"},
              {"seq", seq},
              {"state", state},
              {"metrics", metrics.is_object() ? metrics : json::object()}};
}

json make_log(std::string_view level, std::string_view message) {
  // Keep log lines comfortably below the 64 KiB line limit.
  constexpr size_t kMaxMessage = 16 * 1024;
  std::string text(message.substr(0, kMaxMessage));
  if (message.size() > kMaxMessage) text += "...";
  return json{{"type", "log"}, {"level", level}, {"message", std::move(text)}};
}

json make_event(std::string_view name, const json& data) {
  return json{{"type", "event"}, {"name", name}, {"data", data.is_object() ? data : json::object()}};
}

json make_result_ok(int64_t id, const json& data) {
  json msg{{"type", "result"}, {"id", id}, {"ok", true}};
  if (!data.is_null()) msg["data"] = data;
  return msg;
}

json make_result_error(int64_t id, std::string_view code, std::string_view message) {
  return json{{"type", "result"}, {"id", id}, {"ok", false}, {"error", {{"code", code}, {"message", message}}}};
}

json make_welcome(const json& profile, int protocol) {
  return json{{"type", "welcome"}, {"protocol", protocol}, {"profile", profile.is_object() ? profile : json::object()}};
}

json make_request(std::string_view type, int64_t id, const json& fields) {
  json msg = fields.is_object() ? fields : json::object();
  msg["type"] = type;
  msg["id"] = id;
  return msg;
}

std::optional<std::string> get_string(const json& object, std::string_view key) {
  if (!object.is_object()) return std::nullopt;
  auto it = object.find(key);
  if (it == object.end() || !it->is_string()) return std::nullopt;
  return it->get<std::string>();
}

std::optional<int64_t> get_int(const json& object, std::string_view key) {
  if (!object.is_object()) return std::nullopt;
  auto it = object.find(key);
  if (it == object.end()) return std::nullopt;
  if (it->is_number_unsigned()) {
    const uint64_t v = it->get<uint64_t>();
    if (v > static_cast<uint64_t>(INT64_MAX)) return std::nullopt;
    return static_cast<int64_t>(v);
  }
  if (it->is_number_integer()) return it->get<int64_t>();
  if (it->is_number_float()) {
    const double d = it->get<double>();
    if (d >= -9.2e18 && d <= 9.2e18 && d == static_cast<double>(static_cast<int64_t>(d))) return static_cast<int64_t>(d);
  }
  return std::nullopt;
}

std::optional<double> get_number(const json& object, std::string_view key) {
  if (!object.is_object()) return std::nullopt;
  auto it = object.find(key);
  if (it == object.end() || !it->is_number()) return std::nullopt;
  return it->get<double>();
}

std::optional<bool> get_bool(const json& object, std::string_view key) {
  if (!object.is_object()) return std::nullopt;
  auto it = object.find(key);
  if (it == object.end() || !it->is_boolean()) return std::nullopt;
  return it->get<bool>();
}

const json* get_object(const json& object, std::string_view key) {
  if (!object.is_object()) return nullptr;
  auto it = object.find(key);
  if (it == object.end() || !it->is_object()) return nullptr;
  return &*it;
}

}  // namespace splitplay::ipc
