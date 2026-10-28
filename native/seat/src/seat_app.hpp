// SPDX-License-Identifier: MIT
// splitplay-seat: one process per desk in session 0. Holds the desk's RDP connection (Stage 3), owns the input ring
// that the worker writes to, and talks to the service over the control pipe.
#pragma once

#include <atomic>
#include <chrono>
#include <memory>
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
};

class SeatApp {
 public:
  SeatApp(SeatOptions options, std::unique_ptr<IRdpConnector> connector);
  ~SeatApp();

  // Runs until the service asks for shutdown (returns kExitOk) or the pipe breaks (kExitPipeBroken).
  int run();

 private:
  void input_loop();
  ipc::HeartbeatInfo heartbeat();
  std::optional<ipc::Result> on_request(const ipc::Message& request);
  ipc::Result on_profile(const ipc::json& profile, bool initial);

  SeatOptions options_;
  std::unique_ptr<IRdpConnector> connector_;
  std::unique_ptr<ipc::Client> client_;
  std::unique_ptr<shm::InputRingMapping> input_ring_;
  trace::Tracer tracer_;
  RdpConnectParams params_;

  win::UniqueHandle stop_event_;
  std::thread input_thread_;
  std::atomic<uint64_t> input_events_{0};
  std::atomic<uint64_t> input_seq_gaps_{0};
};

}  // namespace splitplay::seat
