// SPDX-License-Identifier: MIT
#include "splitplay/common/qpc.hpp"

#ifdef _WIN32
#include <windows.h>
#else
#include <chrono>
#endif

namespace splitplay {

#ifdef _WIN32

uint64_t qpc_now() noexcept {
  LARGE_INTEGER v;
  QueryPerformanceCounter(&v);
  return static_cast<uint64_t>(v.QuadPart);
}

uint64_t qpc_frequency() noexcept {
  static const uint64_t freq = [] {
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    return static_cast<uint64_t>(f.QuadPart);
  }();
  return freq;
}

#else

uint64_t qpc_now() noexcept {
  return static_cast<uint64_t>(
      std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch())
          .count());
}

uint64_t qpc_frequency() noexcept { return 1'000'000'000ull; }

#endif

}  // namespace splitplay
