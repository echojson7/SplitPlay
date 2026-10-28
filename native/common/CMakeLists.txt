# SPDX-License-Identifier: MIT
# splitplay_common: control IPC client (child side), capture/input ring helpers, QPC and ETW tracing, logging.

add_library(splitplay_common STATIC
  src/args.cpp
  src/capture_ring.cpp
  src/input_ring.cpp
  src/ipc_client.cpp
  src/ipc_codec.cpp
  src/log.cpp
  src/object_names.cpp
  src/qpc.cpp
  src/trace.cpp
  src/trace_etw.cpp)

if(WIN32)
  target_sources(splitplay_common PRIVATE
    src/pipe_transport.cpp
    src/shared_memory.cpp
    src/win_util.cpp)
  target_link_libraries(splitplay_common PUBLIC advapi32)
endif()

add_library(splitplay::common ALIAS splitplay_common)

target_include_directories(splitplay_common PUBLIC
  "${CMAKE_CURRENT_SOURCE_DIR}/include"
  "${PROJECT_SOURCE_DIR}/include")
target_compile_definitions(splitplay_common PUBLIC SPLITPLAY_VERSION="${PROJECT_VERSION}")
target_link_libraries(splitplay_common PUBLIC splitplay::build_options nlohmann_json::nlohmann_json)
