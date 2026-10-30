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
| `0004` ViGEm option | CMake option `SUNSHINE_ENABLE_VIGEM` (default `OFF`); `build.sh` turns it **on** for ViGEm gamepads (see *Gamepads* below) |
| `0005` build fix | the NVENC object libraries link `${Boost_LIBRARIES}`, so they find Boost.Log when Boost 1.89 comes from FetchContent (MSYS2 now ships a newer Boost than the pinned tree accepts) |
| `0006` display settings | libdisplaydevice is not used (nothing is applied, reverted or "recovered", encoder probing no longer depends on its CCD view); a Moonlight launch/resume sends `event mode_requested {width,height,fps}` and waits up to 3 s for the session to change size |
| `0007` capture visibility | the capture banner names the session, DXGI output and adapter LUID; DXGI's view of the session is logged when no output is found; a warning when the session renders on Microsoft Basic Render Driver |
| `0008` session audio | captures the session's default render endpoint ("Remote Audio") through WASAPI loopback; no Steam Streaming Speakers install, no virtual sink unless `virtual_sink` is set, the default endpoint is never changed; the session's endpoints are logged at start-up |
| `0009` desk defaults | `apps.json` offers only "Desktop"; `capture=ddx`, `origin_web_ui_allowed=pc`, `system_tray=disabled` by default |
| `0010` self-test | `--self-test <seconds>`: captures the session's display, moves the mouse with SendInput and reads the default endpoint via WASAPI loopback, writes `<desk-dir>/self-test.json` |
| `0011` live stats | heartbeats carry real stream numbers (`fps`, `bitrate_kbps`, `encode_ms_p50/p99`, `loss_pct`, `rtt_ms`) and a `sessions` list (client name and uuid, address, mode, codec, requested bitrate, HDR); `event client_connected`/`client_disconnected` when a stream is up (its client connected the control stream) and when it ends. Streaming threads only bump counters and RTSP/control threads only queue the events; the heartbeat thread does the rest and all the pipe writes |
| `0012` scheduler protection | the process opts out of power throttling (EcoQoS and ignored timer-resolution requests, `SetProcessInformation(ProcessPowerThrottling)`) and runs ABOVE_NORMAL when idle (upstream's HIGH while streaming is kept); capture, encode, video send and control receive threads run under MMCSS "Games" at normal priority. See *Scheduling* below |
| `0013` stream profile | `welcome` and `set_profile` apply the Profile's `fec_percent`, `min_fps`, `pacing_mbps` (live) and `dpb_frames` (next stream, opt-in); pacing replaces upstream's fixed 800 Mbit/s, the DPB upstream's 5 (H.264/HEVC) or 8 (AV1) reference frames. See *Stream profile* below |
| `0014` instant feedback | rumble, LED and motion requests and HDR metadata are sent as soon as they are queued: the producers ring a loopback "doorbell" datagram that the control thread's ENet intercept turns into a wake-up, instead of waiting up to ~100 ms for the next control loop cycle |
| `0015` NVIDIA profile | the "prefer maximum performance" application profile (`PREFERRED_PSTATE`) is bound to the running executable's file name (`splitplay-worker.exe`) under the profile name "SplitPlay worker" instead of upstream's `sunshine.exe`/"SunshineStream"; `--restore-nvprefs-undo` also deletes that profile. See *NVIDIA driver settings* below |

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

`build.sh` configures a Release build with tray, tests and docs off and ViGEm on, builds only the worker and its web UI
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
test, and uploads `worker/out/` as the `worker-win-x64` artifact. A second job, `worker-in-rdp-session`, runs the
in-session test described below.

## Running

SplitPlay.Service starts one worker per desk, inside the desk's RDP loopback session:

```
splitplay-worker.exe --desk-dir C:\ProgramData\SplitPlay\desks\desk-1 ^
                     --pipe \\.\pipe\splitplay\worker-desk-1 --desk desk-1 port=48089 ^
                     sunshine_name=HOST-desk-1 min_log_level=info ^
                     nvenc_preset=1 nvenc_twopass=disabled hevc_mode=0 av1_mode=0 gamepad=ds4
```

| Option | Meaning |
|---|---|
| `--desk-dir <dir>` | Directory for `sunshine.conf`, `sunshine_state.json` (pairings, unique id), `credentials/` (certificate), `apps.json`, `covers/`, `sunshine.log` and `self-test.json`. Default: `<exe dir>\config`. Created if missing. |
| `--pipe <name>` | Control pipe created by the service ([`ipc.md`](../docs/contracts/ipc.md)). Without it the worker runs standalone (development only). |
| `--desk <id>` | Desk id `[a-z0-9-]{1,32}`, sent in `hello`. Required with `--pipe`. |
| `--self-test <seconds>` | Diagnose the session instead of serving it (1-600 s, not with `--pipe`), see below. |
| `--version` | Print `splitplay-worker <version> (SplitPlay desk worker, based on Sunshine <commit>)` and exit 0. |
| `name=value` | Any upstream Sunshine setting, overriding `sunshine.conf` (e.g. `port=` for the desk's base port). |

All SplitPlay options also accept `--name=value`. They are removed before Sunshine's own parser runs, so all
upstream options keep working.

### How the service must start it

- Token: a duplicate of the service's SYSTEM token (`DuplicateTokenEx`, primary) with `TokenSessionId` = the desk
  session N (`SetTokenInformation`, needs SeTcbPrivilege, which SYSTEM has).
- `CreateProcessAsUser` with `lpDesktop = "winsta0\\default"` and **`CREATE_NO_WINDOW`** (the worker is a console
  program; without the flag a console window opens on the desk's desktop), inside the desk's job object.
- Nothing else is needed: DXGI, `OpenInputDesktop`/`SendInput` and MMDevice all act on the calling process's
  session, so the worker captures, injects into and records session N only.
- [`scripts/session-launch.ps1`](scripts/session-launch.ps1) is a minimal reference of exactly this (used by CI).

### Settings the service passes (`name=value`)

| Setting | Value | Why |
|---|---|---|
| `port` | desk base port `P` | ports P-5, P, P+9..P+11, P+21 ([`config.md`](../docs/contracts/config.md)) |
| `sunshine_name` | `<HOST>-<desk-id>` | the name Moonlight and mDNS show; otherwise every desk is called after the host |
| `min_log_level` | `info` | first light: the log shows the session, output, adapter, encoder and audio endpoint; `warning` later (Duo lesson) |
| `nvenc_preset` | `1` | P1, the fastest NVENC preset (also the built-in default) |
| `nvenc_twopass` | `disabled` | single pass: lowest encode latency (upstream default is `quarter_res`) |
| `hevc_mode`, `av1_mode` | `0` | advertise HEVC and AV1 when the encoder supports them (RTX 5070: both); Moonlight picks |
| `gamepad` | `ds4` | XInput gives an emulated Xbox 360 pad's buttons only to the console's foreground window (see *Gamepads*) |

Built in since patch 0009/0008 (passing them again is harmless): `capture=ddx`, `install_steam_audio_drivers=disabled`,
`origin_web_ui_allowed=pc`, `system_tray=disabled`; `dd_configuration_option` is ignored (patch 0006).
Leave `encoder`, `adapter_name`, `output_name`, `audio_sink` and `virtual_sink` empty: the probe tries NVENC first
and falls back to QuickSync, AMF, Media Foundation and finally software; the only output and the default endpoint
the worker sees are the session's own.

### What a desk session changes (Stage 3, DXGI Desktop Duplication)

- **Capture.** The worker sees only its session's display, the RdpIdd output, listed by DXGI under the session's
  render adapter. With the policy "Use hardware graphics adapters for all Remote Desktop Services sessions"
  (`bEnumerateHWBeforeSW=1`, set by `splitplay host prepare`) that is the GPU, otherwise Microsoft Basic Render
  Driver (then only software encoding works and the log warns). The capture D3D11 device lives on that adapter and
  NVENC/AMF/QSV are opened on the same device, so encoder and capture GPU always match. Desktop switches (lock
  screen, UAC secure desktop) are followed with `OpenInputDesktop` on the session's WinSta0. The log banner shows
  `SplitPlay session : N`, `Output Name`, `Adapter LUID` and `Device Description` for every capture start.
- **Mode.** The display belongs to the session. On a Moonlight launch (or resume with no stream running) the worker
  sends `{"type":"event","name":"mode_requested","data":{"width":W,"height":H,"fps":F}}` and waits up to 3 s for
  the desktop size to change (any change ends the wait, the seat may round it). The service forwards it to the seat
  as `set_mode`. If the size does not change, the stream starts at the current size (scaled to the client's).
- **Cursor.** The RDP session has no hardware cursor in the captured image; the DDA pointer shape (colour,
  monochrome and masked) is blended into every frame, as upstream does. Moonlight's "show cursor" toggle works.
- **Input.** Keyboard, mouse (absolute and relative), scroll, pen and touch go through `SendInput`/
  `InjectSyntheticPointerInput` into session N. Keys are sent as scan codes, so the session's keyboard layout (the
  desk user's, e.g. Russian) maps them; text from Moonlight's keyboard uses `KEYEVENTF_UNICODE`.
- **Gamepads.** Moonlight controllers become ViGEm virtual pads through ViGEmBus. The service sets `gamepad=ds4`:
  XInput hands an Xbox 360 pad's button state only to the console session's foreground window (rumble still arrives),
  so an emulated X360 pad does nothing inside a desk, while a DualShock 4 is a HID device that the desk's games read
  directly or through Steam Input. A desk can switch back to Xbox 360 in its advanced settings. The SplitPlay installer bundles ViGEmBus and installs it
  when it is missing (client Windows only; ViGEmBus 1.22 refuses Windows Server). SplitPlay stays on ViGEm (no own pad driver:
  without an EV certificate it would need Windows test mode). ViGEm pads are global,
  so every session, the console included, sees every desk's pads. Without ViGEmBus the worker logs "ViGEmBus is not
  installed or running" at start-up and reports no gamepads; everything else, the self-test included, is unchanged.
- **Audio.** The session's default render endpoint is "Remote Audio" (the seat announces rdpsnd); the worker captures
  it through WASAPI loopback and follows default-device changes. It never installs Steam Streaming Speakers and never
  changes the default endpoint. At start-up the log lists `active render endpoint(s) in session N` and marks the
  default.
- **Apps.** A new desk's `apps.json` contains only "Desktop".

### Self-test

`splitplay-worker.exe --desk-dir <dir> --self-test 10`, started like the worker (SYSTEM in session N), runs the
normal start-up (encoder probe included) and then for 10 s captures the display through the chosen encoder's
capture path, moves the mouse with `SendInput` and checks the cursor follows, and reads 0.5 s of the default endpoint
through WASAPI loopback. It writes `<dir>/self-test.json` and exits 0 when frames were captured:

```json
{"version":"0.1.0","session":2,"encoder":"nvenc","capture_memory":"gpu",
 "capture":{"frames":312,"timeouts":3,"reinits":0,"width":1920,"height":1080},
 "input":{"moves":100,"cursor_moved":true},
 "audio":{"ok":true,"endpoint":"{0.0.0.00000000}.{...}","packets":100},"ok":true}
```

### In-session CI test

[`scripts/session-test.ps1`](scripts/session-test.ps1) (job `worker-in-rdp-session`, Windows Server 2022, no GPU)
enables RDP and the Windows Audio service, creates a local user in Users + Remote Desktop Users, logs it on over
loopback RDP (`127.0.0.2`, NLA, audio channel on) with FreeRDP from MSYS2 standing in for splitplay-seat, and starts
the worker as SYSTEM with `TokenSessionId` = N through a SYSTEM scheduled task running
[`scripts/session-launch.ps1`](scripts/session-launch.ps1). It checks:

1. a probe child runs as `NT AUTHORITY\SYSTEM` in session N;
2. `--self-test`: the session's output was duplicated (log banner and `Desktop resolution`), frames were captured
   (software encoder, Microsoft Basic Render Driver), the cursor followed `SendInput`; the session's default render
   endpoint is Remote Audio and WASAPI loopback delivered packets from it (`-RequireAudio`);
3. control IPC: `hello.session` = N, `ready` after the encoder probe found the session's output, libdisplaydevice
   disabled, `shutdown` gives exit code 0.

Logs (worker, FreeRDP) are uploaded as `worker-session-test-logs`.

Control IPC behaviour (protocol 1):

- `hello` is sent right after start-up (before encoder probing), then the worker waits up to 10 s for `welcome`.
  The initial profile is logged and its stream settings are applied (*Stream profile* below), so the encoder probe
  already uses a DPB size the profile sets.
- `heartbeat` every 1000 ms from `welcome` on, with `state` `starting` until `ready`, then `idle` or `streaming`.
  `metrics.clients` is the number of active streams; `metrics` has the live numbers of the last second and
  `sessions` lists the streams (definitions and sources in ipc.md).
- `event client_connected` / `client_disconnected` when a Moonlight stream is up (control stream connected) and when
  it ends. The heartbeat thread sends them right before the heartbeat that reflects them, so a heartbeat sent after
  the event always agrees with it; no other thread writes them to the pipe.
- `ready` (with `ports.base`) once the HTTP base port accepts connections on loopback.
- `event mode_requested` on a Moonlight launch, see above.
- `shutdown` is answered and the worker exits with code 0. `pair`, `list_clients` and `unpair` are served;
  `set_profile` applies the stream settings and answers which apply now, from the next stream and not at all;
  `send_sas` answers `unsupported`; other requests answer `unknown_type`.
- Exit codes: 0 after `shutdown`, 2 for an invalid command line, 3 when the handshake fails or the pipe breaks.

### Stream profile (patch 0013)

The worker applies four fields of the Profile object ([`ipc.md`](../docs/contracts/ipc.md)) from `welcome` and every
`set_profile`, all or nothing: an absent field keeps its value, `null` restores the default, a value of the wrong
type or out of range rejects `set_profile` with `bad_request`.

| Field | Range | Replaces upstream's | Takes effect |
|---|---|---|---|
| `fec_percent` | 0-255 | `fec_percentage` (default 20) | next frame; the encoder's bitrate allowance for FEC on the next stream |
| `min_fps` | 1-1000, capped at the stream's fps | `minimum_fps_target` (default half the stream's fps) | next frame |
| `pacing_mbps` | 1-100000 | fixed 80 % of 1 Gbit/s (`ratecontrol_packets_in_1ms`, stream.cpp) | next frame |
| `dpb_frames` | 1-16 (AV1 at most 8), 0 = codec default | 5 (H.264, HEVC) and 8 (AV1) reference frames | next stream (or encoder reinit) |

`dpb_frames` only applies when the client leaves the reference frame count to the host, which Moonlight does for
decoders that support reference frame invalidation (`maxNumReferenceFrames=0`); other decoders keep one reference.
Upstream's RFI path is unchanged: a loss of fewer frames than the DPB holds is answered with
`nvEncInvalidateRefFrames`, a larger one with an IDR frame, so 16 frames cover 133 ms at 120 fps (5 cover 42 ms).
It is opt-in (the service sends 0): more references than the codec level allows for the resolution (H.264 4K at
level 5.x: 5; HEVC 4K at level 5.x and 1080p at 4.1: 6) make NVENC signal a higher level, which a client decoder may
refuse, and every reference costs a reconstructed surface of VRAM (about 200 MB per desk at 4K with 16). If
`nvEncInitializeEncoder` refuses the DPB size the encoder is created again with the codec default (warning in the
log); the encoder line in the log shows `dpb=N`. The profile wins over `fec_percentage` and `minimum_fps_target` passed as
`name=value`. `fps`, size, bitrate and codec stay Moonlight's choice; `gpu_priority`, `gpu_luid` and `cpu_set` are
not applied yet.

### Scheduling (patch 0012)

At start-up the worker logs one line, for example `SplitPlay: scheduling: power throttling off (execution speed,
timer resolution), priority class above normal (high while streaming), MMCSS "Games" for ...`, and one
`... runs under MMCSS "Games", priority normal` line for each streaming thread.

- **Power throttling.** `SetProcessInformation(ProcessPowerThrottling)` with `EXECUTION_SPEED` and
  `IGNORE_TIMER_RESOLUTION` in the control mask and nothing in the state mask: no EcoQoS, and the 0.5 ms timer
  resolution upstream requests while streaming (`NtSetTimerResolution`, `timeBeginPeriod(1)` as fallback) is honoured
  although the worker never has a visible window. The pacing sleeps (capture slots, send pacing) already use
  upstream's `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` timer.
- **Priority class.** HIGH while streaming (upstream), ABOVE_NORMAL instead of NORMAL when idle: an idle desk does
  almost nothing, but its heartbeats and the next stream's start-up must not queue behind other desks' games.
  HIGH keeps the threads that are not under MMCSS (audio send, RTSP) above every normal-class game thread except
  TIME_CRITICAL. REALTIME is never used.
- **MMCSS.** `video::capture`, the encode thread, `video::capture_sync`, `stream::videoBroadcast` and
  `stream::controlBroadcast` (receives input) join the task "Games" with `AVRT_PRIORITY_NORMAL`. The default task
  definitions put "Games" (task priority 2) and "Audio" (task priority 6) in scheduling category Medium (real-time
  range 16-22, above every normal-class game thread, at most 15) and "Pro Audio" (Sunshine's audio capture) in High.
  "Games" at normal priority is the most modest registration that lifts these threads above the games and asks for
  nothing above the audio engine's "Audio" task. MMCSS keeps a share of each period for other threads
  (`SystemResponsiveness`, 20 %): a registered thread that overruns it drops to the Exhausted category (1-7), below
  normal threads, until the next period, so a busy worker cannot starve the PC; whether that ever happens with
  several desks is a lab item.
- **Input injection** runs on Sunshine's single task-pool thread, which also runs every other pool task, so it is not
  registered with MMCSS: it runs at the process class (HIGH while streaming, base 13, above normal-class game threads
  except TIME_CRITICAL ones). A dedicated input thread under MMCSS is a follow-up.

### NVIDIA driver settings (patch 0015)

With an NVIDIA driver, every worker start (and every stream start) checks two driver settings through NvAPI DRS, as
upstream Sunshine does:

| Setting | Where | Option (default) | Undone |
|---|---|---|---|
| `PREFERRED_PSTATE` = prefer maximum performance | application profile "SplitPlay worker" for `splitplay-worker.exe` | `nvenc_latency_over_power` (on; `disabled` removes it) | the profile is deleted by `--restore-nvprefs-undo` |
| `OGL_CPL_PREFER_DXPRESENT` = prefer layered on DXGI swapchain (Vulkan/OpenGL present method) | **global** base profile | `nvenc_opengl_vulkan_on_dxgi` (on; `disabled` leaves it alone) | the previous value is written to `%ProgramData%\Sunshine\nvprefs_undo.json` before the change and restored from it |

The global value is restored when the worker that changed it exits, at the next worker start when a worker was killed
before it could restore it, and by `splitplay-worker.exe --restore-nvprefs-undo`, which also deletes the application
profile. The SplitPlay uninstaller runs that command once the service and every worker have stopped, so uninstalling
leaves the driver database as it was found (a value someone changed after the worker set it is left alone, as
upstream does). The undo file is upstream's, so a co-installed Sunshine and the workers keep one record of the original
value.

All desks share both settings. The first worker to change the global value owns the undo file and restores the value
when it exits, even if other desks still run; another desk sets it again (with a new undo file) at its next stream
start. A desk with `nvenc_latency_over_power=disabled` removes the profile's setting for every desk until another desk
starts. Both settle once the service owns driver profiles (below).

## Not done yet

- Frame tracing, the IDD capture ring and the input ring (the contract headers are already on the include path);
  the profile's `gpu_priority`, `gpu_luid` and `cpu_set`.
- Per-session gamepads: ViGEm pads are visible to every session; SplitPlay's isolated pad driver comes in Stage 4.
- Upstream's NVIDIA profile tweaks (`nvprefs`) still change the global driver profile from each worker (patch 0015);
  with several workers they should move to the service.
- mDNS: every worker publishes itself; Moonlight's automatic discovery is not tested with several desks yet
  (adding `HOST:port` by hand always works).
