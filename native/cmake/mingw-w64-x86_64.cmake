# SPDX-License-Identifier: MIT
# Toolchain file for compile checks of the Windows code from Linux with MinGW-w64 (posix threads).
# CI and releases use MSVC; this is only a developer convenience (binaries can be run under Wine).
set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_SYSTEM_PROCESSOR x86_64)
set(CMAKE_C_COMPILER x86_64-w64-mingw32-gcc-posix)
set(CMAKE_CXX_COMPILER x86_64-w64-mingw32-g++-posix)
set(CMAKE_RC_COMPILER x86_64-w64-mingw32-windres)
set(CMAKE_FIND_ROOT_PATH /usr/x86_64-w64-mingw32)
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
find_program(SPLITPLAY_WINE NAMES wine64 wine PATHS /usr/lib/wine)
if(SPLITPLAY_WINE)
  set(CMAKE_CROSSCOMPILING_EMULATOR "${SPLITPLAY_WINE}")
endif()
