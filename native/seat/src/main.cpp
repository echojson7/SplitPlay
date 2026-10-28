// SPDX-License-Identifier: MIT
// splitplay-seat.exe --pipe \\.\pipe\splitplay\seat-<desk> --desk <desk> [--namespace global|local]
//                    [--connect-timeout-ms N] [--log-level trace|debug|info|warn|error]
#include <cstdio>
#include <memory>

#include "null_rdp_connector.hpp"
#include "seat_app.hpp"
#include "splitplay/common/args.hpp"
#include "splitplay/common/log.hpp"
#include "splitplay/common/trace.hpp"
#include "splitplay/common/version.hpp"

using namespace splitplay;

static void usage() {
  std::fprintf(stderr,
               "usage: splitplay-seat --pipe <\\\\.\\pipe\\splitplay\\seat-DESK> --desk <DESK>\n"
               "                      [--namespace global|local] [--connect-timeout-ms N]\n"
               "                      [--log-level trace|debug|info|warn|error] [--version]\n");
}

int main(int argc, char** argv) {
  log_set_component("seat");
  std::string err;
  auto args = Args::parse(argc, argv, {"version", "help"}, &err);
  if (!args) {
    std::fprintf(stderr, "splitplay-seat: %s\n", err.c_str());
    usage();
    return kExitUsage;
  }
  if (args->has("version")) {
    std::printf("splitplay-seat %s\n", kVersion);
    return kExitOk;
  }
  if (args->has("help")) {
    usage();
    return kExitOk;
  }
  for (const auto& u : args->unknown({"pipe", "desk", "namespace", "connect-timeout-ms", "log-level"})) {
    std::fprintf(stderr, "splitplay-seat: unknown option --%s\n", u.c_str());
    usage();
    return kExitUsage;
  }

  seat::SeatOptions options;
  options.pipe = args->get_or("pipe", "");
  options.desk = args->get_or("desk", "");
  if (options.pipe.empty() || !is_valid_desk_id(options.desk)) {
    std::fprintf(stderr, "splitplay-seat: --pipe and a valid --desk ([a-z0-9-]{1,32}) are required\n");
    usage();
    return kExitUsage;
  }
  if (auto ns = args->get("namespace"); ns && !parse_object_namespace(*ns, options.ns)) {
    std::fprintf(stderr, "splitplay-seat: --namespace must be global or local\n");
    return kExitUsage;
  }
  bool ok = true;
  if (auto t = args->get_int("connect-timeout-ms", &ok); t && *t > 0) options.connect_timeout = std::chrono::milliseconds(*t);
  if (!ok) {
    std::fprintf(stderr, "splitplay-seat: --connect-timeout-ms must be an integer\n");
    return kExitUsage;
  }
  if (auto level = args->get("log-level")) {
    LogLevel l;
    if (!parse_log_level(*level, l)) {
      std::fprintf(stderr, "splitplay-seat: bad --log-level\n");
      return kExitUsage;
    }
    log_set_min_level(l);
  }

  trace::register_provider();
  int rc;
  {
    seat::SeatApp app(options, std::make_unique<seat::NullRdpConnector>());
    rc = app.run();
  }
  trace::unregister_provider();
  return rc;
}
