#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-only
#
# Configure and build splitplay-worker.exe from the tree prepared by fetch.sh. Run inside MSYS2 UCRT64.
#
#   worker/scripts/build.sh [--build-dir DIR] [--out DIR] [--jobs N]
#
# Output (<out>, default worker/out):
#   splitplay-worker.exe, zlib1.dll        the worker (statically linked apart from zlib1.dll)
#   assets/                                shaders, web UI, apps.json and images, resolved relative to the exe
#   COPYING, SOURCE.txt, patches/          GPL-3.0 licence and the corresponding source description
#
# Environment: SPLITPLAY_VERSION (default: worker/VERSION), NODEJS_PATH (Windows path of a Node.js install,
# added to PATH when npm is not found), CMAKE_EXTRA_ARGS (appended to the cmake configure command line).
set -euo pipefail

WORKER_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_ROOT="$(cd "${WORKER_DIR}/.." && pwd)"
BUILD_DIR="${WORKER_BUILD_DIR:-${WORKER_DIR}/build}"
OUT_DIR="${WORKER_OUT_DIR:-${WORKER_DIR}/out}"
JOBS="${JOBS:-}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --build-dir) BUILD_DIR="$2"; shift 2 ;;
    --out) OUT_DIR="$2"; shift 2 ;;
    --jobs) JOBS="$2"; shift 2 ;;
    -h|--help) sed -n '3,15p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "build.sh: unknown argument: $1" >&2; exit 2 ;;
  esac
done

die() { echo "::error::build.sh: $*" >&2; exit 1; }

[[ "${MSYSTEM:-}" == "UCRT64" ]] || die "run this script in an MSYS2 UCRT64 shell (MSYSTEM=${MSYSTEM:-unset})"

SRC_DIR="${BUILD_DIR}/src"
CMAKE_DIR="${BUILD_DIR}/cmake"
[[ -f "${SRC_DIR}/CMakeLists.txt" ]] || die "${SRC_DIR} is missing; run worker/scripts/fetch.sh first"
git -C "${SRC_DIR}" rev-parse -q --verify 'refs/tags/splitplay-base^{commit}' >/dev/null \
  || die "${SRC_DIR} was not prepared by fetch.sh"

UPSTREAM_COMMIT="$(git -C "${SRC_DIR}" rev-parse 'refs/tags/splitplay-base^{commit}')"
SPLITPLAY_VERSION="${SPLITPLAY_VERSION:-$(tr -d ' \r\n' < "${WORKER_DIR}/VERSION")}"
SPLITPLAY_COMMIT="$(git -C "${REPO_ROOT}" rev-parse --short=12 HEAD 2>/dev/null || echo unknown)"

if ! command -v npm >/dev/null 2>&1 && [[ -n "${NODEJS_PATH:-}" ]]; then
  PATH="${PATH}:$(cygpath -u "${NODEJS_PATH}")"
  export PATH
fi
command -v npm >/dev/null 2>&1 || die "npm not found (the Sunshine web UI is built with Node.js); set NODEJS_PATH"

echo "==> splitplay-worker ${SPLITPLAY_VERSION} from Sunshine ${UPSTREAM_COMMIT} (SplitPlay ${SPLITPLAY_COMMIT})"

# Sunshine's build_version.cmake takes the version from these when BRANCH and BUILD_VERSION are set,
# instead of asking git (the patched tree would report a "-dirty" development version).
export BRANCH="splitplay"
export BUILD_VERSION="${SPLITPLAY_VERSION}"
export COMMIT="${UPSTREAM_COMMIT:0:8}+splitplay.${SPLITPLAY_COMMIT}"

launcher_args=()
if command -v ccache >/dev/null 2>&1; then
  launcher_args=(-DCMAKE_C_COMPILER_LAUNCHER=ccache -DCMAKE_CXX_COMPILER_LAUNCHER=ccache)
  ccache --zero-stats >/dev/null || true
fi

# shellcheck disable=SC2206 # CMAKE_EXTRA_ARGS is intentionally word-split
extra_args=(${CMAKE_EXTRA_ARGS:-})

echo "==> configure"
cmake -S "${SRC_DIR}" -B "${CMAKE_DIR}" -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DBUILD_DOCS=OFF \
  -DBUILD_TESTS=OFF \
  -DBUILD_WERROR=OFF \
  -DSUNSHINE_ENABLE_TRAY=OFF \
  -DSUNSHINE_ENABLE_VIGEM=OFF \
  -DSUNSHINE_ASSETS_DIR=assets \
  -DSUNSHINE_PUBLISHER_NAME="SplitPlay" \
  -DSUNSHINE_PUBLISHER_WEBSITE="https://github.com/echojson7/SplitPlay" \
  -DSUNSHINE_PUBLISHER_ISSUE_URL="https://github.com/echojson7/SplitPlay/issues" \
  -DSPLITPLAY_VERSION="${SPLITPLAY_VERSION}" \
  -DSPLITPLAY_UPSTREAM_COMMIT="${UPSTREAM_COMMIT:0:8}" \
  "${launcher_args[@]}" \
  "${extra_args[@]}"

echo "==> build"
ninja_args=(-C "${CMAKE_DIR}")
[[ -z "${JOBS}" ]] || ninja_args+=(-j "${JOBS}")
# Only the worker and its web UI; the upstream tools (sunshinesvc, dxgi-info, audio-info) are not shipped.
ninja "${ninja_args[@]}" sunshine web-ui

if command -v ccache >/dev/null 2>&1; then
  ccache --show-stats || true
fi

EXE="${CMAKE_DIR}/splitplay-worker.exe"
[[ -f "${EXE}" ]] || die "${EXE} was not produced"

echo "==> package ${OUT_DIR}"
rm -rf "${OUT_DIR}"
mkdir -p "${OUT_DIR}/assets"
cp "${EXE}" "${OUT_DIR}/"

# zlib1.dll is loaded at run time by OpenSSL's libcrypto (same hardening as upstream's installer).
ZLIB_DLL="$(sed -n 's/^ZLIB:FILEPATH=//p' "${CMAKE_DIR}/CMakeCache.txt")"
[[ -n "${ZLIB_DLL}" && -f "${ZLIB_DLL}" ]] || ZLIB_DLL="${MINGW_PREFIX:-/ucrt64}/bin/zlib1.dll"
[[ -f "${ZLIB_DLL}" ]] || die "zlib1.dll not found"
cp "${ZLIB_DLL}" "${OUT_DIR}/"

# Same layout as upstream's install(): common assets without web/, Windows assets, then the built web UI.
(cd "${SRC_DIR}/src_assets/common/assets" && find . -path ./web -prune -o -type f -print0 \
  | xargs -0 -I{} cp --parents {} "${OUT_DIR}/assets/")
cp -r "${SRC_DIR}/src_assets/windows/assets/." "${OUT_DIR}/assets/"
[[ -d "${CMAKE_DIR}/assets/web" ]] || die "web UI was not built (${CMAKE_DIR}/assets/web missing)"
cp -r "${CMAKE_DIR}/assets/web" "${OUT_DIR}/assets/"

cp "${WORKER_DIR}/COPYING" "${OUT_DIR}/COPYING"
mkdir -p "${OUT_DIR}/patches"
cp "${WORKER_DIR}"/patches/*.patch "${OUT_DIR}/patches/"
cp "${WORKER_DIR}/upstream.lock" "${OUT_DIR}/patches/upstream.lock"
cat > "${OUT_DIR}/SOURCE.txt" <<EOF
splitplay-worker ${SPLITPLAY_VERSION}

This program is free software under the GNU General Public License v3.0 (see COPYING).
It is LizardByte Sunshine ${UPSTREAM_COMMIT}
(https://github.com/LizardByte/Sunshine) with the SplitPlay patches in patches/ applied.
Complete corresponding source: https://github.com/echojson7/SplitPlay, directory worker/,
commit ${SPLITPLAY_COMMIT}. Rebuild with worker/scripts/fetch.sh and worker/scripts/build.sh.
EOF

echo "==> linkage"
# Everything except zlib1.dll must be linked statically or come from Windows itself.
if linkage="$(ldd "${OUT_DIR}/splitplay-worker.exe")"; then
  echo "${linkage}"
else
  echo "${linkage}"
  die "ldd failed"
fi
unexpected="$(awk '
  BEGIN { IGNORECASE = 1 }
  $1 == "zlib1.dll" { next }
  $0 ~ /=>[[:space:]]+not found/ { print }
  $0 ~ /=>[[:space:]]+\/(clang64|mingw32|mingw64|ucrt64)\/bin\// { print }
' <<< "${linkage}")"
[[ -z "${unexpected}" ]] || die "unexpected runtime dependencies:
${unexpected}"

echo "==> done"
ls -la "${OUT_DIR}"
