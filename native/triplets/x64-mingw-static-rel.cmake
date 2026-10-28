# SPDX-License-Identifier: MIT
# Local compile and link checks from Linux only (MinGW-w64 cross build of the seat with FreeRDP, preset
# mingw-cross-freerdp). CI and releases use MSVC with x64-windows-static-rel.
set(VCPKG_TARGET_ARCHITECTURE x64)
set(VCPKG_CRT_LINKAGE dynamic)
set(VCPKG_LIBRARY_LINKAGE static)
set(VCPKG_ENV_PASSTHROUGH PATH)
set(VCPKG_CMAKE_SYSTEM_NAME MinGW)
set(VCPKG_BUILD_TYPE release)
