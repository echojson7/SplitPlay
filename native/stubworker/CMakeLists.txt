# SPDX-License-Identifier: MIT
# splitplay-stubworker.exe: the stub pipeline, standalone (--self-test) or under the service (--pipe/--desk).

add_executable(splitplay-stubworker
  src/main.cpp
  src/stub_pipeline.cpp)
target_link_libraries(splitplay-stubworker PRIVATE splitplay::pipeline splitplay::common)
