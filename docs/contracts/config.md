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
    "composeIntervalMs": 4,
    "logLevel": "info",
    "keepAwake": true
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
  names `[A-Za-z0-9_.-]{1,64}`, values up to 1024 characters. Only these may be set (Sunshine's `src/config.cpp` at
  the commit in `worker/upstream.lock`), because the worker runs as SYSTEM:
  - encoder and codec: `encoder`, `hevc_mode`, `av1_mode`, `qp`, `min_threads`, `sw_preset`, `sw_tune`, `nvenc_preset`,
    `nvenc_vbv_increase`, `nvenc_spatial_aq`, `nvenc_twopass`, `nvenc_h264_cavlc`, `nvenc_split_encode`,
    `nvenc_realtime_hags`, `nvenc_latency_over_power`, `qsv_preset`, `qsv_coder`, `qsv_slow_hevc`, `amd_quality`,
    `amd_rc`, `amd_coder`, `amd_usage`, `amd_preanalysis`, `amd_vbaq`, `amd_enforce_hrd`;
  - rate control and frame rate: `max_bitrate`, `minimum_fps_target`, `fec_percentage`;
  - audio: `stream_audio`;
  - input and gamepads: `gamepad`, `ds4_back_as_touchpad_click`, `motion_as_ds4`, `touchpad_as_ds4`, `mouse`,
    `keyboard`, `controller`, `always_send_scancodes`, `high_resolution_scrolling`, `native_pen_touch`, `keybindings`,
    `key_rightalt_to_key_win`, `back_button_timeout`, `key_repeat_frequency`, `key_repeat_delay`;
  - `sunshine_name`, `min_log_level`.

  Anything else (commands, files, certificates, network and UPnP, encryption modes, driver installs, capture, display
  and audio devices, which follow the desk's session) gets a 400 naming the setting; a `config.json` that has them
  loads without them (logged as a warning) and the worker launch drops them in any case. `port` is set by the service
  from `basePort`, as are the defaults the service passes itself.
- `name` (optional, up to 64 characters) is what Moonlight shows: the worker gets `sunshine_name=<PC name>-<name>`
  (`<PC name>-<id>` without a name) and names its mDNS instance after it. Moonlight tells hosts apart by their
  `uniqueid`, so renaming a desk (only while it is stopped) keeps its paired clients; they show the new name at the
  next poll. A desk's `workerSettings.sunshine_name` overrides it.
- `basePort` unique; if omitted the service picks `basePortStart + stride * k` with the lowest free `k ≥ 0`. A desk occupies `basePort-5 … basePort+21`.
  The service creates the desk's inbound firewall rules for those ports (`SplitPlay desk <id> (TCP|UDP)`, the worker's
  executable, private and domain profiles) and creates them again when the desk starts, or a worker restarts, and they
  are missing, disabled or different. Before a worker starts the ports must be free (see `lastError` in web-api.md).
- `gpu`: `"auto"` or an adapter LUID string `"0x…"`.
- `profile` may also carry `gpuLuid` and `cpuSet` (array of logical CPU indices), passed to the worker as in ipc.md.
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
- `host.composeIntervalMs` is written to `DWMFRAMEINTERVAL` by the service (2 = 500 Hz, 4 = 250 Hz).
- `host.keepAwake` (default `true`; left out = `true`): while a Moonlight client streams from any desk the service holds
  a system-required power request (`powercfg /requests` lists it), so Windows does not sleep on its own. Sleeping by
  hand still works: before sleep every worker is shut down and its desk waits (state `Recovering`, not a failure, the
  seat and session are kept); after resume each paused desk gets a new worker (NVENC and capture are recreated).
  In the web UI: Settings → Power.
