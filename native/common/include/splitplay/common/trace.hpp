// SPDX-License-Identifier: MIT
// Frame tracing (splitplay/trace.h): ETW TraceLogging provider "SplitPlay" plus an optional in-process recorder that
// keeps per-frame stage stamps for latency statistics (used by the stub worker self-test).
#pragma once

#include <cstddef>
#include <cstdint>
#include <mutex>
#include <string>
#include <string_view>
#include <vector>

#include "splitplay/trace.h"

namespace splitplay::trace {

// Registers / unregisters the ETW provider. Safe to call more than once; no-op where ETW is unavailable.
void register_provider() noexcept;
void unregister_provider() noexcept;
// True if the ETW provider is registered and an ETW session currently enables it.
bool provider_enabled() noexcept;
// Emits one "Frame" event (FrameId, Qpc, Stage, DeskHash).
void emit(const SpTraceRecord& record) noexcept;

const char* stage_name(uint32_t stage) noexcept;

struct IntervalStats {
  std::string name;   // "IDD_PUBLISH->WORKER_WAKE"
  size_t count = 0;
  double min_us = 0, p50_us = 0, p90_us = 0, p99_us = 0, max_us = 0, mean_us = 0;
};

// Keeps the last `capacity` frames' stamps. Thread-safe.
class Recorder {
 public:
  explicit Recorder(size_t capacity = 8192);

  void stamp(uint64_t frame_id, uint32_t stage, uint64_t qpc);
  // Statistics of (to - from) over every recorded frame that has both stamps.
  IntervalStats interval(uint32_t from, uint32_t to) const;
  // The standard frame pipeline intervals, PRESENT -> ... -> LAST_PACKET plus end-to-end.
  std::vector<IntervalStats> frame_pipeline_summary() const;
  size_t frames_with_stage(uint32_t stage) const;
  void clear();

 private:
  static constexpr size_t kStages = 12;
  static size_t stage_index(uint32_t stage) noexcept;
  struct Entry {
    uint64_t frame_id = 0;
    uint64_t qpc[kStages] = {};
  };
  mutable std::mutex mutex_;
  std::vector<Entry> entries_;
};

// Stamps one desk's frames: emits ETW and, if set, feeds a Recorder.
class Tracer {
 public:
  explicit Tracer(std::string_view desk_id, Recorder* recorder = nullptr) noexcept
      : desk_hash_(sp_desk_hash(std::string(desk_id).c_str())), recorder_(recorder) {}

  void stamp(uint64_t frame_id, SpTraceStage stage, uint64_t qpc) const;
  void stamp_now(uint64_t frame_id, SpTraceStage stage) const;
  uint32_t desk_hash() const noexcept { return desk_hash_; }
  void set_recorder(Recorder* recorder) noexcept { recorder_ = recorder; }

 private:
  uint32_t desk_hash_;
  Recorder* recorder_;
};

std::string format_stats_table(const std::vector<IntervalStats>& stats);

}  // namespace splitplay::trace
