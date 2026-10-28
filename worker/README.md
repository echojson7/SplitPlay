# SplitPlay worker (`splitplay-worker.exe`)

The worker is the per-desk streaming agent: one process per desk, running as SYSTEM inside the desk's
RDP-loopback session, speaking the Moonlight protocol to clients. It is [LizardByte Sunshine](https://github.com/LizardByte/Sunshine)
with a small queue of SplitPlay patches on top.

Everything in this directory, and everything built from it, is licensed under the **GNU General Public License v3.0**
(see [`COPYING`](COPYING)). The rest of the SplitPlay repository is MIT and never links worker code; the two sides
meet only through the contracts in [`../docs/contracts/`](../docs/contracts/).

## Patch-queue model

There is no fork repository. The worker is defined by three things in this directory:

| File | Meaning |
|---|---|
| [`upstream.lock`](upstream.lock) | upstream repository and the exact commit we build from |
| [`patches/*.patch`](patches/) | SplitPlay changes, applied in file-name order with `git am` |
| [`scripts/`](scripts/) | fetch, build, licence check, smoke test, patch export |

`scripts/fetch.sh` clones the pinned commit (plus the submodules the Windows build needs) into `build/src`, tags it
`splitplay-base`, copies the MIT contract headers from `native/include/splitplay/` to `build/src/third-party/splitplay/include/`
unmodified, and applies every patch as one commit. A patch that does not apply exactly (no fuzz, no 3-way merge) stops
the script with an error, so a stale queue can never produce a half-patched build.

Each patch is small, does one thing and explains itself in its commit message:

| Patch | What it does |
|---|---|
| `0001` product identity | executable `splitplay-worker`, SplitPlay version string (`--version`, `--help`, log), `--desk-dir` for all config/state paths, working directory = exe directory, tray off by default, no browser launch |
| `0002` control client | child side of [`ipc.md`](../docs/contracts/ipc.md), role `worker`: `--pipe`/`--desk`, hello/welcome/ready, 1 s heartbeats, `shutdown`, `pair`/`list_clients`/`unpair`, exit when the pipe breaks |
| `0003` session targeting | the user token for launched apps comes from the worker's own session instead of `WTSGetActiveConsoleSessionId()` |
| `0004` ViGEm off | CMake option `SUNSHINE_ENABLE_VIGEM` (default `OFF`); gamepads arrive with SplitPlay's own pad driver in Stage 4 |
| `0005` build fix | the NVENC object libraries link `${Boost_LIBRARIES}`, so they find Boost.Log when Boost 1.89 comes from FetchContent (MSYS2 now ships a newer Boost than the pinned tree accepts) |

### Changing a patch or adding one

```bash
worker/scripts/fetch.sh                      # build/src = upstream + queue, one commit per patch
cd worker/build/src
# edit, then either amend the matching commit (git commit --fixup + git rebase -i --autosquash splitplay-base)
# or add a new commit on top
cd -
worker/scripts/export-patches.sh             # rewrites worker/patches/*.patch from splitplay-base..HEAD
worker/scripts/fetch.sh                      # proves the queue applies to a clean clone
```

`export-patches.sh` writes stable files (no commit hashes, no git version), so untouched patches show no diff.

### Moving the upstream pin

Change `commit=` in `upstream.lock`, run `fetch.sh`, rebase the queue onto the new commit, export, build. The pin must
stay at or before upstream `cf52f4b6`, the last commit before libvirtualhid (687e12d0) brought in files under the
LizardByte Source-Available License, unless every such file is removed from the build. `scripts/check-licenses.sh`
fails the build if any file in `worker/` (patch queue, scripts and the patched tree), the npm packages or the
downloaded dependencies carries that licence's SPDX identifier. As in the repository-wide check of `native.yml`,
Markdown under a scanned root's `docs/` and files under its `.github/workflows/` are exempt, since they may name the
identifier when stating the policy.

## Building

The toolchain is Sunshine's own: MSYS2 UCRT64 with GCC, CMake and Ninja, plus Node.js for the web UI. In an
MSYS2 UCRT64 shell with the packages from [`.github/workflows/worker.yml`](../.github/workflows/worker.yml) installed:

```bash
worker/scripts/fetch.sh
worker/scripts/check-licenses.sh
NODEJS_PATH='C:\Program Files\nodejs' worker/scripts/build.sh
pwsh worker/scripts/smoke-test.ps1
```

`build.sh` configures a Release build with tray, tests, docs and ViGEm off, builds only the worker and its web UI
(not upstream's `sunshinesvc`, `dxgi-info` and `audio-info`), uses `ccache` when installed and assembles `worker/out/`:

```
splitplay-worker.exe   statically linked; only zlib1.dll and Windows system DLLs are loaded (checked with ldd)
zlib1.dll
assets/                shaders, web UI, apps.json, images (found relative to the exe)
COPYING, SOURCE.txt    licence and where the corresponding source is
patches/               the patch queue and upstream.lock
```

The version comes from [`VERSION`](VERSION) (override with `SPLITPLAY_VERSION`). CI (`.github/workflows/worker.yml`)
runs fetch, licence check, build, a second licence check over npm packages and downloaded dependencies, the smoke
test, and uploads `worker/out/` as the `worker-win-x64` artifact.

## Running

SplitPlay.Service starts one worker per desk:

```
splitplay-worker.exe --desk-dir C:\ProgramData\SplitPlay\desks\desk-1 ^
                     --pipe \\.\pipe\splitplay\worker-desk-1 --desk desk-1 port=48089
```

| Option | Meaning |
|---|---|
| `--desk-dir <dir>` | Directory for `sunshine.conf`, `sunshine_state.json` (pairings, unique id), `credentials/` (certificate), `apps.json`, `covers/` and `sunshine.log`. Default: `<exe dir>\config`. Created if missing. |
| `--pipe <name>` | Control pipe created by the service ([`ipc.md`](../docs/contracts/ipc.md)). Without it the worker runs standalone (development only). |
| `--desk <id>` | Desk id `[a-z0-9-]{1,32}`, sent in `hello`. Required with `--pipe`. |
| `--version` | Print `splitplay-worker <version> (SplitPlay desk worker, based on Sunshine <commit>)` and exit 0. |
| `name=value` | Any upstream Sunshine setting, overriding `sunshine.conf` (e.g. `port=` for the desk's base port). |

All three SplitPlay options also accept `--name=value`. They are removed before Sunshine's own parser runs, so all
upstream options keep working.

Control IPC behaviour (protocol 1):

- `hello` is sent right after start-up (before encoder probing), then the worker waits up to 10 s for `welcome`.
  The initial profile is logged; it is applied from Stage 3 on.
- `heartbeat` every 1000 ms from `welcome` on, with `state` `starting` until `ready`, then `idle` or `streaming`.
  `metrics.clients` is the number of active streams; the encoder metrics are 0 until Stage 3.
- `ready` (with `ports.base`) once the HTTP base port accepts connections on loopback.
- `shutdown` is answered and the worker exits with code 0. `pair`, `list_clients` and `unpair` are served;
  `set_profile` and `send_sas` answer `unsupported`; other requests answer `unknown_type`.
- Exit codes: 0 after `shutdown`, 2 for an invalid command line, 3 when the handshake fails or the pipe breaks.

## Not done yet

- Live profile changes (`set_profile`), real encoder metrics, frame tracing, the IDD capture ring and the input ring
  (the contract headers are already on the include path).
- Gamepads (ViGEm is off; SplitPlay's pad driver comes in Stage 4).
- Upstream's NVIDIA profile tweaks (`nvprefs`) still change the global driver profile; with several workers they
  should move to the service.
