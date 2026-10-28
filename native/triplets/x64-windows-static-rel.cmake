# SPDX-License-Identifier: MIT
# x64, static libraries, static CRT (/MT), release only: halves the FreeRDP/OpenSSL build time. Debug builds of
# SplitPlay link these release libraries (CMakeLists.txt selects the release CRT for every configuration when
# FreeRDP is enabled).
set(VCPKG_TARGET_ARCHITECTURE x64)
set(VCPKG_CRT_LINKAGE static)
set(VCPKG_LIBRARY_LINKAGE static)
set(VCPKG_BUILD_TYPE release)
