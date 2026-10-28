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
      "gpu": "auto",
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

- `id` unique, `[a-z0-9-]{1,32}`; `user` unique, a local Windows account name (created by the service if missing).
- `basePort` unique; if omitted the service picks `basePortStart + stride * k` with the lowest free `k ≥ 0`. A desk occupies `basePort-5 … basePort+21`.
- `gpu`: `"auto"` or an adapter LUID string `"0x…"`.
- `profile.fps` 10–500, `codec` one of `h264`, `hevc`, `av1`; `gpuPriority` `normal`, `high`, `realtime`.
- `host.composeIntervalMs` is written to `DWMFRAMEINTERVAL` by the service (2 = 500 Hz, 4 = 250 Hz).
