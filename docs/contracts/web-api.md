# Web API v1

Base URL: `http://127.0.0.1:38299/api/v1` (LAN access, when enabled, is HTTPS on the same port).
JSON in and out, `camelCase` field names. Errors: HTTP 4xx/5xx with `{"error":{"code":"...","message":"..."}}`.

## Authentication

- Requests from loopback carrying a valid token pass. The tray app and CLI obtain a token from the service over
  `\\.\pipe\splitplay\admin` (DACL: Administrators and SYSTEM) and send it as `Authorization: Bearer <token>`.
- The browser UI logs in with the admin password: `POST /auth/login {"password":"..."}` sets an HttpOnly session cookie;
  `POST /auth/logout` clears it. State-changing requests from the browser also need the `X-CSRF-Token` header
  whose value is returned by login and by `GET /auth/session`.
- Sessions use the HttpOnly, `SameSite=Strict` cookie `splitplay_session`. A request that carries the cookie and
  changes state (POST, PUT, DELETE) must send `X-CSRF-Token`, otherwise it gets 403 `csrf_failed`.
- Stage 2 skeleton: the admin token pipe is not implemented yet, so loopback requests are served without a session.
  The service listens on loopback only until LAN access (HTTPS) is implemented.
- Browser checks in front of every `/api` route (403 `forbidden`):
  - a POST, PUT or DELETE with `Sec-Fetch-Site: cross-site`, or with an `Origin` that is not the request's own
    scheme://host:port (`Origin: null` included), is refused: another site's page could otherwise post a form here,
    and it carries no `SameSite=Strict` cookie, so the CSRF check would not apply. Requests without these headers (CLI,
    scripts) and the UI itself (same origin, on loopback or on the LAN host) pass;
  - on a loopback connection the `Host` header must be `localhost` or an IP address (DNS rebinding).

| Request | Body | Answer |
|---|---|---|
| `GET /auth/session` | | `{"setupRequired":false,"authenticated":true,"csrfToken":"…"}` (`csrfToken` only when authenticated) |
| `POST /auth/login` | `{"password":"…"}` | `{"csrfToken":"…"}`; wrong password → 401 `invalid_password` |
| `POST /auth/logout` | | 204 |
| `POST /auth/password` | `{"newPassword":"…","currentPassword":"…"}` (`currentPassword` omitted for the first password; at least 8 characters) | `{"csrfToken":"…"}`, and the browser is logged in |

## Endpoints

| Method | Path | Purpose |
|---|---|---|
| GET | `/health` | `{"status":"ok","version":"0.1.0","uptimeSeconds":12}`; no auth |
| GET | `/auth/session` | current auth state |
| POST | `/auth/login`, `/auth/logout`, `/auth/password` | login, logout, set or change the admin password |
| GET, PUT | `/host` | read or replace the `host` object of config.md; validated and saved; a `webPort` change applies after a service restart; a `lanAccess` change toggles the firewall rule `SplitPlay Web UI` |
| GET | `/host/users` | local accounts that can be a desk user: `[{"name","fullName","isAdmin","isConsoleUser","createdBySplitPlay"}]` (built-in accounts left out) |
| GET | `/host/network` | LAN addresses of the host for Moonlight: `{"addresses":["192.168.1.20"]}`: IPv4 addresses of interfaces that are up, without loopback and link-local (169.254/16), those of interfaces with a default gateway first; empty when none. A desk is added in Moonlight as `<address>:<basePort>` |
| GET | `/host/services` | the Windows services SplitPlay depends on, see [Host services](#host-services) |
| POST | `/host/services/{name}/start`, `/host/services/{name}/stop` | start or stop one of those services; 202 with its entry. `{name}` outside the list → 404 `not_found`; a service that cannot be started or stopped now (already running, disabled, not installed, a driver) → 409 `invalid_state`; the SCM refusing → 500 `internal` with its message |
| GET | `/desks` | array of desks with live state |
| POST | `/desks` | create a desk (body = desk config, see config.md, plus optional `password`: the Windows password of `user`, verified when the account exists, used for a new account otherwise) |
| GET | `/desks/{id}` | one desk with live state |
| PUT | `/desks/{id}` | replace desk config (only while stopped, except `profile`); optional `password` as for POST. A `profile.width/height` change on a running desk is sent to the seat as `set_mode` |
| DELETE | `/desks/{id}` | delete a stopped desk (`?deleteUser=true` also removes the Windows account; 409 `user_not_managed` for an account SplitPlay did not create) |
| POST | `/desks/{id}/start`, `/desks/{id}/stop`, `/desks/{id}/restart` | lifecycle; return 202 with the desk. Starting a desk that is not `Stopped`/`Defined` gives 409 `invalid_state` (a `Faulted` desk must be stopped first); stop is accepted in every state and is a no-op on a stopped desk |
| GET | `/desks/{id}/clients` | array of paired Moonlight clients `{"id","name","pairedAt"?}` (409 `child_unavailable` when the desk is not running) |
| POST | `/desks/{id}/pair` | `{"pin":"1234","name":"Laptop"}` |
| DELETE | `/desks/{id}/clients/{clientId}` | revoke a client |
| GET | `/desks/{id}/client-pack`, `/desks/{id}/client-pack/{windows,linux}` | Moonlight settings recommended for the desk and its launch scripts, see [Client launch pack](#client-launch-pack) |
| GET | `/metrics` | host-level metrics plus per-desk metrics |
| GET | `/logs?component=service&desk=desk-1&tail=200` | array of `{"time","level","component","desk"?,"message"}` |
| GET | `/config/export` | zip download (secrets excluded unless `?includeSecrets=true&password=...`) |
| POST | `/config/import` | zip upload |
| GET | `/ws` | WebSocket: a `desk` event per desk on connect and on every change (including `stream`), `metrics` every 2 s |

## Host services

`GET /host/services` returns an array, in this order, of the services desks rely on:

| `name` | Why |
|---|---|
| `SplitPlay` | this service |
| `TermService` | Remote Desktop Services: every desk is an RDP session |
| `UmRdpService` | Remote Desktop UserMode Port Redirector (UmWrap patches it on Home and Server editions) |
| `ViGEmBus` | gamepad driver (kernel driver service; often `missing`) |
| `Audiosrv` | Windows Audio |
| `Steam Client Service` | only listed when installed |

```json
{"name":"TermService","displayName":"Remote Desktop Services","status":"running","startType":"manual","canStart":false,"canStop":true}
```

- `displayName` is the Windows display name (localized by Windows; the English name when the service is missing).
  The UI shows its own label for each `name`.
- `status`: `running`, `stopped`, `starting`, `stopping` or `missing` (not installed).
- `startType`: `automatic`, `automaticDelayed`, `manual`, `disabled`, `boot` or `system`; `null` when missing.
- `canStart`: stopped, not disabled and not a driver. `canStop`: running, accepts a stop control and not a driver.
- Only these names can be started or stopped; names are matched case-insensitively and URL-encoded
  (`Steam%20Client%20Service`).
- Stop answers 202 at once with `status: "stopping"`; the stop runs in the background and stops running dependents
  first (stopping `TermService` stops `UmRdpService` and ends every desk session, so the UI asks first).
- Stopping `SplitPlay` answers 202, then stops this service through the SCM after 0.5 s: the web UI goes away until
  the service is started again (the tray app offers that).
- When the service starts it also starts `TermService` and `UmRdpService` if they are stopped and not disabled
  (logged; a failure does not stop start-up).

## Desk object (response)

```json
{
  "id": "desk-1",
  "name": "Skyrim box",
  "user": "splitplay-desk1",
  "userManaged": true,
  "hasPassword": true,
  "resolutionPendingRestart": false,
  "steamIsolation": true,
  "state": "Stopped",
  "sessionId": null,
  "basePort": 48089,
  "autostart": true,
  "gpu": "auto",
  "profile": { "fps": 120, "width": 1920, "height": 1080, "bitrateKbps": 60000, "codec": "hevc" },
  "metrics": { "fps": 0, "bitrateKbps": 0, "encodeMsP50": 0, "encodeMsP99": 0, "lossPct": 0, "rttMs": null, "clients": 0 },
  "stream": null,
  "lastError": null
}
```

A streaming desk:

```json
{
  "state": "Streaming",
  "metrics": { "fps": 119.8, "bitrateKbps": 38450, "encodeMsP50": 3.1, "encodeMsP99": 5.4, "lossPct": 0, "rttMs": 4, "clients": 1 },
  "stream": {
    "client": "Living room PC", "clientId": "…", "address": "192.168.1.37",
    "width": 1920, "height": 1080, "fps": 120, "codec": "hevc", "bitrateKbps": 50000, "hdr": false,
    "startedAt": "2026-10-29T18:00:00+00:00"
  }
}
```

`stream` is the Moonlight stream the desk serves, `null` when nobody is connected (with several, the earliest).
`client` and `clientId` name the paired client (`clientId` as in `GET /desks/{id}/clients`), `null` when the worker does
not know it; `width`, `height`, `fps`, `codec`, `bitrateKbps` and `hdr` are what the client asked for. It appears once the
client has connected the stream's control channel and changes on the worker's `client_connected`/`client_disconnected`
events and heartbeats (ipc.md), and is cleared when the worker stops or
restarts. `state` follows it: `Ready` → `Streaming` while a stream runs, back to `Ready` when the last one ends.

`metrics` are the worker's live numbers over the last second (definitions in ipc.md): `fps` and `bitrateKbps` actually
sent, `encodeMsP50`/`encodeMsP99` host processing latency (capture to encoded frame), `lossPct` frames the client
reported lost, `rttMs` round-trip time to the client (`null` until one is measured). All 0 (`rttMs` `null`) when
nothing streams.

`hasPassword` tells whether a password for `user` is stored; the password itself is never returned.
`resolutionPendingRestart` is true when the resolution changed while running and the seat could not resize the session.
`steamIsolation` (config.md) is always present in responses; POST/PUT accept it, and a PUT without it keeps the current
value. Changing it on a running desk is `409 invalid_state`.

`state` is one of the lifecycle states in ARCHITECTURE.md §4:
`Defined, Provisioning, Connecting, SessionReady, DisplayReady, Ready, Streaming, Recovering, Faulted, Stopping, Stopped`.

## Client launch pack

The host cannot take V-Sync latency out of the client, only recommend settings that do (ARCHITECTURE.md §9: about
8 ms on a 60 Hz client). `GET /desks/{id}/client-pack[?address=…]` answers:

```json
{
  "desk": "desk-1", "host": "192.168.1.20", "port": 48089, "address": "192.168.1.20:48089", "app": "Desktop",
  "settings": { "width": 1920, "height": 1080, "fps": 120, "bitrateKbps": 50000, "codec": "hevc",
                "vsync": false, "displayMode": "fullscreen", "videoDecoder": "hardware", "framePacing": false },
  "arguments": ["stream", "192.168.1.20:48089", "Desktop", "--resolution", "1920x1080", "--fps", "120",
                "--bitrate", "50000", "--video-codec", "HEVC", "--video-decoder", "hardware",
                "--display-mode", "fullscreen", "--no-vsync", "--no-frame-pacing"],
  "scripts": [{ "platform": "windows", "fileName": "splitplay-desk-1.cmd" },
              { "platform": "linux", "fileName": "splitplay-desk-1.sh" }]
}
```

- `host`: `?address=` when given (an IPv4 address or host name, otherwise 400 `validation_failed`), else the first
  address of `GET /host/network`, else the computer name; `port` is the desk's `basePort`; `app` is the desk's
  Moonlight app "Desktop".
- `settings`: the desk profile's resolution and fps (1920×1080 and 60 when unset) and codec (`auto` when unset: Moonlight picks, so a client without a hardware HEVC decoder falls back); its
  `bitrateKbps` when set, otherwise 30 Mbit/s for 1080p60 scaled by the square root of the pixel rate, +15 %, rounded
  to 5 Mbit/s, 10–150 Mbit/s (the add-desk wizard's rule), kept within Moonlight's 500–500000 kbit/s. V-Sync off
  (a VRR display avoids tearing without it), exclusive full screen, hardware decoding (D3D11VA on Windows), frame
  pacing off.
- `arguments` is moonlight-qt's command line after the executable; the option names are those of moonlight-qt's
  `app/cli/commandlineparser.cpp` (v6.2.0). Moonlight finds the host by `address:port` and adds it when it does not
  know it yet; the client must already be paired with the desk.
- `GET /desks/{id}/client-pack/windows` and `/linux` (same `?address=`) download the scripts as attachments: a `.cmd`
  (CRLF) that runs `%MOONLIGHT%`, a `Moonlight.exe` next to it, `%ProgramFiles%\Moonlight Game Streaming\Moonlight.exe`
  or `Moonlight.exe` on `PATH`, and a POSIX `.sh` that runs `$MOONLIGHT`, `moonlight`, `moonlight-qt` or the Flathub
  app `com.moonlight_stream.Moonlight`. Another platform name gives 404 `not_found`.

## Metrics

```json
{
  "host": {
    "cpuPercent": 7.5, "memoryUsedMb": 9120, "memoryTotalMb": 32607, "composeHz": 250,
    "gpus": [{ "luid": "0x0000000000012345", "name": "NVIDIA GeForce RTX 4080", "utilizationPercent": null,
               "encoderPercent": null, "vramUsedMb": null, "vramTotalMb": 16376 }]
  },
  "desks": [{ "id": "desk-1", "fps": 119.8, "bitrateKbps": 38450, "encodeMsP50": 3.1, "encodeMsP99": 5.4,
              "lossPct": 0, "rttMs": 4, "clients": 1 }]
}
```

Values that are not measured yet are `null`. Software adapters (Microsoft Basic Render Driver) are not listed.
The WebSocket `metrics` event is `{"type":"metrics","host":{…},"desks":[…]}` with the same fields.

## Error codes

| Code | HTTP | Meaning |
|---|---|---|
| `bad_request` | 400 | malformed JSON or parameters |
| `validation_failed` | 400 | the body breaks a rule of config.md |
| `unauthorized` | 401 | no valid session |
| `invalid_password` | 401 | wrong admin password |
| `csrf_failed`, `forbidden` | 403 | missing or wrong `X-CSRF-Token`; not allowed (non-loopback caller, cross-origin state change, unexpected `Host`) |
| `not_found` | 404 | unknown desk or client |
| `already_exists` | 409 | desk id, user or base port taken |
| `console_user` | 409 | the desk's user is the one signed in at the console |
| `user_not_managed` | 409 | `deleteUser=true` on an account SplitPlay did not create |
| `invalid_state` | 409 | the desk is in the wrong state (for example `PUT` of non-profile fields while running) |
| `child_unavailable` | 409, 504 | the desk's worker is not connected (409) or did not answer in time (504) |
| `child_error` | 502, 501 | the worker answered with an error; the message starts with the IPC code (501 for `unsupported`/`unknown_type`) |
| `not_implemented` | 501 | planned for a later stage (`includeSecrets=true`) |
| `internal` | 500 | unexpected failure |
