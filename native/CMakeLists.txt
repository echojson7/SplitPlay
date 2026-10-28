# SPDX-License-Identifier: MIT
#
# SplitPlay native components: splitplay_common, the stub capture/encode/network pipeline,
# splitplay-seat.exe, splitplay-rdsprov.dll, splitplay-stubworker.exe and their tests.
#
# Windows (MSVC) is the target platform. On other hosts only the portable parts (IPC codec and client,
# ring helpers, tracing, null bitstream, UDP loopback transport) and their tests are built, which is
# enough for local checks on Linux. A MinGW-w64 toolchain file is provided for compile checks of the
# Windows code from Linux (cmake/mingw-w64-x86_64.cmake); CI builds with MSVC.
cmake_minimum_required(VERSION 3.24)

project(SplitPlayNative
  VERSION 0.1.0
  DESCRIPTION "SplitPlay native components"
  LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
# Static CRT: the executables and the TermService provider DLL must not depend on a VC++ redistributable.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")

option(SPLITPLAY_BUILD_TESTS "Build the native tests" ON)
option(SPLITPLAY_WARNINGS_AS_ERRORS "Treat compiler warnings as errors" OFF)
# splitplay-seat's FreeRDP 3 connector. The libraries come from vcpkg in manifest mode (vcpkg.json, triplet
# x64-windows-static-rel from triplets/); the windows-x64 presets set the vcpkg toolchain from $env{VCPKG_ROOT}.
# Without it the seat only has the null connector.
if(WIN32 AND MSVC)
  set(_splitplay_freerdp_default ON)
else()
  set(_splitplay_freerdp_default OFF)
endif()
option(SPLITPLAY_WITH_FREERDP "Build splitplay-seat with the FreeRDP connector" ${_splitplay_freerdp_default})

if(WIN32 AND SPLITPLAY_WITH_FREERDP AND MSVC)
  # The vcpkg triplet is release-only with the static CRT; use the same CRT in every configuration so that Debug
  # builds link too.
  set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
endif()

list(APPEND CMAKE_MODULE_PATH "${CMAKE_CURRENT_SOURCE_DIR}/cmake")
include(SplitPlayOptions)

# ---- third-party -------------------------------------------------------------------------------
include(FetchContent)
# nlohmann/json (MIT), header-only. Pinned release archive with hash.
FetchContent_Declare(nlohmann_json
  URL https://github.com/nlohmann/json/releases/download/v3.11.3/json.tar.xz
  URL_HASH SHA256=d6c65aca6b1ed68e7a182f4757257b107ae403032760ed6ef121c9d55e81757d
  DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
set(JSON_BuildTests OFF CACHE INTERNAL "")
set(JSON_Install OFF CACHE INTERNAL "")
FetchContent_MakeAvailable(nlohmann_json)

if(WIN32 AND SPLITPLAY_WITH_FREERDP)
  # FreeRDP 3 client (Apache-2.0), static. Package names as installed by the vcpkg port.
  find_package(WinPR 3 CONFIG REQUIRED)
  find_package(FreeRDP 3 CONFIG REQUIRED)
  find_package(FreeRDP-Client 3 CONFIG REQUIRED)
  message(STATUS "splitplay-seat: FreeRDP connector enabled (FreeRDP ${FreeRDP_VERSION})")
endif()

# ---- components ----------------------------------------------------------------------------------
add_subdirectory(common)
add_subdirectory(pipeline)

if(WIN32)
  add_subdirectory(seat)
  add_subdirectory(rdsprov)
  add_subdirectory(stubworker)
endif()

if(SPLITPLAY_BUILD_TESTS)
  enable_testing()
  add_subdirectory(tests)
endif()

if(WIN32)
  install(TARGETS splitplay-seat splitplay-stubworker splitplay-rdsprov
    RUNTIME DESTINATION bin
    LIBRARY DESTINATION bin)
endif()
