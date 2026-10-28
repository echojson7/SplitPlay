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
    "logLevel": "info"
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
- `workerSettings`: extra Sunshine `name=value` settings passed to the worker after the service defaults (ipc.md);
  names `[A-Za-z0-9_.-]{1,64}`, values up to 1024 characters; `port` is reserved.
- `basePort` unique; if omitted the service picks `basePortStart + stride * k` with the lowest free `k ≥ 0`. A desk occupies `basePort-5 … basePort+21`.
- `gpu`: `"auto"` or an adapter LUID string `"0x…"`.
- `profile` may also carry `gpuLuid` and `cpuSet` (array of logical CPU indices), passed to the worker as in ipc.md.
- Desk port ranges may not overlap each other or contain `host.webPort`; `basePortStride` must be at least 27.
- `profile.fps` 10–500, `codec` one of `h264`, `hevc`, `av1`; `gpuPriority` `normal`, `high`, `realtime`.
- `host.composeIntervalMs` is written to `DWMFRAMEINTERVAL` by the service (2 = 500 Hz, 4 = 250 Hz).
