// SPDX-License-Identifier: MIT
#include "splitplay/common/trace.hpp"

#include <algorithm>
#include <format>

#include "splitplay/common/qpc.hpp"

namespace splitplay::trace {

const char* stage_name(uint32_t stage) noexcept {
  switch (stage) {
    case SP_STAGE_PRESENT: return "PRESENT";
    case SP_STAGE_IDD_ACQUIRE: return "IDD_ACQUIRE";
    case SP_STAGE_IDD_PUBLISH: return "IDD_PUBLISH";
    case SP_STAGE_WORKER_WAKE: return "WORKER_WAKE";
    case SP_STAGE_CONVERT_DONE: return "CONVERT_DONE";
    case SP_STAGE_ENCODE_DONE: return "ENCODE_DONE";
    case SP_STAGE_FIRST_PACKET: return "FIRST_PACKET";
    case SP_STAGE_LAST_PACKET: return "LAST_PACKET";
    case SP_STAGE_INPUT_RECV: return "INPUT_RECV";
    case SP_STAGE_INPUT_INJECT: return "INPUT_INJECT";
    default: return "UNKNOWN";
  }
}

Recorder::Recorder(size_t capacity) : entries_(capacity == 0 ? 1 : capacity) {}

size_t Recorder::stage_index(uint32_t stage) noexcept {
  if (stage >= SP_STAGE_PRESENT && stage <= SP_STAGE_LAST_PACKET) return stage;  // 1..8
  if (stage == SP_STAGE_INPUT_RECV) return 9;
  if (stage == SP_STAGE_INPUT_INJECT) return 10;
  return 0;  // unknown stages are folded into slot 0 and never reported
}

void Recorder::stamp(uint64_t frame_id, uint32_t stage, uint64_t qpc) {
  std::lock_guard lock(mutex_);
  Entry& e = entries_[frame_id % entries_.size()];
  if (e.frame_id != frame_id) e = Entry{frame_id, {}};
  e.qpc[stage_index(stage)] = qpc;
}

IntervalStats Recorder::interval(uint32_t from, uint32_t to) const {
  IntervalStats out;
  out.name = std::format("{}->{}", stage_name(from), stage_name(to));
  std::vector<double> values;
  {
    std::lock_guard lock(mutex_);
    const size_t a = stage_index(from), b = stage_index(to);
    for (const Entry& e : entries_) {
      if (e.frame_id == 0 || e.qpc[a] == 0 || e.qpc[b] == 0) continue;
      values.push_back(qpc_delta_us(e.qpc[a], e.qpc[b]));
    }
  }
  out.count = values.size();
  if (values.empty()) return out;
  std::sort(values.begin(), values.end());
  auto pct = [&](double p) {
    const size_t idx = static_cast<size_t>(p * static_cast<double>(values.size() - 1) + 0.5);
    return values[std::min(idx, values.size() - 1)];
  };
  double sum = 0;
  for (double v : values) sum += v;
  out.min_us = values.front();
  out.max_us = values.back();
  out.p50_us = pct(0.50);
  out.p90_us = pct(0.90);
  out.p99_us = pct(0.99);
  out.mean_us = sum / static_cast<double>(values.size());
  return out;
}

std::vector<IntervalStats> Recorder::frame_pipeline_summary() const {
  static constexpr uint32_t chain[] = {SP_STAGE_PRESENT,      SP_STAGE_IDD_ACQUIRE, SP_STAGE_IDD_PUBLISH,
                                       SP_STAGE_WORKER_WAKE,  SP_STAGE_CONVERT_DONE, SP_STAGE_ENCODE_DONE,
                                       SP_STAGE_FIRST_PACKET, SP_STAGE_LAST_PACKET};
  std::vector<IntervalStats> out;
  for (size_t i = 0; i + 1 < std::size(chain); ++i) out.push_back(interval(chain[i], chain[i + 1]));
  out.push_back(interval(SP_STAGE_IDD_PUBLISH, SP_STAGE_LAST_PACKET));
  out.push_back(interval(SP_STAGE_PRESENT, SP_STAGE_LAST_PACKET));
  return out;
}

size_t Recorder::frames_with_stage(uint32_t stage) const {
  std::lock_guard lock(mutex_);
  const size_t idx = stage_index(stage);
  return static_cast<size_t>(
      std::count_if(entries_.begin(), entries_.end(), [&](const Entry& e) { return e.frame_id != 0 && e.qpc[idx] != 0; }));
}

void Recorder::clear() {
  std::lock_guard lock(mutex_);
  std::fill(entries_.begin(), entries_.end(), Entry{});
}

void Tracer::stamp(uint64_t frame_id, SpTraceStage stage, uint64_t qpc) const {
  SpTraceRecord record{frame_id, qpc, static_cast<uint32_t>(stage), desk_hash_};
  emit(record);
  if (recorder_) recorder_->stamp(frame_id, record.stage, qpc);
}

void Tracer::stamp_now(uint64_t frame_id, SpTraceStage stage) const { stamp(frame_id, stage, qpc_now()); }

std::string format_stats_table(const std::vector<IntervalStats>& stats) {
  std::string out = std::format("{:<30} {:>7} {:>10} {:>10} {:>10} {:>10} {:>10}\n", "interval (us)", "count", "min",
                                "p50", "p90", "p99", "max");
  for (const auto& s : stats) {
    out += std::format("{:<30} {:>7} {:>10.1f} {:>10.1f} {:>10.1f} {:>10.1f} {:>10.1f}\n", s.name, s.count, s.min_us,
                       s.p50_us, s.p90_us, s.p99_us, s.max_us);
  }
  return out;
}

}  // namespace splitplay::trace
