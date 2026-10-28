// SPDX-License-Identifier: MIT
// splitplay-seat: one process per desk in session 0. Holds the desk's RDP connection, owns the input ring that the
// worker writes to, and talks to the service over the control pipe.
#pragma once

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <deque>
#include <memory>
#include <mutex>
#include <string>
#include <thread>

#include "rdp_connector.hpp"
#include "splitplay/common/ipc_client.hpp"
#include "splitplay/common/object_names.hpp"
#include "splitplay/common/shared_memory.hpp"
#include "splitplay/common/trace.hpp"

namespace splitplay::seat {

struct SeatOptions {
  std::string pipe;
  std::string desk;
  ObjectNamespace ns = ObjectNamespace::global;
  std::chrono::milliseconds connect_timeout{10000};
  // How long `shutdown` waits for the logged-off session's connection to end (the IPC contract allows 5 s in all).
  std::chrono::milliseconds logoff_wait{3000};
};

class SeatApp {
 public:
  SeatApp(SeatOptions options, std::unique_ptr<IRdpConnector> connector);
  ~SeatApp();

  // Runs until the service asks for shutdown (kExitOk), the pipe breaks (kExitPipeBroken) or the RDP connector gives
  // up (kExitRdpGaveUp).
  int run();

 private:
  struct StateEvent {
    RdpState state;
    std::string detail;
  };

  void input_loop();
  ipc::HeartbeatInfo heartbeat();
  std::optional<ipc::Result> on_request(const ipc::Message& request);
  ipc::Result on_profile(const ipc::json& profile, bool initial);
  void on_welcome(ipc::json& welcome);
  // Handles one connector state change on the main thread. Returns an exit code to stop with, or -1.
  int on_state(const StateEvent& event);

  SeatOptions options_;
  std::unique_ptr<IRdpConnector> connector_;
  std::unique_ptr<ipc::Client> client_;
  std::unique_ptr<shm::InputRingMapping> input_ring_;
  trace::Tracer tracer_;
  RdpConnectParams params_;
  bool have_rdp_ = false;
  uint32_t profile_width_ = 0;
  uint32_t profile_height_ = 0;

  std::mutex events_mutex_;
  std::condition_variable events_cv_;
  std::deque<StateEvent> events_;
  bool ready_sent_ = false;
  bool session_lost_ = false;

  win::UniqueHandle stop_event_;
  std::thread input_thread_;
  std::atomic<uint64_t> input_events_{0};
  std::atomic<uint64_t> input_seq_gaps_{0};
  std::atomic<uint64_t> reconnects_{0};
};

}  // namespace splitplay::seat
