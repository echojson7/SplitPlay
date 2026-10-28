// SPDX-License-Identifier: MIT
// Control IPC codec: parsing, builders matching docs/contracts/ipc.md, line framing.
#include <string>

#include "sp_test.hpp"
#include "splitplay/common/ipc_codec.hpp"

using namespace splitplay::ipc;

TEST_CASE("parse valid messages") {
  auto m = parse_line(R"({"type":"shutdown","id":10})");
  REQUIRE(m.has_value());
  CHECK(m->type == "shutdown");
  REQUIRE(m->id.has_value());
  CHECK_EQ(*m->id, 10);

  m = parse_line("{\"type\":\"welcome\",\"protocol\":1,\"profile\":{\"fps\":120}}\r\n");
  REQUIRE(m.has_value());
  CHECK(m->type == "welcome");
  CHECK(!m->id.has_value());
  CHECK_EQ(*get_int(*get_object(m->body, "profile"), "fps"), 120);

  m = parse_line(R"({"type":"x","id":null,"unknown":[1,2,3]})");
  REQUIRE(m.has_value());
  CHECK(!m->id.has_value());
}

TEST_CASE("reject invalid messages") {
  std::string err;
  CHECK(!parse_line("", &err).has_value());
  CHECK(!parse_line("not json", &err).has_value());
  CHECK(!parse_line("[1,2]", &err).has_value());
  CHECK(!parse_line(R"({"id":1})", &err).has_value());
  CHECK(!parse_line(R"({"type":5})", &err).has_value());
  CHECK(!parse_line(R"({"type":"pair","id":"7"})", &err).has_value());
  CHECK(!parse_line(R"({"type":"pair","id":1.5})", &err).has_value());
  CHECK(!parse_line(R"({"type":"pair"} trailing)", &err).has_value());
  CHECK(!err.empty());
}

TEST_CASE("child messages match the contract") {
  Hello h{"seat", "desk-1", 1234, "0.1.0", 3};
  json hello = make_hello(h);
  CHECK(hello == json::parse(R"({"type":"hello","protocol":1,"role":"seat","desk":"desk-1","pid":1234,"version":"0.1.0","session":3})"));

  json ready = make_ready({{"ports", {{"base", 48089}}}});
  CHECK(ready == json::parse(R"({"type":"ready","ports":{"base":48089}})"));
  CHECK(make_ready() == json::parse(R"({"type":"ready"})"));

  json hb = make_heartbeat(17, "idle", {{"fps", 0}, {"clients", 0}});
  CHECK(hb == json::parse(R"({"type":"heartbeat","seq":17,"state":"idle","metrics":{"fps":0,"clients":0}})"));

  CHECK(make_log("info", "hi") == json::parse(R"({"type":"log","level":"info","message":"hi"})"));
  CHECK(make_event("client_connected", {{"client", "Living room PC"}}) ==
        json::parse(R"({"type":"event","name":"client_connected","data":{"client":"Living room PC"}})"));
}

TEST_CASE("results") {
  CHECK(make_result_ok(5) == json::parse(R"({"type":"result","id":5,"ok":true})"));
  CHECK(make_result_ok(5, {{"clients", json::array()}}) ==
        json::parse(R"({"type":"result","id":5,"ok":true,"data":{"clients":[]}})"));
  CHECK(make_result_error(9, "unsupported", "no") ==
        json::parse(R"({"type":"result","id":9,"ok":false,"error":{"code":"unsupported","message":"no"}})"));
}

TEST_CASE("encode produces one line") {
  auto line = encode_line(make_log("info", "multi\nline \"quoted\""));
  REQUIRE(line.has_value());
  CHECK(line->back() == '\n');
  CHECK(line->find('\n') == line->size() - 1);
  auto back = parse_line(*line);
  REQUIRE(back.has_value());
  CHECK(*get_string(back->body, "message") == "multi\nline \"quoted\"");
}

TEST_CASE("encode replaces invalid utf-8 instead of throwing") {
  json m = make_log("warn", std::string("bad \xff\xfe bytes"));
  auto line = encode_line(m);
  REQUIRE(line.has_value());
  CHECK(parse_line(*line).has_value());
}

TEST_CASE("encode enforces the 64 KiB line limit") {
  json big{{"type", "event"}, {"name", "x"}, {"data", {{"blob", std::string(70000, 'a')}}}};
  CHECK(!encode_line(big).has_value());
  // log messages are truncated by the builder so they always fit
  auto line = encode_line(make_log("info", std::string(100000, 'b')));
  REQUIRE(line.has_value());
  CHECK(line->size() < kMaxLineBytes);
}

TEST_CASE("line framer handles partial, multiple and CRLF lines") {
  LineFramer f;
  auto lines = f.push("{\"type\":\"a\"}\n{\"ty");
  REQUIRE(lines.size() == 1);
  CHECK(lines[0] == "{\"type\":\"a\"}");
  CHECK(f.buffered_bytes() == 4);
  lines = f.push("pe\":\"b\"}\r\n\n{\"type\":\"c\"}\n");
  REQUIRE(lines.size() == 2);
  CHECK(lines[0] == "{\"type\":\"b\"}");
  CHECK(lines[1] == "{\"type\":\"c\"}");
  CHECK(f.buffered_bytes() == 0);
}

TEST_CASE("line framer discards oversized lines and recovers") {
  LineFramer f;
  std::string huge(kMaxLineBytes + 10, 'x');
  auto lines = f.push(huge.substr(0, 40000));
  CHECK(lines.empty());
  lines = f.push(huge.substr(40000));
  CHECK(lines.empty());
  lines = f.push("still the same line\n{\"type\":\"ok\"}\n");
  REQUIRE(lines.size() == 1);
  CHECK(lines[0] == "{\"type\":\"ok\"}");
  CHECK_EQ(f.oversized_lines(), 1u);
  // A line of exactly the limit (including '\n') is too long; one byte less fits.
  lines = f.push(std::string(kMaxLineBytes - 1, 'y') + "\n");
  CHECK_EQ(lines.size(), 1u);
  lines = f.push(std::string(kMaxLineBytes, 'z') + "\n");
  CHECK(lines.empty());
}

TEST_CASE("known service messages") {
  for (const char* t : {"welcome", "set_profile", "pair", "list_clients", "unpair", "send_sas", "shutdown"})
    CHECK(is_known_service_message(t));
  CHECK(!is_known_service_message("reboot"));
  CHECK(!is_known_service_message("hello"));
}

TEST_CASE("safe accessors never throw") {
  json j = json::parse(R"({"s":"x","i":5,"u":18446744073709551615,"f":2.0,"g":2.5,"b":true,"o":{}})");
  CHECK(*get_string(j, "s") == "x");
  CHECK(!get_string(j, "i").has_value());
  CHECK_EQ(*get_int(j, "i"), 5);
  CHECK(!get_int(j, "u").has_value());
  CHECK_EQ(*get_int(j, "f"), 2);
  CHECK(!get_int(j, "g").has_value());
  CHECK(!get_int(j, "s").has_value());
  CHECK(*get_bool(j, "b"));
  CHECK(get_object(j, "o") != nullptr);
  CHECK(get_object(j, "s") == nullptr);
  CHECK(!get_int(json::array(), "x").has_value());
}

SP_TEST_MAIN()
