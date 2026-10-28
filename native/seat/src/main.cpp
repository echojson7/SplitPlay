// SPDX-License-Identifier: MIT
// splitplay-seat.exe --pipe \\.\pipe\splitplay\seat-<desk> --desk <desk> [--namespace global|local]
//                    [--connect-timeout-ms N] [--log-level trace|debug|info|warn|error] [--connector freerdp|null]
//
// The RDP credentials arrive in `welcome.rdp` over the pipe (never on the command line). --connector null (the
// default when built without FreeRDP) runs without an RDP stack, for IPC tests.
#include <cstdio>
#include <memory>

#ifdef SPLITPLAY_HAVE_FREERDP
#include "freerdp_connector.hpp"
#endif
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
               "                      [--log-level trace|debug|info|warn|error] [--connector freerdp|null]\n"
               "                      [--version]\n");
}

#ifdef SPLITPLAY_HAVE_FREERDP
static constexpr const char* kDefaultConnector = "freerdp";
#else
static constexpr const char* kDefaultConnector = "null";
#endif

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
  for (const auto& u : args->unknown({"pipe", "desk", "namespace", "connect-timeout-ms", "log-level", "connector"})) {
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

  const std::string connector_name = args->get_or("connector", kDefaultConnector);
  std::unique_ptr<seat::IRdpConnector> connector;
  if (connector_name == "null") {
    connector = std::make_unique<seat::NullRdpConnector>();
#ifdef SPLITPLAY_HAVE_FREERDP
  } else if (connector_name == "freerdp") {
    seat::FreeRdpConnector::Options o;
    o.verbose = log_min_level() <= LogLevel::debug;
    connector = std::make_unique<seat::FreeRdpConnector>(o);
#endif
  } else {
    std::fprintf(stderr, "splitplay-seat: --connector must be one of: %s\n",
#ifdef SPLITPLAY_HAVE_FREERDP
                 "freerdp, null"
#else
                 "null (built without FreeRDP)"
#endif
    );
    return kExitUsage;
  }

  trace::register_provider();
  int rc;
  {
    seat::SeatApp app(options, std::move(connector));
    rc = app.run();
  }
  trace::unregister_provider();
  return rc;
}
