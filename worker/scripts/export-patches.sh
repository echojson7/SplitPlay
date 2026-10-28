#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-only
#
# Regenerate worker/patches/*.patch from the commits on top of `splitplay-base` in the fetched tree.
#
#   worker/scripts/fetch.sh                  # tree with the current queue applied as commits
#   (edit, git commit / git commit --amend / git rebase -i splitplay-base inside <build-dir>/src)
#   worker/scripts/export-patches.sh [--build-dir DIR]
#
# The output is stable: no commit hashes, no version signature, so unchanged patches produce no diff.
set -euo pipefail

WORKER_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUILD_DIR="${WORKER_BUILD_DIR:-${WORKER_DIR}/build}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --build-dir) BUILD_DIR="$2"; shift 2 ;;
    -h|--help) sed -n '3,9p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "export-patches.sh: unknown argument: $1" >&2; exit 2 ;;
  esac
done

SRC_DIR="${BUILD_DIR}/src"
git -C "${SRC_DIR}" rev-parse -q --verify 'refs/tags/splitplay-base^{commit}' >/dev/null \
  || { echo "export-patches.sh: ${SRC_DIR} has no splitplay-base tag; run fetch.sh first" >&2; exit 1; }

rm -f "${WORKER_DIR}"/patches/*.patch
mkdir -p "${WORKER_DIR}/patches"
git -C "${SRC_DIR}" format-patch -q \
  --zero-commit --no-signature --no-numbered --no-stat --full-index \
  -o "${WORKER_DIR}/patches" splitplay-base..HEAD
ls -1 "${WORKER_DIR}/patches"
