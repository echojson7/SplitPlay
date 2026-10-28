// SPDX-License-Identifier: MIT
// splitplay-stubworker.exe: stands in for the desk worker until the Sunshine fork is wired up.
//
//   Under the service:  --pipe \\.\pipe\splitplay\worker-<desk> --desk <desk> [--namespace global|local]
//                       [--adapter auto|warp|default] [--pipeline synthetic|none]
//   Standalone:         --self-test N [--fps F] [--width W] [--height H] [--adapter auto|warp|default]
//                       [--desk D] [--namespace local|global] [--no-regen]
//
// Both modes run the stub pipeline: synthetic producer -> capture ring -> consumer -> null encoder -> UDP loopback.
// The self-test exits 0 if every frame arrived intact and prints latency statistics from the trace stamps.
#include <windows.h>

#include <algorithm>
#include <cstdio>
#include <format>
#include <memory>

#include "splitplay/common/args.hpp"
#include "splitplay/common/ipc_client.hpp"
#include "splitplay/common/log.hpp"
#include "splitplay/common/pipe_transport.hpp"
#include "splitplay/common/trace.hpp"
#include "splitplay/common/version.hpp"
#include "stub_pipeline.hpp"

using namespace splitplay;
using namespace std::chrono_literals;

namespace {

void usage() {
  std::fprintf(stderr,
               "usage: splitplay-stubworker --pipe <PIPE> --desk <DESK> [--namespace global|local]\n"
               "                            [--adapter auto|warp|default] [--pipeline synthetic|none]\n"
               "       splitplay-stubworker --self-test <FRAMES> [--fps F] [--width W] [--height H]\n"
               "                            [--adapter auto|warp|default] [--desk D] [--namespace local|global]\n"
               "                            [--no-regen]\n"
               "common: [--log-level trace|debug|info|warn|error] [--version]\n");
}

bool parse_common(const Args& args, stubworker::PipelineOptions& p) {
  bool ok = true;
  if (auto v = args.get("adapter"); v && !pipeline::parse_adapter_choice(*v, p.adapter)) {
    std::fprintf(stderr, "splitplay-stubworker: --adapter must be auto, warp or default\n");
    return false;
  }
  if (auto v = args.get("namespace"); v && !parse_object_namespace(*v, p.ns)) {
    std::fprintf(stderr, "splitplay-stubworker: --namespace must be global or local\n");
    return false;
  }
  if (auto v = args.get_int("fps", &ok); v && *v > 0 && *v <= 1000) p.fps = static_cast<double>(*v);
  if (auto v = args.get_int("width", &ok); v && *v > 0) p.width = static_cast<uint32_t>(*v);
  if (auto v = args.get_int("height", &ok); v && *v > 0) p.height = static_cast<uint32_t>(*v);
  if (!ok) std::fprintf(stderr, "splitplay-stubworker: numeric option expected\n");
  return ok;
}

void apply_profile(const ipc::json& profile, stubworker::PipelineOptions& p) {
  if (auto v = ipc::get_number(profile, "fps"); v && *v > 0 && *v <= 1000) p.fps = *v;
  if (auto v = ipc::get_int(profile, "width"); v && *v > 0 && *v <= 8192) p.width = static_cast<uint32_t>(*v);
  if (auto v = ipc::get_int(profile, "height"); v && *v > 0 && *v <= 8192) p.height = static_cast<uint32_t>(*v);
}

// ---- standalone self-test --------------------------------------------------------------------------

int run_self_test(uint64_t frames, stubworker::PipelineOptions options, bool regenerate) {
  if (options.desk.empty()) options.desk = std::format("selftest-{}", GetCurrentProcessId());
  if (regenerate) options.regenerate_at_frame = std::max<uint64_t>(2, frames / 2);
  log_info("self-test: {} frames, {}x{} @ {} fps, adapter {}, desk {} ({})", frames, options.width, options.height,
           options.fps, pipeline::to_string(options.adapter), options.desk, to_string(options.ns));

  trace::Recorder recorder(16384);
  std::string err;
  auto pipe = stubworker::StubPipeline::create(options, &recorder, &err);
  if (!pipe) {
    log_error("self-test: cannot build the pipeline: {}", err);
    return kExitFailure;
  }
  pipe->start();
  const auto timeout = std::chrono::milliseconds(static_cast<int64_t>(static_cast<double>(frames) / options.fps * 5000.0)) + 30s;
  const bool reached = pipe->wait_for_sent(frames, timeout);
  pipe->stop();
  const auto s = pipe->stats();

  std::printf("\nSplitPlay stub pipeline self-test\n");
  std::printf("adapter: %s, fence wait: %s, %ux%u @ %.0f fps\n", pipe->adapter_description().c_str(),
              s.gpu_fence_wait ? "GPU" : "CPU (producer side)", options.width, options.height, options.fps);
  std::printf("frames: produced %llu, producer dropped %llu, acquired %llu, skipped %llu, encoded %llu, sent %llu\n",
              static_cast<unsigned long long>(s.produced), static_cast<unsigned long long>(s.producer_dropped),
              static_cast<unsigned long long>(s.acquired), static_cast<unsigned long long>(s.skipped),
              static_cast<unsigned long long>(s.encoded), static_cast<unsigned long long>(s.sent));
  std::printf("received %llu, verified %llu, corrupt %llu, out of order %llu, incomplete %llu, generation changes %llu\n",
              static_cast<unsigned long long>(s.received), static_cast<unsigned long long>(s.verified),
              static_cast<unsigned long long>(s.corrupt), static_cast<unsigned long long>(s.out_of_order),
              static_cast<unsigned long long>(s.receiver.frames_incomplete),
              static_cast<unsigned long long>(s.generation_changes));
  std::printf("\n%s\n", trace::format_stats_table(recorder.frame_pipeline_summary()).c_str());
  std::fflush(stdout);

  std::vector<std::string> failures;
  if (!reached) failures.push_back(std::format("only {} of {} frames sent before the timeout", s.sent, frames));
  if (s.producer_failed) failures.push_back("the producer failed");
  if (s.encode_errors) failures.push_back(std::format("{} encode errors", s.encode_errors));
  if (s.corrupt) failures.push_back(std::format("{} corrupt frames", s.corrupt));
  if (s.out_of_order) failures.push_back(std::format("{} frames out of order", s.out_of_order));
  const uint64_t allowed_loss = std::max<uint64_t>(2, s.sent / 100);
  if (s.verified + allowed_loss < s.sent)
    failures.push_back(std::format("{} of {} sent frames verified", s.verified, s.sent));
  if (regenerate && s.generation_changes == 0) failures.push_back("the texture generation change was not observed");
  if (recorder.interval(SP_STAGE_PRESENT, SP_STAGE_LAST_PACKET).count == 0)
    failures.push_back("no complete trace stamps recorded");

  if (!failures.empty()) {
    for (const auto& f : failures) log_error("self-test FAILED: {}", f);
    return kExitFailure;
  }
  log_info("self-test passed");
  return kExitOk;
}

// ---- under the service -----------------------------------------------------------------------------

int run_service_mode(const std::string& pipe_name, stubworker::PipelineOptions options, bool with_pipeline) {
  log_info("splitplay-stubworker {} starting for desk {} (pipe {})", kVersion, options.desk, pipe_name);
  std::string err;
  auto transport = ipc::PipeTransport::connect(pipe_name, 10s, &err);
  if (!transport) {
    log_error("cannot connect to the service: {}", err);
    return kExitConnectFailed;
  }

  std::unique_ptr<stubworker::StubPipeline> pipe;
  std::mutex pipe_mutex;
  stubworker::PipelineOptions initial = options;

  ipc::Client::Handlers handlers;
  handlers.on_profile = [&](const ipc::json& profile, bool is_initial) {
    if (is_initial) {
      apply_profile(profile, initial);
      return ipc::Result::success();
    }
    std::lock_guard lock(pipe_mutex);
    stubworker::PipelineOptions changed = initial;
    apply_profile(profile, changed);
    if (pipe && changed.fps != initial.fps) pipe->set_fps(changed.fps);
    if (changed.width != initial.width || changed.height != initial.height)
      log_info("profile: frame size changes apply on restart");
    initial.fps = changed.fps;
    log_info("profile updated: fps {}", changed.fps);
    return ipc::Result::success();
  };
  handlers.on_request = [](const ipc::Message&) -> std::optional<ipc::Result> {
    return std::nullopt;  // pair / list_clients / unpair need the real worker; send_sas is seat-only
  };
  handlers.on_heartbeat = [&] {
    ipc::HeartbeatInfo hb;
    std::lock_guard lock(pipe_mutex);
    if (pipe) {
      hb.state = "streaming";
      hb.metrics = pipe->heartbeat_metrics();
    } else {
      hb.state = "idle";
      hb.metrics = {{"fps", 0}, {"bitrate_kbps", 0}, {"clients", 0}};
    }
    return hb;
  };
  ipc::Client client(std::move(transport), std::move(handlers));

  ipc::Hello hello{"stubworker", options.desk, GetCurrentProcessId(), kVersion, win::current_session_id()};
  if (!client.handshake(hello, &err)) {
    log_error("handshake with the service failed: {}", err);
    return kExitConnectFailed;
  }
  log_set_sink([&client](LogLevel level, std::string_view message) { client.send_log(to_string(level), message); });

  ipc::json ready = {{"ports", {{"base", 0}}}};
  if (with_pipeline) {
    std::unique_ptr<stubworker::StubPipeline> created;
    {
      std::lock_guard lock(pipe_mutex);
      created = stubworker::StubPipeline::create(initial, nullptr, &err);
    }
    if (!created) {
      log_error("cannot build the stub pipeline: {}", err);
      log_set_sink({});
      client.stop();
      return kExitFailure;
    }
    created->start();
    ready["stub"] = {{"udp_port", created->receiver_port()}, {"adapter", created->adapter_description()}};
    std::lock_guard lock(pipe_mutex);
    pipe = std::move(created);
  }
  client.send_ready(ready);
  log_info("stubworker ready ({})", with_pipeline ? "synthetic pipeline running" : "no pipeline");

  const ipc::ExitReason reason = client.wait();
  log_set_sink({});
  {
    std::unique_ptr<stubworker::StubPipeline> stopping;
    {
      std::lock_guard lock(pipe_mutex);
      stopping = std::move(pipe);
    }
    if (stopping) stopping->stop();
  }
  client.stop();
  log_info("stubworker exiting: {}", ipc::to_string(reason));
  return reason == ipc::ExitReason::shutdown ? kExitOk : kExitPipeBroken;
}

}  // namespace

int main(int argc, char** argv) {
  log_set_component("stubworker");
  std::string err;
  auto args = Args::parse(argc, argv, {"version", "help", "no-regen"}, &err);
  if (!args) {
    std::fprintf(stderr, "splitplay-stubworker: %s\n", err.c_str());
    usage();
    return kExitUsage;
  }
  if (args->has("version")) {
    std::printf("splitplay-stubworker %s\n", kVersion);
    return kExitOk;
  }
  if (args->has("help")) {
    usage();
    return kExitOk;
  }
  for (const auto& u : args->unknown({"pipe", "desk", "namespace", "adapter", "pipeline", "self-test", "fps", "width",
                                      "height", "no-regen", "log-level"})) {
    std::fprintf(stderr, "splitplay-stubworker: unknown option --%s\n", u.c_str());
    usage();
    return kExitUsage;
  }
  if (auto level = args->get("log-level")) {
    LogLevel l;
    if (!parse_log_level(*level, l)) {
      std::fprintf(stderr, "splitplay-stubworker: bad --log-level\n");
      return kExitUsage;
    }
    log_set_min_level(l);
  }

  stubworker::PipelineOptions options;
  options.desk = args->get_or("desk", "");
  if (!options.desk.empty() && !is_valid_desk_id(options.desk)) {
    std::fprintf(stderr, "splitplay-stubworker: --desk must match [a-z0-9-]{1,32}\n");
    return kExitUsage;
  }

  trace::register_provider();
  int rc = kExitUsage;
  if (args->has("self-test")) {
    bool ok = true;
    auto frames = args->get_int("self-test", &ok);
    options.ns = ObjectNamespace::local;
    if (!ok || !frames || *frames <= 0 || !parse_common(*args, options)) {
      usage();
    } else {
      rc = run_self_test(static_cast<uint64_t>(*frames), options, !args->has("no-regen"));
    }
  } else if (auto pipe = args->get("pipe")) {
    options.ns = ObjectNamespace::global;
    options.fps = 60;
    options.width = 1280;
    options.height = 720;
    const std::string mode = args->get_or("pipeline", "synthetic");
    if (options.desk.empty() || (mode != "synthetic" && mode != "none") || !parse_common(*args, options)) {
      std::fprintf(stderr, "splitplay-stubworker: --pipe needs --desk; --pipeline is synthetic or none\n");
      usage();
    } else {
      rc = run_service_mode(*pipe, options, mode == "synthetic");
    }
  } else {
    usage();
  }
  trace::unregister_provider();
  return rc;
}
