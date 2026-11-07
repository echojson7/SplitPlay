# Configuration file

Path: `%ProgramData%\SplitPlay\config.json`. UTF-8 JSON, `camelCase`. Written atomically (temp file + `MoveFileEx` with replace).
The service validates it on load; a file with a newer `schemaVersion` than the service understands is refused, an older one is migrated in memory and rewritten.
Secrets are never stored here (see ARCHITECTURE.md §6).

```json
{
  "schemaVersion": 1,
  "host": {
    "webPort": 38299,
    "lanAccess": false,
    "basePortStart": 48089,
    "basePortStride": 100,
    "composeIntervalMs": 0,
    "logLevel": "info",
    "keepAwake": true,
    "nvidiaProfiles": true,
    "deskCores": "off"
  },
  "desks": [
    {
      "id": "desk-1",
      "name": "Skyrim box",
      "user": "splitplay-desk1",
      "basePort": 48089,
      "autostart": true,
      "userManaged": true,
      "gpu": "auto",
      "disableBluetoothUserService": true,
      "steamIsolation": true,
      "gameFpsLimit": 60,
      "workerSettings": { "min_log_level": "warning" },
      "profile": {
        "fps": 120,
        "width": 1920,
        "height": 1080,
        "bitrateKbps": 60000,
        "codec": "hevc",
        "fecPercent": 20,
        "minFps": 15,
        "dpbFrames": 16,
        "pacingMbps": 800,
        "captureMode": "arrival",
        "input": "rdp",
        "adaptiveStream": true,
        "gpuPriority": "high"
      }
    }
  ]
}
```

Rules:

- `id` unique, `[a-z0-9-]{1,32}`; `user` unique, a local Windows account name. It may not be the user signed in at the
  console.
- `userManaged` is set by the service. `true`: SplitPlay created the account and owns its password (random, never
  expires, rotated when the stored secret is lost, removable with `deleteUser=true`). `false`: an existing account the
  owner picked; SplitPlay stores the password the owner typed (verified with `LogonUser`) and never changes the account.
  Desk passwords live DPAPI-protected in `secrets\desks\<id>.bin`, never in this file.
- `disableBluetoothUserService` (default `true`) stops the per-user `BluetoothUserService_*` instance in the desk
  session so it does not compete with the console user's Bluetooth devices (issue #3).
- `hideFromLogon` (optional) keeps the desk's account off the Windows sign-in screen: DWORD `<user>` = `0` under
  `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList`, applied when the desk is
  provisioned or the setting changes. Left out, it is on for accounts SplitPlay created and off for any other: an
  account SplitPlay did not create is hidden only when the owner sets `true` for that desk. The value before is
  recorded in `desks\<id>\logon-hidden.json` and comes back when the setting goes off, the user changes, the desk is
  deleted or SplitPlay is uninstalled (a value someone changed since is left alone). A request that leaves it out
  keeps the current value unless the user changes.
- `gameFpsLimit` (optional; a request that leaves it out keeps the current value): the NVIDIA driver's frame rate
  limit ("Max Frame Rate", `FRL_FPS`) for the games the service finds in this desk. Left out or `null`: Auto, the
  frame rate the desk's last Moonlight client asked for (the desk's `profile.fps` before any client connected); `0`:
  off; `10`–`1000`: that many FPS. Changes apply while the desk runs. How it works:
  - While a desk runs with the limit on, the service reads the GPU load per process every 5 s (the counters behind
    `/metrics`). A process in the desk's session that renders at 10 % or more in 3 reads within a minute is a game unless it lives in
    `%WINDIR%`, is SplitPlay's, or is a known non-game (launchers, browsers, chat, overlays, players). Games are kept
    in `games.json` in the data folder, with the desks they ran in and each desk's last client frame rate.
  - Each game gets the largest limit of its desks, in the driver profile the driver applies to its full path (often
    one of NVIDIA's game profiles), else in a profile "SplitPlay game <exe>" that SplitPlay creates. A lookup that
    fails keeps the game in the profile it had; failed games are tried again after a minute. The previous
    value is recorded in `nvidia-profiles.json` (see `host.nvidiaProfiles`) and comes back when the limit goes;
    NVIDIA's predefined values are restored by deleting the setting. A limit the owner set for that game in NVIDIA
    Control Panel (any value but 0) is left alone, and so is a global Max Frame Rate the owner set (SplitPlay then
    sets nothing per game). A "SplitPlay game" profile that holds the owner's own limit is left to the owner.
  - The driver reads its profiles when a game starts: a limit applies from the game's next start, and it holds for
    that executable on the whole PC, the console included. Limits stay while SplitPlay is installed and go with
    `host.nvidiaProfiles=false` or on uninstall. `GET /games` (web-api.md) lists the games; one can be excluded.
    In the web UI: desk page → Games.
- `steamIsolation` (default `true`; a request that leaves it out keeps the current value) lets the desk user's Steam
  run next to the console user's Steam (Steam isolation research, 2026-10-28). Only the desk user's hive is changed, never the
  console user's and never machine-wide: `HKU\<sid>\Environment\steam_master_ipc_name_override` = `SplitPlay_<id>`
  (REG_SZ, `-` in the id becomes `_`); Steam's autostart `...\CurrentVersion\Run\Steam`, when present, gets
  `-master_ipc_name_override SplitPlay_<id>` after `steam.exe`; a per-user `steam://` handler
  (`Software\Classes\steam`, i.e. `UsrClass.dat`) mirrors the machine one plus the flag. Values are written before
  logon with the profile mounted from disk when it exists, otherwise at the first logon (then the session is logged
  off once, together with the keyboard-layout relogon, so explorer sees the variable). Each value SplitPlay writes is
  recorded with its previous value in `desks\<id>\steam-isolation.json`; turning the option off, changing `user` or
  deleting the desk restores them (a value changed since is left alone). Existing accounts (`userManaged: false`) are
  included. With Steam installed, the desk's `apps.json` also gets a Moonlight app "Steam":
  `"<InstallPath>\steam.exe" -master_ipc_name_override SplitPlay_<id> -gamepadui` (an owner's own "Steam" entry
  without the flag is kept). Changing the option requires a stopped desk.
- `workerSettings`: extra Sunshine `name=value` settings passed to the worker after the service defaults (ipc.md);
  names `[A-Za-z0-9_.-]{1,64}`, values up to 1024 characters of printable ASCII without `"` and `\`, or letters and
  digits of other scripts (Sunshine reads its settings from the ANSI `argv`, where "best fit" turns look-alikes such as
  U+FF02 into a quote that could end the argument; the desk `name` loses such characters). Only these may be set (Sunshine's `src/config.cpp` at
  the commit in `worker/upstream.lock`), because the worker runs as SYSTEM:
  - encoder and codec: `encoder`, `hevc_mode`, `av1_mode`, `qp`, `min_threads`, `sw_preset`, `sw_tune`, `nvenc_preset`,
    `nvenc_vbv_increase`, `nvenc_spatial_aq`, `nvenc_twopass`, `nvenc_h264_cavlc`, `nvenc_split_encode`,
    `nvenc_realtime_hags`, `qsv_preset`, `qsv_coder`, `qsv_slow_hevc`, `amd_quality`,
    `amd_rc`, `amd_coder`, `amd_usage`, `amd_preanalysis`, `amd_vbaq`, `amd_enforce_hrd`;
  - rate control and frame rate: `max_bitrate`, `minimum_fps_target`, `fec_percentage`;
  - audio: `stream_audio`;
  - input and gamepads: `gamepad`, `ds4_back_as_touchpad_click`, `motion_as_ds4`, `touchpad_as_ds4`, `mouse`,
    `keyboard`, `controller`, `always_send_scancodes`, `high_resolution_scrolling`, `native_pen_touch`, `keybindings`,
    `key_rightalt_to_key_win`, `back_button_timeout`, `key_repeat_frequency`, `key_repeat_delay`;
  - `sunshine_name`, `min_log_level`.

  Anything else (commands, files, certificates, network and UPnP, encryption modes, driver installs, capture, display
  and audio devices, which follow the desk's session) or an unsafe value gets a 400 naming the setting; a `config.json` that has them
  loads without them (logged as a warning) and the worker launch drops them in any case. `port` is set by the service
  from `basePort`, as are the defaults the service passes itself.
- `name` (optional, up to 64 characters) is what Moonlight shows: the worker gets `sunshine_name=<PC name>-<name>`
  (`<PC name>-<id>` without a name) and names its mDNS instance after it. Moonlight tells hosts apart by their
  `uniqueid`, so renaming a desk (only while it is stopped) keeps its paired clients; they show the new name at the
  next poll. A desk's `workerSettings.sunshine_name` overrides it. When two desks would get the same mDNS instance name
  (equal up to case, punctuation or the 63-byte cut), each of them gets `<PC name>-<id>` instead.
- `basePort` unique; if omitted the service picks `basePortStart + stride * k` with the lowest free `k ≥ 0`. A desk occupies `basePort-5 … basePort+21`.
  The service creates the desk's inbound firewall rules for those ports (`SplitPlay desk <id> (TCP|UDP)`, the worker's
  executable, private and domain profiles) and creates them again when the desk starts, or a worker restarts, and they
  are missing, disabled or different. Before a worker starts the ports must be free (see `lastError` in web-api.md).
- `gpu`: `"auto"` or an adapter LUID string `"0x…"`.
- `profile` may also carry `gpuLuid` and `cpuSet` (array of logical processor numbers 0–2047, `group * 64 + number`;
  passed to the worker as in ipc.md). A `cpuSet` keeps the desk's processes on those processors whatever
  `host.deskCores` says; numbers this PC does not have are dropped, and a set with none left counts as not set.
- Desk port ranges may not overlap each other or contain `host.webPort`; `basePortStride` must be at least 27.
- `profile.fps` 10–500, `codec` one of `h264`, `hevc`, `av1`; `gpuPriority` `normal`, `high`, `realtime`.
- Stream settings the worker applies (ipc.md, *Profile object*); a running desk takes a change at once except
  `dpbFrames` (next stream). Left out, the service sends its defaults: `fecPercent` 0–100, default 20;
  `minFps` 1–`fps`, default 20 (capped at `fps`: an unchanged desktop is encoded 20 times a second instead of at half
  the stream rate); `dpbFrames` 1–16, not set by default (the service sends 0, the codec default of 5 or 8 for AV1;
  a larger DPB repairs longer losses with reference frame invalidation instead of an IDR frame, but more references
  than the codec level allows make NVENC signal a higher level that a client decoder may refuse, and each costs VRAM,
  about 200 MB per desk at 4K with 16, so it is opt-in and not in the web UI); `pacingMbps` 1–100000, default 800 (Sunshine's fixed value; lower it when a
  Wi-Fi client loses frames on large frames, raise it on 2.5 GbE and faster). In the web UI: Дополнительно → FEC,
  Минимальный FPS, Скорость отправки.
- `profile.captureMode` (live): `slot` (default, Sunshine's loop: the newest frame at the stream's next frame slot,
  half a stream interval later on average) or `arrival` (each frame as soon as DWM composes it, at most `fps` on
  average; worker/README.md, *Capture on arrival*). The default stays `slot` until the lab measures `arrival`. In the
  web UI: Дополнительно → Захват кадра.
- `profile.input` (live): `windows` (default, SendInput inside the session) or `rdp` (keyboard and mouse go through the
  seat's RDP connection as a real remote keyboard and mouse: no injection flag, UAC prompts and Ctrl+Alt+Del work;
  relative mouse motion stays on SendInput when the RDP server has no relative mouse PDUs; ipc.md, *RDP input*). The
  default stays `windows` until the lab tries `rdp` with games (lab E). In the web UI: Дополнительно → Клавиатура и
  мышь; the desk page then shows a «Ввод» row with the path in use (`metrics.input`, web-api.md).
- `profile.adaptiveStream` (default `true`, «Адаптивный поток»; worker patches 0023 and 0025): while a client streams
  the service reads the packet loss the client reports on every heartbeat and
  - sets FEC between 5 and 50 %: the worst FEC block of the last 15 s plus a quarter and two points, rounded up to
    5; up at once, down one step every 10 s, so a clean link goes from 20 % to 5 % in 30 s. A desk with `fecPercent`
    keeps that share;
  - caps the stream's video traffic (encoder bitrate with FEC included, native NVENC only) to 3/4 of what it sends
    after two heartbeats in a row with 5 % of packets lost, unrecoverable frames with 1 %, or a round trip 50 ms and
    twice above its lowest; not below a fifth of the client's bitrate or 2 Mbit/s. After 10 clean seconds the cap rises
    by 5 % of the client's bitrate per second and is lifted when it gets there. A new stream starts uncapped;
  - shares 80 % of a network adapter's link speed between the desks whose clients it reaches directly (same subnet),
    in proportion to their clients' bitrates, when together they ask for more.
  `false`: the desk's `fecPercent` (or 20 %) and the client's bitrate, as before. In the web UI: Поток → Адаптивный
  поток.
- `host.composeIntervalMs` is written to `DWMFRAMEINTERVAL` by the service (2 = 500 Hz, 4 = 250 Hz), 1–100. `0`
  (default, «Частота композиции: Авто»): the service composes at least twice per frame of the fastest desk and never
  slower than 250 Hz: 4 ms up to 125 fps, 3 ms for 126–166 fps (144, 165), 2 ms from 167 fps (240). The fastest desk is the highest
  `profile.fps` or the highest frame rate a Moonlight client asked for since the service started. DWM reads the value
  when a session logs on, so a change applies to desk sessions that log on afterwards. `GET /metrics` reports the
  resolved rate as `host.composeHz`; each desk's measured rate is `captureFps`.
- `host.keepAwake` (default `true`; left out = `true`): while a Moonlight client streams from any desk the service holds
  a system-required power request (`powercfg /requests` lists it), so Windows does not sleep on its own. Sleeping by
  hand still works: before sleep every worker is shut down and its desk waits (state `Recovering`, not a failure, the
  seat and session are kept); after resume each paused desk gets a new worker (NVENC and capture are recreated).
  In the web UI: Settings → Power.
- `host.nvidiaProfiles` (default `true`; left out = `true`): the service manages the NVIDIA driver settings through
  NvAPI (the workers leave them alone since worker patch 0020): the application profile "SplitPlay worker" with
  "prefer maximum performance" for `splitplay-worker.exe` (kept while SplitPlay is installed), and, while the service
  runs, the global "Vulkan/OpenGL present method: prefer layered on DXGI swapchain". Before its first change the
  service records the previous value in `nvidia-profiles.json` in the data folder; it gives the global value back when it
  stops and everything on uninstall, unless the owner changed the value since. `false` gives everything back at once
  and leaves the driver alone. Replaces the per-desk `nvenc_latency_over_power`, which is no longer accepted (one
  shared driver database cannot hold a value per desk).
- `host.deskCores` (default `"off"`; left out = `"off"`): which processor cores the desk user's processes (Explorer,
  Steam, games and everything they start) prefer. `"off"`: wherever Windows puts them. `"auto"`: on a CPU with
  several L3 caches (Ryzen with two or more CCDs) the desks take the caches in turn in config order (stopped desks
  too, so a desk keeps its CCD), the largest (3D V-Cache) first, round-robin when there are more desks than caches; on
  an Intel hybrid CPU with one L3 every desk gets the P-cores; on any other CPU nothing changes. The console and
  SYSTEM processes (DWM, worker, seat) are never restricted. The service gives the desk user's processes in the
  session default CPU Sets (`SetProcessDefaultCpuSets`): soft, so a game that pins its own threads still can, and
  nothing is left behind when lifted. CPU Sets are not inherited: the service looks for new processes every 2 s
  (`Supervisor:CpuSweepMs`). A change applies to running desks within one sweep; back to `"off"` clears the sets.
  Protected processes cannot be opened and are left alone. `GET /host/cpu` (web-api.md) shows the CPU and each
  desk's processors. In the web UI: Settings → Processor → Cores for desks.
