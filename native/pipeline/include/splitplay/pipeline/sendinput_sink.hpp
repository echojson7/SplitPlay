// SPDX-License-Identifier: MIT
// Stub of the MVP input sink. Stage 3 translates SpInputEvent into SendInput calls (scancodes, relative mouse) with
// the input-desktop fallback; in Stage 2 it only logs and counts what it would inject.
#pragma once

#include <atomic>
#include <cstdint>

#include "splitplay/common/trace.hpp"
#include "splitplay/pipeline/interfaces.hpp"

namespace splitplay::pipeline {

class SendInputSink final : public IInputSink {
 public:
  explicit SendInputSink(const trace::Tracer* tracer = nullptr) : tracer_(tracer) {}

  bool inject(const SpInputEvent& event) override;
  void release_all() override;
  bool supports_relative_mouse() const override { return true; }
  std::string_view name() const override { return "sendinput-stub"; }

  uint64_t injected() const noexcept { return injected_.load(); }
  uint64_t releases() const noexcept { return releases_.load(); }

 private:
  const trace::Tracer* tracer_;
  std::atomic<uint64_t> injected_{0};
  std::atomic<uint64_t> releases_{0};
};

}  // namespace splitplay::pipeline
