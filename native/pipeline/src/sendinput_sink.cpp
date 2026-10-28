// SPDX-License-Identifier: MIT
#include "splitplay/pipeline/sendinput_sink.hpp"

#include "splitplay/common/input_ring.hpp"
#include "splitplay/common/log.hpp"
#include "splitplay/common/qpc.hpp"

namespace splitplay::pipeline {

bool SendInputSink::inject(const SpInputEvent& event) {
  const uint64_t n = ++injected_;
  // Log the first events and then every 1000th, so a flood of mouse motion does not flood the log.
  if (n <= 16 || n % 1000 == 0) {
    log_debug("sendinput-stub: would inject #{} {} a={} b={} seq={}", n, input::input_type_name(event.type), event.a,
              event.b, event.seq);
  }
  if (event.type == SP_IN_RELEASE_ALL) release_all();
  if (tracer_) tracer_->stamp_now(event.seq, SP_STAGE_INPUT_INJECT);
  return true;
}

void SendInputSink::release_all() {
  ++releases_;
  log_debug("sendinput-stub: release all keys and buttons");
}

}  // namespace splitplay::pipeline
