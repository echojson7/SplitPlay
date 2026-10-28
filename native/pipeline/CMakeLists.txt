# SPDX-License-Identifier: MIT
# splitplay_pipeline: stage interfaces and the Stage 2 stub implementations.

add_library(splitplay_pipeline STATIC
  src/null_bitstream.cpp
  src/sendinput_sink.cpp
  src/test_pattern.cpp
  src/udp_loopback_transport.cpp)

if(WIN32)
  target_sources(splitplay_pipeline PRIVATE
    src/d3d_util.cpp
    src/null_encoder.cpp
    src/ring_capture_source.cpp
    src/synthetic_capture_producer.cpp)
  # d3d12.dll is loaded on demand (shared fences by name), so it is not linked.
  target_link_libraries(splitplay_pipeline PUBLIC d3d11 dxgi ws2_32)
endif()

add_library(splitplay::pipeline ALIAS splitplay_pipeline)
target_include_directories(splitplay_pipeline PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_link_libraries(splitplay_pipeline PUBLIC splitplay::common)
