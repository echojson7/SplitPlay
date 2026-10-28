// SPDX-License-Identifier: MIT
// ETW TraceLogging provider "SplitPlay" {6B2E3F5A-9C41-4D7B-A1E2-53504C415931} (splitplay/trace.h).
//
// Capture with e.g.:
//   wpr / xperf, or: tracelog -start sp -guid #6B2E3F5A-9C41-4D7B-A1E2-53504C415931 -f sp.etl
#include "splitplay/common/trace.hpp"

#if defined(_WIN32) && __has_include(<TraceLoggingProvider.h>)
#define SPLITPLAY_HAVE_TRACELOGGING 1
#include <windows.h>
#include <TraceLoggingProvider.h>
#include <atomic>
#include <mutex>
#endif

#ifdef SPLITPLAY_HAVE_TRACELOGGING
// Defined at global scope: the macro emits linkage specifications and section annotations.
TRACELOGGING_DEFINE_PROVIDER(g_splitplay_provider, SP_TRACE_PROVIDER_NAME,
                             (0x6b2e3f5a, 0x9c41, 0x4d7b, 0xa1, 0xe2, 0x53, 0x50, 0x4c, 0x41, 0x59, 0x31));
#endif

namespace splitplay::trace {

#ifdef SPLITPLAY_HAVE_TRACELOGGING

namespace {
std::mutex g_register_mutex;
std::atomic<bool> g_registered{false};
}  // namespace

void register_provider() noexcept {
  std::lock_guard lock(g_register_mutex);
  if (g_registered.load()) return;
  if (SUCCEEDED(TraceLoggingRegister(g_splitplay_provider))) g_registered.store(true);
}

void unregister_provider() noexcept {
  std::lock_guard lock(g_register_mutex);
  if (!g_registered.load()) return;
  g_registered.store(false);
  TraceLoggingUnregister(g_splitplay_provider);
}

bool provider_enabled() noexcept {
  return g_registered.load(std::memory_order_relaxed) && TraceLoggingProviderEnabled(g_splitplay_provider, 0, 0);
}

void emit(const SpTraceRecord& record) noexcept {
  if (!g_registered.load(std::memory_order_relaxed)) return;
  TraceLoggingWrite(g_splitplay_provider, "Frame",
                    TraceLoggingUInt64(record.frame_id, "FrameId"),
                    TraceLoggingUInt64(record.qpc, "Qpc"),
                    TraceLoggingUInt32(record.stage, "Stage"),
                    TraceLoggingUInt32(record.desk_hash, "DeskHash"));
}

#else

// Portable builds (Linux unit tests, MinGW compile checks without TraceLoggingProvider.h): tracing is local only.
void register_provider() noexcept {}
void unregister_provider() noexcept {}
bool provider_enabled() noexcept { return false; }
void emit(const SpTraceRecord&) noexcept {}

#endif

}  // namespace splitplay::trace
