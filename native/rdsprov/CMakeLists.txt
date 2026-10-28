# SPDX-License-Identifier: MIT
# splitplay-rdsprov.dll: COM skeleton of the TermService protocol provider wrapper (Stage 4 fills it in).
# It deliberately does not link splitplay_common: the DLL runs inside TermService and stays minimal.

configure_file(src/version.rc.in "${CMAKE_CURRENT_BINARY_DIR}/version.rc" @ONLY)

add_library(splitplay-rdsprov SHARED
  src/dllmain.cpp
  src/rdsprov.cpp
  rdsprov.def
  "${CMAKE_CURRENT_BINARY_DIR}/version.rc")
target_link_libraries(splitplay-rdsprov PRIVATE splitplay::build_options ole32 uuid)
set_target_properties(splitplay-rdsprov PROPERTIES PREFIX "")
target_include_directories(splitplay-rdsprov PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/src")
