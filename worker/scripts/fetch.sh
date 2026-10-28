#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-only
#
# Fetch the pinned upstream Sunshine tree and apply the SplitPlay patch queue.
#
#   worker/scripts/fetch.sh [--build-dir DIR] [--no-patches]
#
# Result: <build-dir>/src is a git work tree whose HEAD is the pinned upstream commit plus one commit per
# worker/patches/*.patch (tag `splitplay-base` marks the pinned commit). Any patch that does not apply cleanly
# aborts the script with a non-zero exit code. Works on Linux, macOS and MSYS2.
set -euo pipefail

WORKER_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_ROOT="$(cd "${WORKER_DIR}/.." && pwd)"
BUILD_DIR="${WORKER_BUILD_DIR:-${WORKER_DIR}/build}"
APPLY_PATCHES=1

while [[ $# -gt 0 ]]; do
  case "$1" in
    --build-dir) BUILD_DIR="$2"; shift 2 ;;
    --no-patches) APPLY_PATCHES=0; shift ;;
    -h|--help) sed -n '3,10p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "fetch.sh: unknown argument: $1" >&2; exit 2 ;;
  esac
done

die() { echo "::error::fetch.sh: $*" >&2; exit 1; }

lock_value() {
  local value
  value="$(sed -n "s/^$1=//p" "${WORKER_DIR}/upstream.lock" | tr -d '\r' | head -n 1)"
  [[ -n "${value}" ]] || die "upstream.lock has no '$1=' entry"
  printf '%s' "${value}"
}

UPSTREAM_REPO="$(lock_value repo)"
UPSTREAM_COMMIT="$(lock_value commit)"
SRC_DIR="${BUILD_DIR}/src"

# Submodules the Windows build needs. The others (Linux capture, Flatpak, docs, tray) are not fetched.
# build-deps is fetched without its own submodules: CMake only reads its release tag to download the
# matching prebuilt FFmpeg archive.
RECURSIVE_SUBMODULES=(
  third-party/Simple-Web-Server
  third-party/ViGEmClient
  third-party/libdisplaydevice
  third-party/lizardbyte-common
  third-party/moonlight-common-c
  third-party/nvapi
)
FLAT_SUBMODULES=(
  third-party/build-deps
)

# core.autocrlf=false: the tree and the patches keep upstream's line endings on Windows too
# (the setting is inherited by the git processes that `submodule update` spawns).
git_src() { git -C "${SRC_DIR}" -c advice.detachedHead=false -c core.autocrlf=false "$@"; }

retry() {
  local n=0 delay=2
  until "$@"; do
    n=$((n + 1))
    [[ ${n} -lt 4 ]] || return 1
    echo "fetch.sh: retrying in ${delay}s: $*" >&2
    sleep "${delay}"
    delay=$((delay * 2))
  done
}

echo "==> upstream ${UPSTREAM_REPO} @ ${UPSTREAM_COMMIT}"
echo "==> source tree ${SRC_DIR}"
mkdir -p "${BUILD_DIR}"

if [[ -d "${SRC_DIR}/.git" ]] && git_src rev-parse -q --verify "refs/tags/splitplay-base^{commit}" >/dev/null \
    && [[ "$(git_src rev-parse 'refs/tags/splitplay-base^{commit}')" == "${UPSTREAM_COMMIT}" ]]; then
  echo "==> reusing existing clone, resetting to the pinned commit"
  git_src am --abort >/dev/null 2>&1 || true
  git_src checkout -q --detach splitplay-base
  git_src reset -q --hard splitplay-base
  git_src clean -q -ffdx -e node_modules
else
  rm -rf "${SRC_DIR}"
  git -c core.autocrlf=false init -q "${SRC_DIR}"
  git_src remote add origin "${UPSTREAM_REPO}"
  retry git_src fetch -q --depth 1 --no-tags origin "${UPSTREAM_COMMIT}"
  git_src checkout -q --detach FETCH_HEAD
  git_src tag splitplay-base
fi

[[ "$(git_src rev-parse HEAD)" == "${UPSTREAM_COMMIT}" ]] || die "checked out $(git_src rev-parse HEAD), expected ${UPSTREAM_COMMIT}"

echo "==> submodules"
git_src submodule sync -q
retry git_src submodule update -q --init --depth 1 --jobs 4 -- "${FLAT_SUBMODULES[@]}"
retry git_src submodule update -q --init --depth 1 --recursive --jobs 4 -- "${RECURSIVE_SUBMODULES[@]}"
git_src submodule status --recursive | sed 's/^/    /'

# MIT contract headers from this repository, copied unmodified (see docs/contracts/README.md, licence boundary).
echo "==> contract headers"
CONTRACTS_DST="${SRC_DIR}/third-party/splitplay/include/splitplay"
rm -rf "${SRC_DIR}/third-party/splitplay"
mkdir -p "${CONTRACTS_DST}"
cp "${REPO_ROOT}"/native/include/splitplay/*.h "${CONTRACTS_DST}/"
grep -qx '/third-party/splitplay/' "${SRC_DIR}/.git/info/exclude" 2>/dev/null \
  || echo '/third-party/splitplay/' >> "${SRC_DIR}/.git/info/exclude"
ls "${CONTRACTS_DST}" | sed 's/^/    /'

if [[ ${APPLY_PATCHES} -eq 1 ]]; then
  shopt -s nullglob
  patches=("${WORKER_DIR}"/patches/*.patch)
  shopt -u nullglob
  echo "==> applying ${#patches[@]} patch(es)"
  for patch in "${patches[@]}"; do
    echo "    $(basename "${patch}")"
    # No --3way and no fuzz: every patch must apply exactly to the pinned tree.
    if ! git_src -c user.name="SplitPlay CI" -c user.email="ci@splitplay.invalid" \
        am -q --committer-date-is-author-date "${patch}"; then
      echo "::error::patch does not apply cleanly: $(basename "${patch}")" >&2
      git_src apply --check -v "${patch}" 2>&1 | tail -n 40 >&2 || true
      git_src am --abort || true
      exit 1
    fi
  done
fi

git_src log --oneline splitplay-base..HEAD | sed 's/^/    /'
echo "==> done: ${SRC_DIR}"
