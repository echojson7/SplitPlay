// SPDX-License-Identifier: MIT
// Windows kernel objects: named sections and events of both rings, and the IPC client over a real named pipe.
#include <windows.h>

#include <atomic>
#include <format>
#include <thread>

#include "sp_test.hpp"
#include "splitplay/common/input_ring.hpp"
#include "splitplay/common/ipc_client.hpp"
#include "splitplay/common/pipe_transport.hpp"
#include "splitplay/common/qpc.hpp"
#include "splitplay/common/shared_atomic.hpp"
#include "splitplay/common/shared_memory.hpp"

using namespace splitplay;
using namespace std::chrono_literals;

namespace {
std::string unique_desk(const char* tag) { return std::format("t-{}-{}", tag, GetCurrentProcessId()); }
}  // namespace

TEST_CASE("input ring section and event by name") {
  const std::string desk = unique_desk("in");
  std::string err;
  auto seat = shm::InputRingMapping::create(ObjectNamespace::local, desk, 1, &err);
  REQUIRE_MSG(seat != nullptr, err);
  CHECK_EQ(seat->ring()->consumer_pid, GetCurrentProcessId());
  CHECK_EQ(seat->ring()->flags, 1u);

  auto worker = shm::InputRingMapping::open(ObjectNamespace::local, desk, &err);
  REQUIRE_MSG(worker != nullptr, err);
  CHECK(worker->ring() != seat->ring());  // separate views of the same section
  CHECK(worker->section->size() >= sizeof(SpInputRing));

  input::Producer p(worker->ring());
  input::Consumer c(seat->ring());
  std::atomic<int> got{0};
  std::thread consumer([&] {
    SpInputEvent e{};
    while (got.load() < 100) {
      if (seat->input_ready->wait(2000) != WAIT_OBJECT_0) break;
      while (c.pop(e)) ++got;
    }
  });
  for (int i = 0; i < 100; ++i) {
    SpInputEvent e{};
    e.type = SP_IN_KEY;
    e.a = i;
    e.qpc = qpc_now();
    CHECK(p.push(e));
    worker->input_ready->set();
  }
  consumer.join();
  CHECK_EQ(got.load(), 100);

  CHECK(shm::InputRingMapping::open(ObjectNamespace::local, unique_desk("missing"), &err) == nullptr);
  CHECK(!err.empty());
}

TEST_CASE("capture ring section and frame-ready event by name") {
  const std::string desk = unique_desk("cap");
  std::string err;
  capture::RingFormat f;
  f.width = 640;
  f.height = 360;
  f.refresh_num = 240;
  auto producer_side = shm::CaptureRingMapping::create(ObjectNamespace::local, desk, f, &err);
  REQUIRE_MSG(producer_side != nullptr, err);
  auto consumer_side = shm::CaptureRingMapping::open(ObjectNamespace::local, desk, &err);
  REQUIRE_MSG(consumer_side != nullptr, err);
  CHECK_EQ(consumer_side->header()->width, 640u);
  CHECK_EQ(consumer_side->header()->producer_pid, GetCurrentProcessId());

  capture::Producer p(producer_side->header());
  capture::Consumer c(consumer_side->header());
  p.set_generation(1, 640, 360);
  auto slot = p.begin_frame();
  REQUIRE(slot.has_value());
  capture::PublishInfo info;
  info.frame_id = 1;
  info.generation = 1;
  p.publish(*slot, info);
  producer_side->frame_ready->set();
  CHECK_EQ(consumer_side->frame_ready->wait(1000), static_cast<DWORD>(WAIT_OBJECT_0));
  capture::AcquiredSlot a;
  REQUIRE(c.try_acquire(a));
  CHECK_EQ(a.info.frame_id, 1u);
  // Auto-reset: the event is consumed by one wait.
  CHECK_EQ(consumer_side->frame_ready->wait(0), static_cast<DWORD>(WAIT_TIMEOUT));
  c.release(a.slot);

  // A corrupted header is rejected by the consumer.
  producer_side->header()->magic = 0;
  CHECK(shm::CaptureRingMapping::open(ObjectNamespace::local, desk, &err) == nullptr);
}

TEST_CASE("ipc client over a real named pipe") {
  const std::string pipe = control_pipe_name("seat", unique_desk("pipe"));
  std::string err;
  auto server = ipc::PipeServer::create(pipe, &err);
  REQUIRE_MSG(server != nullptr, err);

  std::unique_ptr<ipc::PipeTransport> service;
  std::string accept_err;
  std::thread accept([&] { service = server->accept(5s, &accept_err); });
  auto child_transport = ipc::PipeTransport::connect(pipe, 5s, &err);
  accept.join();
  REQUIRE_MSG(child_transport != nullptr, err);
  REQUIRE_MSG(service != nullptr, accept_err);

  ipc::Client::Handlers h;
  h.on_heartbeat = [] { return ipc::HeartbeatInfo{"idle", {{"fps", 0}}}; };
  ipc::Client::Options o;
  o.heartbeat_interval = 50ms;
  ipc::Client client(std::move(child_transport), std::move(h), o);

  // Service side: read hello, answer welcome, then read whatever comes.
  std::atomic<int> heartbeats{0};
  std::atomic<bool> got_hello{false}, got_ready{false}, got_shutdown_result{false};
  std::thread svc([&] {
    ipc::LineFramer framer;
    char buf[4096];
    for (;;) {
      const auto n = service->read_some(buf, sizeof(buf));
      if (n <= 0) return;
      for (auto& line : framer.push(std::string_view(buf, static_cast<size_t>(n)))) {
        auto m = ipc::parse_line(line);
        if (!m) continue;
        if (m->type == "hello") {
          got_hello = true;
          service->write_all(*ipc::encode_line(ipc::make_welcome({{"fps", 60}})));
        } else if (m->type == "ready") {
          got_ready = true;
        } else if (m->type == "heartbeat") {
          if (++heartbeats == 3) service->write_all(*ipc::encode_line(ipc::make_request("shutdown", 1)));
        } else if (m->type == "result" && m->id == std::optional<int64_t>(1)) {
          got_shutdown_result = true;
        }
      }
    }
  });

  auto profile = client.handshake(ipc::Hello{"seat", "x", GetCurrentProcessId(), "0", 0}, &err);
  REQUIRE_MSG(profile.has_value(), err);
  CHECK((*profile)["fps"] == 60);
  CHECK(client.send_ready());
  CHECK(client.wait(5000ms) == ipc::ExitReason::shutdown);
  std::this_thread::sleep_for(100ms);
  client.stop();      // closes the child end
  service->close();   // unblocks the service reader if needed
  svc.join();
  CHECK(got_hello.load());
  CHECK(got_ready.load());
  CHECK(heartbeats.load() >= 3);
  CHECK(got_shutdown_result.load());
}

TEST_CASE("pipe client gives up when no server exists") {
  std::string err;
  const auto t0 = std::chrono::steady_clock::now();
  auto t = ipc::PipeTransport::connect(control_pipe_name("seat", unique_desk("none")), 300ms, &err);
  CHECK(t == nullptr);
  CHECK(!err.empty());
  CHECK(std::chrono::steady_clock::now() - t0 >= 250ms);
}

TEST_CASE("pipe break is reported as disconnected") {
  const std::string pipe = control_pipe_name("worker", unique_desk("brk"));
  std::string err;
  auto server = ipc::PipeServer::create(pipe, &err);
  REQUIRE(server != nullptr);
  std::unique_ptr<ipc::PipeTransport> service;
  std::string accept_err;
  std::thread accept([&] { service = server->accept(5s, &accept_err); });
  auto child_transport = ipc::PipeTransport::connect(pipe, 5s, &err);
  accept.join();
  REQUIRE(child_transport && service);
  ipc::Client client(std::move(child_transport), {});
  std::thread svc([&] {
    char buf[4096];
    const auto n = service->read_some(buf, sizeof(buf));
    if (n > 0) service->write_all(*ipc::encode_line(ipc::make_welcome(ipc::json::object())));
  });
  REQUIRE(client.handshake(ipc::Hello{"stubworker", "x", 1, "0", 0}).has_value());
  svc.join();
  service.reset();  // closes the server handle
  CHECK(client.wait(5000ms) == ipc::ExitReason::disconnected);
  client.stop();
}

SP_TEST_MAIN()
