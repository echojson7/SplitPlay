// SPDX-License-Identifier: MIT
// sp_fake_service: plays the SplitPlay service for one child process over a real named pipe.
//
//   sp_fake_service --child <exe> --role seat|stubworker --scenario shutdown|break
//
// Creates the control pipe, starts the child in a kill-on-close job object, checks hello -> welcome -> ready,
// heartbeats, error answers and role-specific behaviour, then either requests shutdown (child must answer and exit 0)
// or breaks the pipe (child must exit on its own with kExitPipeBroken). Exit code 0 means the scenario passed.
//
//   sp_fake_service --child <seat exe> --role seat --scenario rdp --rdp-user <local user>   (SP_TEST_RDP_PASSWORD)
//
// Real-session test: sends `welcome` with RDP credentials, waits for `ready` with a session id that WTS reports as
// active for that user, resizes it with set_mode, then sends `shutdown` and checks that the session is logged off.
// Needs RDP enabled on the machine and the user in Remote Desktop Users (see .github/workflows/native.yml).
#include <windows.h>

#include <algorithm>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <deque>
#include <format>
#include <functional>
#include <mutex>
#include <optional>
#include <string>
#include <thread>

#include "splitplay/common/args.hpp"
#include "splitplay/common/input_ring.hpp"
#include "splitplay/common/ipc_codec.hpp"
#include "splitplay/common/object_names.hpp"
#include "splitplay/common/pipe_transport.hpp"
#include "splitplay/common/qpc.hpp"
#include "splitplay/common/shared_memory.hpp"
#include "splitplay/common/version.hpp"
#include "splitplay/common/win_util.hpp"
#include "wts_session.hpp"

using namespace splitplay;
using namespace std::chrono_literals;
using ipc::json;

namespace {

struct Failure {
  std::string what;
};

[[noreturn]] void fail(std::string what) { throw Failure{std::move(what)}; }

void expect(bool cond, std::string what) {
  if (!cond) fail(std::move(what));
}

// Reads messages from the pipe on a background thread.
class Inbox {
 public:
  explicit Inbox(ipc::PipeTransport* t) : t_(t), thread_([this] { run(); }) {}
  ~Inbox() {
    t_->close();
    if (thread_.joinable()) thread_.join();
  }

  std::optional<ipc::Message> next_where(const std::function<bool(const ipc::Message&)>& pred,
                                         std::chrono::milliseconds timeout) {
    std::unique_lock lock(mutex_);
    const auto deadline = std::chrono::steady_clock::now() + timeout;
    for (;;) {
      for (auto it = queue_.begin(); it != queue_.end(); ++it) {
        if (pred(*it)) {
          ipc::Message m = std::move(*it);
          queue_.erase(it);
          return m;
        }
      }
      if (closed_) return std::nullopt;
      if (cv_.wait_until(lock, deadline) == std::cv_status::timeout) return std::nullopt;
    }
  }

  std::optional<ipc::Message> next_of(std::string_view type, std::chrono::milliseconds timeout) {
    return next_where([&](const ipc::Message& m) { return m.type == type; }, timeout);
  }

  std::optional<ipc::Message> result(int64_t id, std::chrono::milliseconds timeout) {
    return next_where([&](const ipc::Message& m) { return m.type == "result" && m.id && *m.id == id; }, timeout);
  }

  // Drops queued heartbeats so that the next one is fresh.
  void drop(std::string_view type) {
    std::lock_guard lock(mutex_);
    std::erase_if(queue_, [&](const ipc::Message& m) { return m.type == type; });
  }

 private:
  void run() {
    ipc::LineFramer framer;
    char buf[8192];
    for (;;) {
      const auto n = t_->read_some(buf, sizeof(buf));
      if (n <= 0) break;
      for (auto& line : framer.push(std::string_view(buf, static_cast<size_t>(n)))) {
        auto m = ipc::parse_line(line);
        if (!m) {
          std::fprintf(stderr, "fake-service: child sent a malformed line: %s\n", line.c_str());
          std::lock_guard lock(mutex_);
          malformed_ = true;
          continue;
        }
        if (m->type == "log") {
          // Child logs are already on its stderr; just count them.
          std::lock_guard lock(mutex_);
          ++logs_;
          continue;
        }
        std::lock_guard lock(mutex_);
        queue_.push_back(std::move(*m));
        cv_.notify_all();
      }
    }
    std::lock_guard lock(mutex_);
    closed_ = true;
    cv_.notify_all();
  }

  ipc::PipeTransport* t_;
  std::mutex mutex_;
  std::condition_variable cv_;
  std::deque<ipc::Message> queue_;
  bool closed_ = false;
  bool malformed_ = false;
  int logs_ = 0;
  std::thread thread_;

 public:
  bool malformed() {
    std::lock_guard lock(mutex_);
    return malformed_;
  }
};

bool send(ipc::PipeTransport& t, const json& msg) {
  auto line = ipc::encode_line(msg);
  return line && t.write_all(*line);
}

struct Child {
  win::UniqueHandle job;
  win::UniqueHandle process;
  DWORD pid = 0;
};

Child launch(const std::string& exe, const std::string& args) {
  Child c;
  c.job.reset(CreateJobObjectW(nullptr, nullptr));
  JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
  limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
  SetInformationJobObject(c.job.get(), JobObjectExtendedLimitInformation, &limits, sizeof(limits));
  std::string native_exe = exe;
  std::replace(native_exe.begin(), native_exe.end(), '/', '\\');  // CMake paths use forward slashes
  std::wstring cmd = win::widen("\"" + native_exe + "\" " + args);
  STARTUPINFOW si{};
  si.cb = sizeof(si);
  PROCESS_INFORMATION pi{};
  if (!CreateProcessW(nullptr, cmd.data(), nullptr, nullptr, FALSE, CREATE_SUSPENDED, nullptr, nullptr, &si, &pi))
    fail("CreateProcess failed: " + win::last_error_message());
  AssignProcessToJobObject(c.job.get(), pi.hProcess);
  ResumeThread(pi.hThread);
  CloseHandle(pi.hThread);
  c.process.reset(pi.hProcess);
  c.pid = pi.dwProcessId;
  return c;
}

std::optional<DWORD> wait_exit(const Child& c, std::chrono::milliseconds timeout) {
  if (WaitForSingleObject(c.process.get(), static_cast<DWORD>(timeout.count())) != WAIT_OBJECT_0) return std::nullopt;
  DWORD code = 0;
  GetExitCodeProcess(c.process.get(), &code);
  return code;
}

void check_error_result(const std::optional<ipc::Message>& r, const char* code, const std::string& what) {
  expect(r.has_value(), "no answer to " + what);
  expect(r->body.value("ok", true) == false, what + " should fail");
  const json* err = ipc::get_object(r->body, "error");
  expect(err && ipc::get_string(*err, "code") == std::string(code),
         std::format("{} should fail with {}, got {}", what, code, r->body.dump()));
  expect(err && ipc::get_string(*err, "message").has_value(), what + ": error without message");
}

int run(const std::string& exe, const std::string& role, const std::string& scenario) {
  const std::string desk = std::format("test-{}", GetCurrentProcessId());
  const std::string pipe_name = control_pipe_name(role == "seat" ? "seat" : "worker", desk);
  std::string err;
  auto server = ipc::PipeServer::create(pipe_name, &err);
  expect(server != nullptr, err);

  std::string child_args = std::format("--pipe {} --desk {} --namespace local", pipe_name, desk);
  if (role == "stubworker") child_args += " --adapter warp";
  if (role == "seat") child_args += " --connector null";  // the IPC scenarios run without an RDP stack
  std::fprintf(stderr, "fake-service: starting %s %s\n", exe.c_str(), child_args.c_str());
  Child child = launch(exe, child_args);

  try {
    auto transport = server->accept(15s, &err);
    expect(transport != nullptr, "child did not connect: " + err);
    Inbox inbox(transport.get());

    // 1. hello
    auto hello = inbox.next_of("hello", 10s);
    expect(hello.has_value(), "no hello");
    expect(ipc::get_int(hello->body, "protocol") == 1, "hello.protocol != 1");
    expect(ipc::get_string(hello->body, "role") == role, "hello.role mismatch: " + hello->body.dump());
    expect(ipc::get_string(hello->body, "desk") == desk, "hello.desk mismatch");
    expect(ipc::get_int(hello->body, "pid") == static_cast<int64_t>(child.pid), "hello.pid mismatch");
    expect(ipc::get_string(hello->body, "version").has_value(), "hello.version missing");
    expect(ipc::get_int(hello->body, "session").has_value(), "hello.session missing");

    // 2. welcome -> ready
    expect(send(*transport, ipc::make_welcome({{"fps", 60}, {"width", 320}, {"height", 180}, {"codec", "hevc"}})),
           "cannot send welcome");
    auto ready = inbox.next_of("ready", 60s);
    expect(ready.has_value(), "no ready");
    std::fprintf(stderr, "fake-service: ready %s\n", ready->body.dump().c_str());

    // 3. heartbeats about every second, seq increasing
    auto hb1 = inbox.next_of("heartbeat", 3s);
    auto hb2 = inbox.next_of("heartbeat", 3s);
    expect(hb1 && hb2, "missing heartbeats");
    expect(ipc::get_int(hb2->body, "seq") > ipc::get_int(hb1->body, "seq"), "heartbeat seq not increasing");
    expect(ipc::get_string(hb1->body, "state").has_value(), "heartbeat without state");
    expect(ipc::get_object(hb1->body, "metrics") != nullptr, "heartbeat without metrics");

    // 4. unknown type -> unknown_type; unknown type without id -> ignored
    send(*transport, ipc::make_request("reboot_the_universe", 1));
    check_error_result(inbox.result(1, 5s), ipc::kErrUnknownType, "unknown request");
    send(*transport, json{{"type", "note"}, {"text", "ignored"}});

    if (role == "seat") {
      send(*transport, ipc::make_request("send_sas", 2));
      check_error_result(inbox.result(2, 5s), ipc::kErrUnsupported, "send_sas");
      send(*transport, ipc::make_request("pair", 3, {{"pin", "1234"}, {"name", "x"}}));
      check_error_result(inbox.result(3, 5s), ipc::kErrUnsupported, "pair on the seat");

      // Play the worker: write input events into the seat's ring.
      auto ring = shm::InputRingMapping::open(ObjectNamespace::local, desk, &err);
      expect(ring != nullptr, "cannot open the seat's input ring: " + err);
      input::Producer producer(ring->ring());
      constexpr int kEvents = 50;
      for (int i = 0; i < kEvents; ++i) {
        SpInputEvent e{};
        e.qpc = qpc_now();
        e.type = i % 2 ? SP_IN_MOUSE_REL : SP_IN_KEY;
        e.a = i;
        e.b = i % 2 ? -i : 0;
        expect(producer.push(e), "input ring unexpectedly full");
      }
      ring->input_ready->set();
      inbox.drop("heartbeat");
      bool drained = false;
      for (int i = 0; i < 6 && !drained; ++i) {
        auto hb = inbox.next_of("heartbeat", 3s);
        expect(hb.has_value(), "no heartbeat while waiting for input drain");
        const json* m = ipc::get_object(hb->body, "metrics");
        drained = m && ipc::get_int(*m, "input_events").value_or(0) >= kEvents;
      }
      expect(drained, "the seat did not drain the input ring");
    } else {
      send(*transport, ipc::make_request("pair", 2, {{"pin", "1234"}, {"name", "x"}}));
      check_error_result(inbox.result(2, 5s), ipc::kErrUnsupported, "pair on the stub worker");
      send(*transport, ipc::make_request("set_profile", 3, {{"profile", {{"fps", 30}}}}));
      auto r = inbox.result(3, 5s);
      expect(r && r->body.value("ok", false), "set_profile was not accepted");
      bool flowing = false;
      for (int i = 0; i < 10 && !flowing; ++i) {
        auto hb = inbox.next_of("heartbeat", 3s);
        expect(hb.has_value(), "no heartbeat while waiting for frames");
        const json* m = ipc::get_object(hb->body, "metrics");
        expect(m && ipc::get_int(*m, "frames_corrupt").value_or(0) == 0, "stub pipeline reports corrupt frames");
        flowing = m && ipc::get_int(*m, "frames_verified").value_or(0) > 10;
      }
      expect(flowing, "no verified frames reported by the stub pipeline");
    }

    if (scenario == "shutdown") {
      send(*transport, ipc::make_request("shutdown", 100));
      auto r = inbox.result(100, 5s);
      expect(r && r->body.value("ok", false), "shutdown not acknowledged");
      auto code = wait_exit(child, 5s);
      expect(code.has_value(), "child did not exit within 5 s of shutdown");
      expect(*code == static_cast<DWORD>(kExitOk), std::format("child exit code {} after shutdown", *code));
    } else {
      transport->close();  // the Inbox destructor joins the reader; the handle closes with the transport
    }
    expect(!inbox.malformed(), "child sent malformed lines");
  } catch (...) {
    TerminateJobObject(child.job.get(), 1);
    throw;
  }

  if (scenario == "break") {
    // transport and inbox are gone: the pipe handle is closed, so the child sees a broken pipe.
    auto code = wait_exit(child, 5s);
    expect(code.has_value(), "child did not exit within 5 s of the pipe breaking");
    expect(*code == static_cast<DWORD>(kExitPipeBroken), std::format("child exit code {} after pipe break", *code));
  }
  std::fprintf(stderr, "fake-service: %s/%s PASSED\n", role.c_str(), scenario.c_str());
  return 0;
}

// ---- rdp: a real RDP-loopback session ------------------------------------------------------------------------------

struct RdpScenario {
  std::string user;
  std::string password;  // from the environment, never from the command line
  std::string host = "127.0.0.2";
  int port = 0;          // 0: read RDP-Tcp\PortNumber like the service does
  int ready_timeout_s = 240;
  int logoff_timeout_s = 120;
};

int rdp_port_from_registry() {
  DWORD port = 0;
  DWORD size = sizeof(port);
  if (RegGetValueW(HKEY_LOCAL_MACHINE, L"SYSTEM\\CurrentControlSet\\Control\\Terminal Server\\WinStations\\RDP-Tcp",
                   L"PortNumber", RRF_RT_REG_DWORD, nullptr, &port, &size) != ERROR_SUCCESS)
    return 3389;
  return static_cast<int>(port);
}

void dump_sessions() {
  std::string err;
  auto sessions = seat::wts::list_sessions(&err);
  if (!err.empty()) std::fprintf(stderr, "fake-service: %s\n", err.c_str());
  for (const auto& s : sessions) {
    std::fprintf(stderr, "fake-service:   session %u %-12s user=%s\\%s client=%s\n", s.id, seat::wts::state_name(s.state),
                 win::narrow(s.domain).c_str(), win::narrow(s.user).c_str(), win::narrow(s.client_name).c_str());
  }
}

bool same_user(const std::wstring& a, const std::string& b) {
  const std::wstring w = win::widen(b);
  return a.size() == w.size() &&
         CompareStringOrdinal(a.data(), static_cast<int>(a.size()), w.data(), static_cast<int>(w.size()), TRUE) ==
             CSTR_EQUAL;
}

int run_rdp(const std::string& exe, const RdpScenario& sc) {
  const std::string desk = std::format("rdp-{}", GetCurrentProcessId());
  const std::string pipe_name = control_pipe_name("seat", desk);
  std::string err;
  auto server = ipc::PipeServer::create(pipe_name, &err);
  expect(server != nullptr, err);

  const std::string child_args =
      std::format("--pipe {} --desk {} --namespace local --connector freerdp --log-level debug", pipe_name, desk);
  std::fprintf(stderr, "fake-service: starting %s %s\n", exe.c_str(), child_args.c_str());
  std::fprintf(stderr, "fake-service: sessions before the test:\n");
  dump_sessions();
  Child child = launch(exe, child_args);
  uint32_t sid = 0;

  try {
    auto transport = server->accept(15s, &err);
    expect(transport != nullptr, "child did not connect: " + err);
    Inbox inbox(transport.get());

    auto hello = inbox.next_of("hello", 10s);
    expect(hello.has_value(), "no hello");
    expect(ipc::get_string(hello->body, "role") == std::string("seat"), "hello.role mismatch");

    const int port = sc.port > 0 ? sc.port : rdp_port_from_registry();
    json welcome = ipc::make_welcome({{"fps", 60}, {"width", 1280}, {"height", 720}});
    welcome["rdp"] = {{"host", sc.host},     {"port", port},       {"user", sc.user}, {"domain", "."},
                      {"password", sc.password}, {"width", 1280}, {"height", 720}};
    std::fprintf(stderr, "fake-service: welcome with rdp %s:%d user .\\%s (password not shown)\n", sc.host.c_str(),
                 port, sc.user.c_str());
    expect(send(*transport, welcome), "cannot send welcome");

    // ready arrives only once the session is logged on and active.
    auto ready = inbox.next_of("ready", std::chrono::seconds(sc.ready_timeout_s));
    if (!ready) {
      dump_sessions();
      if (auto code = wait_exit(child, 5s))
        fail(std::format("the seat exited with code {} before ready (4 = RDP connection given up)", *code));
      fail(std::format("no ready within {} s (or the pipe closed)", sc.ready_timeout_s));
    }
    std::fprintf(stderr, "fake-service: ready %s\n", ready->body.dump().c_str());
    expect(ipc::get_string(ready->body, "connector") == std::string("freerdp"), "ready.connector is not freerdp");
    const auto session = ipc::get_int(ready->body, "session");
    expect(session.has_value() && *session > 0, "ready without a session id");
    sid = static_cast<uint32_t>(*session);

    auto info = seat::wts::query_session(sid);
    dump_sessions();
    expect(info.has_value(), std::format("WTS does not know session {}", sid));
    expect(info->active(), std::format("session {} is {}, not active", sid, seat::wts::state_name(info->state)));
    expect(same_user(info->user, sc.user),
           std::format("session {} belongs to '{}', not '{}'", sid, win::narrow(info->user), sc.user));
    std::fprintf(stderr, "fake-service: session %u is active for %s (client %s)\n", sid, sc.user.c_str(),
                 win::narrow(info->client_name).c_str());

    // Heartbeats report the session.
    inbox.drop("heartbeat");
    auto hb = inbox.next_of("heartbeat", 3s);
    expect(hb.has_value(), "no heartbeat after ready");
    const json* metrics = ipc::get_object(hb->body, "metrics");
    expect(metrics && ipc::get_int(*metrics, "session") == static_cast<int64_t>(sid), "heartbeat without the session");
    expect(ipc::get_string(hb->body, "state") == std::string("logged_on"), "heartbeat state is not logged_on");

    // Display control: resize the session.
    send(*transport, ipc::make_request("set_mode", 10, {{"width", 1600}, {"height", 900}}));
    auto r = inbox.result(10, 10s);
    expect(r.has_value(), "no answer to set_mode");
    std::fprintf(stderr, "fake-service: set_mode -> %s\n", r->body.dump().c_str());
    expect(r->body.value("ok", false), "set_mode failed: " + r->body.dump());
    send(*transport, ipc::make_request("set_mode", 11, {{"width", 0}}));
    check_error_result(inbox.result(11, 5s), ipc::kErrBadRequest, "set_mode without a size");
    send(*transport, ipc::make_request("send_sas", 12));
    check_error_result(inbox.result(12, 5s), ipc::kErrUnsupported, "send_sas");

    // Give the session a moment, then make sure no session_lost was reported.
    std::this_thread::sleep_for(3s);
    auto lost = inbox.next_where(
        [](const ipc::Message& m) { return m.type == "event" && ipc::get_string(m.body, "name") == std::string("session_lost"); },
        0ms);
    expect(!lost.has_value(), "unexpected session_lost: " + (lost ? lost->body.dump() : std::string()));

    // Connection loss: the server drops the RDP connection (WTSDisconnectSession). The seat must report
    // session_lost, reconnect on its own and report session_restored with the same session.
    expect(seat::wts::disconnect(sid, &err), err);
    std::fprintf(stderr, "fake-service: disconnected session %u from the server side\n", sid);
    auto is_event = [](const char* name) {
      return [name](const ipc::Message& m) {
        return m.type == "event" && ipc::get_string(m.body, "name") == std::string(name);
      };
    };
    lost = inbox.next_where(is_event("session_lost"), 30s);
    expect(lost.has_value(), "no session_lost within 30 s of WTSDisconnectSession");
    std::fprintf(stderr, "fake-service: %s\n", lost->body.dump().c_str());
    auto restored = inbox.next_where(is_event("session_restored"), 120s);
    if (!restored) dump_sessions();
    expect(restored.has_value(), "no session_restored within 120 s");
    std::fprintf(stderr, "fake-service: %s\n", restored->body.dump().c_str());
    const json* rdata = ipc::get_object(restored->body, "data");
    expect(rdata && ipc::get_int(*rdata, "session") == static_cast<int64_t>(sid),
           "session_restored does not carry the same session: " + restored->body.dump());
    // The restored connection must hold: no further session_lost, session still active.
    std::this_thread::sleep_for(10s);
    lost = inbox.next_where(is_event("session_lost"), 0ms);
    expect(!lost.has_value(), "session_lost after session_restored: " + (lost ? lost->body.dump() : std::string()));
    info = seat::wts::query_session(sid);
    expect(info && info->active(), std::format("session {} is not active after the reconnect", sid));

    // shutdown: the seat logs the session off and exits.
    send(*transport, ipc::make_request("shutdown", 100));
    auto sr = inbox.result(100, 5s);
    expect(sr && sr->body.value("ok", false), "shutdown not acknowledged");
    auto code = wait_exit(child, 10s);
    expect(code.has_value(), "the seat did not exit within 10 s of shutdown");
    expect(*code == static_cast<DWORD>(kExitOk), std::format("seat exit code {} after shutdown", *code));
    expect(!inbox.malformed(), "the seat sent malformed lines");
  } catch (...) {
    TerminateJobObject(child.job.get(), 1);
    throw;
  }

  // The session must go away (logged off), not linger as disconnected.
  const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(sc.logoff_timeout_s);
  for (;;) {
    auto info = seat::wts::query_session(sid);
    const bool gone = !info || info->user.empty() || !same_user(info->user, sc.user);
    if (gone) break;
    if (std::chrono::steady_clock::now() > deadline) {
      dump_sessions();
      fail(std::format("session {} still exists ({}) {} s after shutdown", sid, seat::wts::state_name(info->state),
                       sc.logoff_timeout_s));
    }
    std::this_thread::sleep_for(1s);
  }
  std::fprintf(stderr, "fake-service: session %u logged off\n", sid);
  dump_sessions();
  std::fprintf(stderr, "fake-service: seat/rdp PASSED\n");
  return 0;
}

}  // namespace

int main(int argc, char** argv) {
  std::string err;
  auto args = Args::parse(argc, argv, {}, &err);
  if (!args || !args->get("child") || !args->get("role") || !args->get("scenario")) {
    std::fprintf(stderr,
                 "usage: sp_fake_service --child <exe> --role seat|stubworker --scenario shutdown|break\n"
                 "       sp_fake_service --child <seat exe> --role seat --scenario rdp --rdp-user <name>\n"
                 "                       [--rdp-host 127.0.0.2] [--rdp-port N] [--ready-timeout-s N]\n"
                 "       (rdp: the password is read from the SP_TEST_RDP_PASSWORD environment variable)\n");
    return 2;
  }
  const std::string role = *args->get("role");
  const std::string scenario = *args->get("scenario");
  if (role == "seat" && scenario == "rdp") {
    RdpScenario sc;
    sc.user = args->get_or("rdp-user", "");
    sc.host = args->get_or("rdp-host", sc.host);
    bool ok = true;
    if (auto p = args->get_int("rdp-port", &ok)) sc.port = static_cast<int>(*p);
    if (auto t = args->get_int("ready-timeout-s", &ok)) sc.ready_timeout_s = static_cast<int>(*t);
    wchar_t pw[512];
    const DWORD len = GetEnvironmentVariableW(L"SP_TEST_RDP_PASSWORD", pw, static_cast<DWORD>(std::size(pw)));
    if (len > 0 && len < std::size(pw)) sc.password = win::narrow(std::wstring_view(pw, len));
    SecureZeroMemory(pw, sizeof(pw));
    if (!ok || sc.user.empty() || sc.password.empty()) {
      std::fprintf(stderr, "sp_fake_service: rdp needs --rdp-user and SP_TEST_RDP_PASSWORD\n");
      return 2;
    }
    try {
      return run_rdp(*args->get("child"), sc);
    } catch (const Failure& f) {
      std::fprintf(stderr, "fake-service: seat/rdp FAILED: %s\n", f.what.c_str());
      return 1;
    }
  }
  if ((role != "seat" && role != "stubworker") || (scenario != "shutdown" && scenario != "break")) {
    std::fprintf(stderr, "sp_fake_service: bad --role or --scenario\n");
    return 2;
  }
  try {
    return run(*args->get("child"), role, scenario);
  } catch (const Failure& f) {
    std::fprintf(stderr, "fake-service: %s/%s FAILED: %s\n", role.c_str(), scenario.c_str(), f.what.c_str());
    return 1;
  }
}
