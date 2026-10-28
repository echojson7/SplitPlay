// SPDX-License-Identifier: MIT
// QueryPerformanceCounter helpers. All pipeline timestamps (capture_ring.h, input_ring.h, trace.h) are raw QPC ticks.
// On non-Windows hosts (portable unit tests) a steady clock with 1 ns ticks stands in for QPC.
#pragma once

#include <cstdint>

namespace splitplay {

uint64_t qpc_now() noexcept;
uint64_t qpc_frequency() noexcept;  // ticks per second, constant for the lifetime of the process

inline double qpc_to_us(uint64_t ticks) noexcept {
  return static_cast<double>(ticks) * 1'000'000.0 / static_cast<double>(qpc_frequency());
}
inline double qpc_to_ms(uint64_t ticks) noexcept { return qpc_to_us(ticks) / 1000.0; }
inline uint64_t qpc_from_us(double us) noexcept {
  return static_cast<uint64_t>(us * static_cast<double>(qpc_frequency()) / 1'000'000.0);
}
// Signed difference b - a in microseconds.
inline double qpc_delta_us(uint64_t a, uint64_t b) noexcept {
  return b >= a ? qpc_to_us(b - a) : -qpc_to_us(a - b);
}

}  // namespace splitplay
