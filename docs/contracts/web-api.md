# Web API v1

Base URL: `http://127.0.0.1:38299/api/v1` (LAN access, when enabled, is HTTPS on the same port).
JSON in and out, `camelCase` field names. Errors: HTTP 4xx/5xx with `{"error":{"code":"...","message":"..."}}`.

## Authentication

Desk users are other people signed in on the same PC, so loopback alone proves nothing. Every route except
`GET /health` and the `/auth/*` routes below needs proof of being the host's administrator, reads included
(desks, logs, accounts, config export, the WebSocket); without it the answer is 401 `unauthorized`. Proof is one of:

- **A bearer token from the admin pipe** (CLI, scripts): `Authorization: Bearer <token>`, valid 15 minutes, no CSRF
  token needed. `splitplay auth token` prints one. The CLI uses `SPLITPLAY_TOKEN` when it is set; otherwise, in the
  console session, it keeps the pipe's token in `%LOCALAPPDATA%\SplitPlay\cli-token-s<session>.bin` (DPAPI, current
  user, medium mandatory label with no-read-up so that low-integrity processes cannot read it; if the label cannot be
  set nothing is cached) and reuses it while the service accepts it and at least a minute is left. Elsewhere (an elevated prompt in
  another session) there is no cache: a script that calls the CLI in a loop sets `SPLITPLAY_TOKEN` from
  `splitplay auth token` once, because the pipe takes at most 30 requests a minute per session.
- **A session from the admin pipe** (tray): the tray puts it into its WebView as the `splitplay_session` cookie, so the
  owner never sees a login. Valid 12 hours; the UI posts `auth` to the tray whenever it is not signed in (expired,
  service restarted) and the tray signs in again and reloads, at most every 10 s. If the page is still not signed in
  right after 3 such reloads (the API rejects what the pipe granted) the tray stops and the page keeps its password
  login until the dashboard is opened again. A password change does not end it.
- **A session from the admin password** (any other browser): `POST /auth/login {"password":"..."}` sets the cookie;
  `POST /auth/logout` clears it. Valid 12 hours; a password change ends it.

Admin pipe `\\.\pipe\splitplay\admin`: one JSON line each way, `{"kind":"bearer"|"session","client":"cli"}` →
`{"ok":true,"token":"…","csrfToken":"…","expiresAt":"…"}` or `{"ok":false,"error":{"code":"forbidden","message":"…"}}`.
Its DACL lets SYSTEM, Administrators and interactive users (IU) connect; the service then reads the caller's token
(the pipe client is asked for identification-level impersonation only) and hands out a credential to:

- SYSTEM, or an administrator whose token is elevated (an elevated prompt, the installer, SSH), in any session that
  is not a desk session;
- an administrator with a UAC-filtered token in the active console session (the owner's tray and CLI).

Everybody else is refused: a standard user at the console, any RDP session, a process below medium integrity, and
always a process in a desk session, even a desk user who is an administrator. Desk sessions, callers that are clearly
not the admin and callers over a limit are disconnected right after connecting, before anything is read and without
an answer (the client tries once more after 250 ms, then reports that the service closed the connection); a caller
refused after its request was read gets the reason. The pipe has no instance limit and keeps 3 instances listening;
at most 4 new connections per session are screened at once (more from that session are disconnected unscreened, so a
desk cannot crowd out the owner), a session may keep at most 4 connections open and make 30
requests a minute, every admitted connection ends after 3 s, and dropped connections are logged at most every 10 s.
Refused users can still log in with the admin password (except from a desk session, see below). Clients only talk to a pipe owned by SYSTEM, Administrators or themselves (squatting while
the service is stopped). The first admin password can only be set by the administrator (tray, CLI) in Settings, so a
desk user cannot set it and take the host over.

Defence in depth: the service maps each loopback connection to the process that opened it (`GetExtendedTcpTable`)
and its session. A request from a desk session gets 403 `forbidden` on every route but `/health`, token or not
(this also stops password guessing from a desk). A desk session is one a seat opened, or any session signed in with a
configured desk user or an account SplitPlay created; a session whose user cannot be read counts as one, the console
session never does. A loopback peer whose process cannot be found gets 403 too, unless it carries a credential only the
admin pipe hands out (the tray's session, a bearer token).

Password checks (`/auth/login`, `/auth/password`) run one at a time, and a caller (its session, or its address) has at
most one in progress: further attempts meanwhile get 429 at once. After 5 wrong passwords from one caller each further
one locks it out for 1, 2, 4 … s up to 15 minutes (checked again when its turn comes): 429 `too_many_attempts` with
`Retry-After`. More than 20 wrong passwords within a minute from everybody lock nobody else out; instead every check
waits 0.1, 0.2, 0.4 … s first, at most 2 s.

- Sessions use the HttpOnly, `SameSite=Strict` cookie `splitplay_session`. A request that carries the cookie and
  changes state (POST, PUT, DELETE) must send `X-CSRF-Token` (returned by login, by `GET /auth/session` and by the
  admin pipe), otherwise it gets 403 `csrf_failed`.
- The service listens on loopback only until LAN access (HTTPS) is implemented.
- Browser checks in front of every `/api` route (403 `forbidden`):
  - a POST, PUT or DELETE with `Sec-Fetch-Site: cross-site`, or with an `Origin` that is not the request's own
    scheme://host:port (`Origin: null` included), is refused: another site's page could otherwise post a form here,
    and it carries no `SameSite=Strict` cookie, so the CSRF check would not apply. Requests without these headers (CLI,
    scripts) and the UI itself (same origin, on loopback or on the LAN host) pass;
  - on a loopback connection the `Host` header must be `localhost` or an IP address (DNS rebinding).

| Request | Body | Answer |
|---|---|---|
| `GET /auth/session` | | `{"setupRequired":false,"authenticated":true,"csrfToken":"…"}`: `setupRequired` no admin password is set; `authenticated` the request carries admin proof; `csrfToken` only with a session cookie |
| `POST /auth/login` | `{"password":"…"}` | `{"csrfToken":"…"}`; wrong password → 401 `invalid_password`; no password set → 409 `invalid_state` |
| `POST /auth/logout` | | 204 |
| `POST /auth/password` | `{"newPassword":"…","currentPassword":"…"}` (`currentPassword` omitted for the first password; at least 8 characters) | `{"csrfToken":"…"}`, and the browser is logged in. The first password needs admin proof (401 `unauthorized` otherwise); a change needs the right `currentPassword` |

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
  "hideFromLogon": true,
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
`hideFromLogon` (config.md) is always present in responses as the effective value; POST/PUT accept it, a PUT without it
keeps the current value unless `user` changes, and it may change while the desk runs (it applies at once).

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
| `unauthorized` | 401 | no admin proof (session or bearer token) |
| `invalid_password` | 401 | wrong admin password |
| `too_many_attempts` | 429 | too many wrong passwords, or a check from the same caller is still running; `Retry-After` says when to try again |
| `csrf_failed`, `forbidden` | 403 | missing or wrong `X-CSRF-Token`; not allowed (non-loopback caller, cross-origin state change, unexpected `Host`, a desk session) |
| `not_found` | 404 | unknown desk or client |
| `already_exists` | 409 | desk id, user or base port taken |
| `console_user` | 409 | the desk's user is the one signed in at the console |
| `user_not_managed` | 409 | `deleteUser=true` on an account SplitPlay did not create |
| `invalid_state` | 409 | the desk is in the wrong state (for example `PUT` of non-profile fields while running) |
| `child_unavailable` | 409, 504 | the desk's worker is not connected (409) or did not answer in time (504) |
| `child_error` | 502, 501 | the worker answered with an error; the message starts with the IPC code (501 for `unsupported`/`unknown_type`) |
| `not_implemented` | 501 | planned for a later stage (`includeSecrets=true`) |
| `internal` | 500 | unexpected failure |
