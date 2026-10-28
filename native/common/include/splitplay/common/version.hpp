// SPDX-License-Identifier: MIT
// Build version and protocol constants shared by all native components.
#pragma once

#ifndef SPLITPLAY_VERSION
#define SPLITPLAY_VERSION "0.0.0"
#endif

namespace splitplay {

inline constexpr const char* kVersion = SPLITPLAY_VERSION;

// Control IPC protocol version (docs/contracts/ipc.md).
inline constexpr int kIpcProtocolVersion = 1;

// Process exit codes used by splitplay-seat and splitplay-stubworker.
enum ExitCode : int {
  kExitOk = 0,              // shutdown requested by the service, or a successful self-test
  kExitFailure = 1,         // generic runtime failure, self-test failed
  kExitUsage = 2,           // bad command line
  kExitConnectFailed = 3,   // could not connect to the pipe or the handshake failed
  kExitPipeBroken = 3,      // the service went away (the child never runs unsupervised); ipc.md "pipe lost" = 3
  kExitRdpGaveUp = 4,       // seat only: the RDP connection was lost (or never came up) and reconnecting gave up
};

}  // namespace splitplay
