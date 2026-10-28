// SPDX-License-Identifier: MIT
// Child-side IPC state machine over an in-memory pipe: handshake, heartbeats, request dispatch, shutdown, pipe break.
#include <atomic>
#include <chrono>

#include "mem_transport.hpp"
#include "sp_test.hpp"
#include "splitplay/common/ipc_client.hpp"

using namespace splitplay;
using namespace splitplay::ipc;
using namespace std::chrono_literals;

namespace {

struct Fixture {
  std::unique_ptr<Client> client;
  std::unique_ptr<sp_test::MemTransport> service_transport;
  std::unique_ptr<sp_test::LineEnd<sp_test::MemTransport>> service;
  std::atomic<int> profiles{0};
  std::atomic<int> exits{0};
  json last_profile;
  std::mutex mutex;

  explicit Fixture(Client::Handlers extra = {}) {
    auto [child_end, service_end] = sp_test::make_mem_pipe();
    service_transport = std::move(service_end);
    service = std::make_unique<sp_test::LineEnd<sp_test::MemTransport>>(service_transport.get());
    Client::Handlers h = std::move(extra);
    if (!h.on_profile) {
      h.on_profile = [this](const json& p, bool) {
        std::lock_guard lock(mutex);
        last_profile = p;
        ++profiles;
        if (p.contains("fps") && p["fps"].is_number() && p["fps"].get<double>() < 0)
          return Result::failure(kErrBadRequest, "negative fps");
        return Result::success();
      };
    }
    if (!h.on_heartbeat) h.on_heartbeat = [] { return HeartbeatInfo{"testing", {{"fps", 1}}}; };
    h.on_exit = [this](ExitReason) { ++exits; };
    Client::Options o;
    o.heartbeat_interval = 30ms;
    o.welcome_timeout = 2000ms;
    client = std::make_unique<Client>(std::move(child_end), std::move(h), o);
  }
  // Stop the client threads before the members their handlers use are destroyed.
  ~Fixture() { client->stop(); }

  // Runs the handshake with the service side answering welcome.
  std::optional<json> handshake(json profile = {{"fps", 120}}) {
    std::optional<json> result;
    std::thread t([&] { result = client->handshake(Hello{"seat", "desk-1", 77, "0.1.0", 2}); });
    auto hello = service->next(2000ms);
    if (hello && hello->type == "hello") service->send(make_welcome(profile));
    t.join();
    if (hello) hello_msg = hello->body;
    return result;
  }

  json hello_msg;
};

}  // namespace

TEST_CASE("handshake sends hello and returns the welcome profile") {
  Fixture f;
  auto profile = f.handshake();
  REQUIRE(profile.has_value());
  CHECK((*profile)["fps"] == 120);
  CHECK(f.hello_msg["type"] == "hello");
  CHECK(f.hello_msg["protocol"] == 1);
  CHECK(f.hello_msg["role"] == "seat");
  CHECK(f.hello_msg["desk"] == "desk-1");
  CHECK(f.hello_msg["pid"] == 77);
  CHECK(f.hello_msg["session"] == 2);
  CHECK_EQ(f.profiles.load(), 1);
  f.client->stop();
}

TEST_CASE("heartbeats every interval with increasing seq") {
  Fixture f;
  REQUIRE(f.handshake().has_value());
  REQUIRE(f.client->send_ready({{"ports", {{"base", 0}}}}));
  auto ready = f.service->next_of("ready", 1000ms);
  REQUIRE(ready.has_value());
  uint64_t last = 0;
  for (int i = 0; i < 3; ++i) {
    auto hb = f.service->next_of("heartbeat", 1000ms);
    REQUIRE(hb.has_value());
    const auto seq = get_int(hb->body, "seq");
    REQUIRE(seq.has_value());
    CHECK(static_cast<uint64_t>(*seq) > last);
    last = static_cast<uint64_t>(*seq);
    CHECK(hb->body["state"] == "testing");
    CHECK(hb->body["metrics"]["fps"] == 1);
  }
  f.client->stop();
}

TEST_CASE("unknown types, unsupported requests and handler results") {
  Client::Handlers h;
  h.on_request = [](const Message& m) -> std::optional<Result> {
    if (m.type == "list_clients") return Result::success({{"clients", json::array()}});
    return std::nullopt;
  };
  Fixture f(std::move(h));
  REQUIRE(f.handshake().has_value());

  f.service->send(make_request("reboot", 1));
  auto r = f.service->result(1, 1000ms);
  REQUIRE(r.has_value());
  CHECK(r->body["ok"] == false);
  CHECK(r->body["error"]["code"] == "unknown_type");

  f.service->send(json{{"type", "reboot"}});  // no id: ignored, no answer
  f.service->send(make_request("send_sas", 2));
  r = f.service->result(2, 1000ms);
  REQUIRE(r.has_value());
  CHECK(r->body["ok"] == false);
  CHECK(r->body["error"]["code"] == "unsupported");

  f.service->send(make_request("list_clients", 3));
  r = f.service->result(3, 1000ms);
  REQUIRE(r.has_value());
  CHECK(r->body["ok"] == true);
  CHECK(r->body["data"]["clients"].is_array());

  f.service->send(make_request("pair", 4, {{"pin", "1234"}, {"name", "Laptop"}}));
  r = f.service->result(4, 1000ms);
  REQUIRE(r.has_value());
  CHECK(r->body["error"]["code"] == "unsupported");
  f.client->stop();
}

TEST_CASE("set_profile is applied live or rejected") {
  Fixture f;
  REQUIRE(f.handshake().has_value());
  f.service->send(make_request("set_profile", 5, {{"profile", {{"fps", 240}, {"bitrate_kbps", 80000}}}}));
  auto r = f.service->result(5, 1000ms);
  REQUIRE(r.has_value());
  CHECK(r->body["ok"] == true);
  {
    std::lock_guard lock(f.mutex);
    CHECK(f.last_profile["fps"] == 240);
  }
  f.service->send(make_request("set_profile", 6, {{"profile", {{"fps", -1}}}}));
  r = f.service->result(6, 1000ms);
  REQUIRE(r.has_value());
  CHECK(r->body["ok"] == false);
  CHECK(r->body["error"]["code"] == "bad_request");
  f.service->send(make_request("set_profile", 7));
  r = f.service->result(7, 1000ms);
  REQUIRE(r.has_value());
  CHECK(r->body["ok"] == false);
  f.client->stop();
}

TEST_CASE("shutdown is answered and ends wait()") {
  Fixture f;
  REQUIRE(f.handshake().has_value());
  f.service->send(make_request("shutdown", 10));
  auto r = f.service->result(10, 1000ms);
  REQUIRE(r.has_value());
  CHECK(r->body["ok"] == true);
  CHECK(f.client->wait(2000ms) == ExitReason::shutdown);
  CHECK(f.client->shutdown_id() == std::optional<int64_t>(10));
  // A pipe break after shutdown does not change the reason.
  f.service_transport->close();
  f.client->stop();
  CHECK(f.client->exit_reason() == ExitReason::shutdown);
  CHECK_EQ(f.exits.load(), 1);
}

TEST_CASE("pipe break ends wait() with disconnected") {
  Fixture f;
  REQUIRE(f.handshake().has_value());
  f.service_transport->close();
  CHECK(f.client->wait(2000ms) == ExitReason::disconnected);
  CHECK_EQ(f.exits.load(), 1);
  CHECK(!f.client->send_log("info", "after break"));
  f.client->stop();
}

TEST_CASE("handshake fails on protocol mismatch, close and timeout") {
  {
    Fixture f;
    std::optional<json> result;
    std::string err;
    std::thread t([&] { result = f.client->handshake(Hello{"seat", "d", 1, "0", 0}, &err); });
    REQUIRE(f.service->next(1000ms).has_value());
    f.service->send(make_welcome(json::object(), 2));
    t.join();
    CHECK(!result.has_value());
    CHECK(err.find("protocol") != std::string::npos);
    f.client->stop();
  }
  {
    Fixture f;
    std::optional<json> result;
    std::thread t([&] { result = f.client->handshake(Hello{"seat", "d", 1, "0", 0}); });
    REQUIRE(f.service->next(1000ms).has_value());
    f.service_transport->close();
    t.join();
    CHECK(!result.has_value());
    f.client->stop();
  }
  {
    Fixture f;  // service never answers: welcome_timeout (2 s)
    const auto t0 = std::chrono::steady_clock::now();
    auto result = f.client->handshake(Hello{"seat", "d", 1, "0", 0});
    CHECK(!result.has_value());
    CHECK(std::chrono::steady_clock::now() - t0 >= 1900ms);
    f.client->stop();
  }
}

TEST_CASE("on_welcome sees the whole welcome before the profile, set_mode goes to on_request") {
  std::string password;
  std::atomic<int> order{0};
  int welcome_at = 0, profile_at = 0;
  Client::Handlers h;
  h.on_welcome = [&](json& w) {
    welcome_at = ++order;
    if (json* rdp = w.contains("rdp") ? &w["rdp"] : nullptr; rdp && (*rdp)["password"].is_string()) {
      password = (*rdp)["password"].get<std::string>();
      secure_wipe((*rdp)["password"].get_ref<std::string&>());
    }
  };
  h.on_profile = [&](const json&, bool) {
    profile_at = ++order;
    return Result::success();
  };
  h.on_request = [](const Message& m) -> std::optional<Result> {
    if (m.type != "set_mode") return std::nullopt;
    return Result::success(json{{"width", m.body.value("width", 0)}});
  };
  Fixture f(std::move(h));
  std::optional<json> result;
  std::thread t([&] { result = f.client->handshake(Hello{"seat", "desk-1", 77, "0.1.0", 2}); });
  auto hello = f.service->next(2000ms);
  REQUIRE(hello.has_value());
  json welcome = make_welcome({{"fps", 60}});
  welcome["rdp"] = {{"user", "u"}, {"password", "s3cret"}};
  f.service->send(welcome);
  t.join();
  REQUIRE(result.has_value());
  CHECK(password == "s3cret");
  CHECK(welcome_at == 1);
  CHECK(profile_at == 2);

  f.service->send(make_request("set_mode", 21, {{"width", 1280}, {"height", 720}}));
  auto r = f.service->result(21, 1000ms);
  REQUIRE(r.has_value());
  CHECK(r->body.value("ok", false));
  CHECK(r->body["data"]["width"] == 1280);
  f.client->stop();
}

TEST_CASE("malformed lines are ignored") {
  Fixture f;
  REQUIRE(f.handshake().has_value());
  f.service_transport->write_all("garbage\n{\"no_type\":1}\n");
  f.service->send(make_request("send_sas", 11));
  auto r = f.service->result(11, 1000ms);
  REQUIRE(r.has_value());
  CHECK(f.client->exit_reason() == ExitReason::none);
  f.client->stop();
}

SP_TEST_MAIN()
