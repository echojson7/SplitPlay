# Control IPC: service ↔ worker, service ↔ seat

Protocol version: **1**.

## Transport

- The **service** creates one named pipe server per child process before starting it:
  - worker: `\\.\pipe\splitplay\worker-<desk-id>`
  - seat: `\\.\pipe\splitplay\seat-<desk-id>`
- The pipe DACL allows only `SYSTEM` (children run as SYSTEM, the worker with `TokenSessionId` set to its desk session).
- The child receives the pipe name on its command line: `--pipe \\.\pipe\splitplay\worker-<desk-id> --desk <desk-id>`.
- Byte mode, UTF-8, one JSON object per line (`\n`-terminated), at most 64 KiB per line.
- Every message has `"type"`. Requests that expect an answer carry `"id"` (integer, unique per sender); the answer has `"type":"result"`, the same `"id"`, and either `"ok":true` with optional `"data"` or `"ok":false` with `"error":{"code":"...","message":"..."}`.
- Unknown message types are answered with error code `unknown_type` (if they carry an id) and otherwise ignored. Unknown fields are ignored.

## Child command lines

```
splitplay-worker.exe     --desk-dir %ProgramData%\SplitPlay\desks\<id> --pipe \\.\pipe\splitplay\worker-<id> --desk <id> port=<basePort> [name=value ...]
splitplay-stubworker.exe --pipe \\.\pipe\splitplay\worker-<id> --desk <id>
splitplay-seat.exe       --pipe \\.\pipe\splitplay\seat-<id> --desk <id>
```

- `--desk-dir` holds the worker's per-desk state (`sunshine.conf`, `sunshine_state.json` with pairings and unique id,
  `credentials\`, `apps.json`, `sunshine.log`); trailing `name=value` pairs are Sunshine settings and override `sunshine.conf`.
  The worker resolves `assets\` next to its executable. See `worker/README.md`.
- The service passes the worker's recommended settings by default (`sunshine_name=<HOST>-<id>`, `min_log_level=info`,
  `nvenc_preset=1`, `nvenc_twopass=disabled`, `hevc_mode=0`, `av1_mode=0`); a desk's `workerSettings` (config.md)
  override or extend them. `port` is always set by the service.
- The worker runs as SYSTEM with `TokenSessionId` = the desk session, `lpDesktop` `winsta0\default`,
  `CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT`, in the desk's job object. Diagnostics only:
  `splitplay-worker.exe --desk-dir <dir> --self-test <seconds>` (not with `--pipe`) checks capture, input and audio in
  the session and writes `<desk-dir>\self-test.json`. The full settings list is in worker/README.md. The seat runs as SYSTEM in session 0.
- Exit codes: 0 after `shutdown`, 2 invalid command line, 3 handshake failed or pipe lost, 4 (seat) gave up reconnecting
  the RDP session; anything else is a crash.

## Lifecycle

1. Child connects and sends `hello`.
2. Service answers with `welcome` carrying the initial profile.
3. Child sends `ready` once it is fully up (worker: ports listening; seat: RDP session logged on).
4. Child sends `heartbeat` every 1000 ms, starting right after `welcome` (heartbeats may arrive before `ready`). The service treats 3 missed heartbeats as a hang, kills the child's job object and restarts it with backoff.
5. Service sends `shutdown`; the child answers `result` and exits within 5 s, otherwise it is killed.
6. If the pipe breaks, the child exits (it never runs unsupervised).

## Messages

### child → service

```json
{"type":"hello","protocol":1,"role":"worker","desk":"desk-1","pid":1234,"version":"0.1.0","session":3}
{"type":"ready","ports":{"base":48089}}
{"type":"heartbeat","seq":17,"state":"idle","metrics":{"fps":0,"bitrate_kbps":0,"encode_ms_p50":0,"encode_ms_p99":0,"loss_pct":0,"clients":0}}
{"type":"log","level":"info","message":"..."}
{"type":"event","name":"client_connected","data":{"client":"Living room PC"}}
{"type":"event","name":"mode_requested","data":{"width":2560,"height":1440,"fps":120}}
{"type":"event","name":"session_lost","data":{"reason":"..."}}
{"type":"event","name":"session_restored","data":{"session":3}}
```

Events by role:

- worker `mode_requested`: a Moonlight client launched with this mode. The service forwards it to the seat as
  `set_mode`. The worker waits up to 3 s for the desktop size to change and carries on either way.
- seat `session_lost`: the RDP connection dropped; the seat is reconnecting. The service keeps the worker running.
- seat `session_restored`: reconnected. If the session id changed, the service restarts the worker in the new session.

`role` is `worker`, `seat` or `stubworker`. The worker pipe accepts `worker` and `stubworker`; the seat pipe accepts `seat`.

`ready` payloads by role:

| Role | Fields |
|---|---|
| `worker` | `ports.base`: the HTTP port Moonlight connects to (the other ports follow from it, see config.md) |
| `stubworker` | none (it opens no network ports) |
| `seat` | `input_ring` (section name, input_ring.h), `connector` (`null` or `freerdp`), `session` (Windows session id). Sent only once the desk user is logged on and the session is active |

`state` is a free-form short string for the UI (`idle`, `streaming`, `connecting`, `logged_on`).
`metrics` keys are optional; the service passes unknown keys through to the Web API unchanged.

### service → child

```json
{"type":"welcome","protocol":1,"profile":{ ... }}
{"type":"welcome","protocol":1,"profile":{ ... },"rdp":{"host":"127.0.0.2","port":3389,"user":"splitplay-desk1","domain":".","password":"…","width":1920,"height":1080}}
{"type":"set_mode","id":4,"width":2560,"height":1440}
{"type":"set_profile","id":5,"profile":{ ... }}
{"type":"pair","id":6,"pin":"1234","name":"Laptop"}
{"type":"list_clients","id":7}
{"type":"unpair","id":8,"client":"<uuid>"}
{"type":"send_sas","id":9}
{"type":"shutdown","id":10}
```

Answers with data:

- `list_clients` → `{"clients":[{"uuid":"…","name":"Laptop","paired_at":"2026-10-28T12:00:00Z"}]}`. `paired_at` is optional;
  the service also accepts `id` for `uuid` and a bare array.
- `pair` fails with `pair_failed` when no Moonlight client is waiting for a PIN or the PIN does not match; `unpair` fails with
  `not_found` for an unknown uuid.

`pair`, `list_clients` and `unpair` are worker-only; `send_sas` and `set_mode` are seat-only. A child that does not support a request answers `unsupported`.

Seat specifics:

- `welcome.rdp` is the loopback RDP target. `port` comes from `RDP-Tcp\PortNumber` in the registry. The password
  never appears in logs, on command lines or in the environment. The seat connects with FreeRDP 3, NLA on, and the
  audio playback channel enabled (samples discarded) so that the session gets a Remote Audio endpoint.
- `set_mode` resizes the session through the RDP display-control channel; `unsupported` when the channel is not
  available (the desk then reports `resolutionPendingRestart`).
- `shutdown` logs the session off (`WTSLogoffSession`) and disconnects.

### Profile object

Tuning parameters applied live (see ARCHITECTURE.md §9). All fields optional; absent = keep current.

```json
{
  "fps": 120, "width": 1920, "height": 1080,
  "bitrate_kbps": 60000, "codec": "hevc",
  "fec_percent": 20, "min_fps": 15, "dpb_frames": 16,
  "pacing_mbps": 800,
  "gpu_priority": "high",
  "gpu_luid": "0x0000000000012345",
  "cpu_set": [4,5,6,7]
}
```
