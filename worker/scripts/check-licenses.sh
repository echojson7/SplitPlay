#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-only
#
# Fail if any file under the given directories carries the SPDX identifier of the LizardByte
# Source-Available License (libvirtualhid and anything else under it must never enter the worker build;
# see docs/contracts/README.md, "Licence boundary").
#
#   worker/scripts/check-licenses.sh [DIR...]      (default: <build-dir>/src, the patched upstream tree)
#
# Scans every file, including submodules, node_modules and downloaded dependencies when they are inside DIR.
# Like the repository-wide check in native.yml, Markdown under <DIR>/docs/ and files under
# <DIR>/.github/workflows/ are excluded: they may name the identifier when stating a policy or guarding against it.
set -euo pipefail

WORKER_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUILD_DIR="${WORKER_BUILD_DIR:-${WORKER_DIR}/build}"

# Built from two parts so that this script does not match itself.
SAL_PART_1='LicenseRef-LizardByte'
SAL_PART_2='-SAL'
PATTERN="${SAL_PART_1}${SAL_PART_2}"

dirs=("$@")
[[ ${#dirs[@]} -gt 0 ]] || dirs=("${BUILD_DIR}/src")

status=0
for dir in "${dirs[@]}"; do
  dir="${dir%/}"
  if [[ ! -d "${dir}" ]]; then
    echo "::error::check-licenses.sh: ${dir} does not exist" >&2
    status=1
    continue
  fi
  echo "==> scanning ${dir}"
  # -I skips binary files, -l lists matching files, -F fixed string; .git metadata is not source.
  rc=0
  matches="$(grep -rIlF --exclude-dir=.git -e "${PATTERN}" -- "${dir}")" || rc=$?
  if [[ ${rc} -gt 1 ]]; then
    echo "::error::check-licenses.sh: grep failed on ${dir} (exit ${rc})" >&2
    status=1
    continue
  fi

  hits=()
  while IFS= read -r file; do
    [[ -n "${file}" ]] || continue
    rel="${file#"${dir}"/}"
    if [[ "${rel}" == docs/*.md || "${rel}" == .github/workflows/* ]]; then
      echo "    excluded (policy text): ${rel}"
      continue
    fi
    hits+=("${file}")
  done <<< "${matches}"

  if [[ ${#hits[@]} -gt 0 ]]; then
    echo "::error::files under the LizardByte Source-Available License found in ${dir}:" >&2
    printf '    %s\n' "${hits[@]}" >&2
    status=1
  else
    echo "    no ${PATTERN} files"
  fi
done

exit ${status}
