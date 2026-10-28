// SPDX-License-Identifier: MIT
// JSON-lines codec for the control IPC (docs/contracts/ipc.md): framing, parsing and message builders.
#pragma once

#include <cstddef>
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include <nlohmann/json.hpp>

namespace splitplay::ipc {

using json = nlohmann::json;

inline constexpr size_t kMaxLineBytes = 64 * 1024;  // including the terminating '\n'

// Error codes used in result messages.
inline constexpr const char* kErrUnknownType = "unknown_type";
inline constexpr const char* kErrUnsupported = "unsupported";
inline constexpr const char* kErrBadRequest = "bad_request";
inline constexpr const char* kErrInternal = "internal";

struct Message {
  std::string type;
  std::optional<int64_t> id;
  json body;  // the whole object, including "type" and "id"
};

// Parses one line (without or with the trailing '\n'). Requires a JSON object with a string "type".
// An "id" that is present but not an integer makes the message invalid.
std::optional<Message> parse_line(std::string_view line, std::string* error = nullptr);

// Serialises one message as a single line terminated by '\n'. Invalid UTF-8 is replaced, never thrown.
// Returns std::nullopt if the line would exceed kMaxLineBytes.
std::optional<std::string> encode_line(const json& message);

// Splits a byte stream into lines. Lines longer than kMaxLineBytes are discarded up to the next '\n'.
class LineFramer {
 public:
  // Appends bytes and returns the complete lines found (without '\n'; a trailing '\r' is stripped).
  std::vector<std::string> push(std::string_view bytes);
  size_t oversized_lines() const noexcept { return oversized_; }
  size_t buffered_bytes() const noexcept { return buffer_.size(); }

 private:
  std::string buffer_;
  bool discarding_ = false;
  size_t oversized_ = 0;
};

// The service -> child message types defined by protocol version 1.
bool is_known_service_message(std::string_view type) noexcept;

// ---- builders (child -> service) ----------------------------------------------------------------
struct Hello {
  std::string role;     // "worker", "seat" or "stubworker"
  std::string desk;
  uint32_t pid = 0;
  std::string version;
  int64_t session = -1; // Windows session id of the child process
};

json make_hello(const Hello& hello);
json make_ready(const json& extra = json::object());  // extra fields are merged in, e.g. {"ports":{"base":48089}}
json make_heartbeat(uint64_t seq, std::string_view state, const json& metrics);
json make_log(std::string_view level, std::string_view message);
json make_event(std::string_view name, const json& data = json::object());

// ---- results ------------------------------------------------------------------------------------
json make_result_ok(int64_t id, const json& data = nullptr);
json make_result_error(int64_t id, std::string_view code, std::string_view message);

// ---- builders (service -> child), used by tests and the fake service -----------------------------
json make_welcome(const json& profile, int protocol = 1);
json make_request(std::string_view type, int64_t id, const json& fields = json::object());

// ---- safe accessors (never throw) ------------------------------------------------------------------
std::optional<std::string> get_string(const json& object, std::string_view key);
std::optional<int64_t> get_int(const json& object, std::string_view key);
std::optional<double> get_number(const json& object, std::string_view key);
std::optional<bool> get_bool(const json& object, std::string_view key);
const json* get_object(const json& object, std::string_view key);

}  // namespace splitplay::ipc
