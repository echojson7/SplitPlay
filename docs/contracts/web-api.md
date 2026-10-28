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
| GET | `/desks` | array of desks with live state |
| POST | `/desks` | create a desk (body = desk config, see config.md, plus optional `password`: the Windows password of `user`, verified when the account exists, used for a new account otherwise) |
| GET | `/desks/{id}` | one desk with live state |
| PUT | `/desks/{id}` | replace desk config (only while stopped, except `profile`); optional `password` as for POST. A `profile.width/height` change on a running desk is sent to the seat as `set_mode` |
| DELETE | `/desks/{id}` | delete a stopped desk (`?deleteUser=true` also removes the Windows account; 409 `user_not_managed` for an account SplitPlay did not create) |
| POST | `/desks/{id}/start`, `/desks/{id}/stop`, `/desks/{id}/restart` | lifecycle; return 202 with the desk. Starting a desk that is not `Stopped`/`Defined` gives 409 `invalid_state` (a `Faulted` desk must be stopped first); stop is accepted in every state and is a no-op on a stopped desk |
| GET | `/desks/{id}/clients` | array of paired Moonlight clients `{"id","name","pairedAt"?}` (409 `child_unavailable` when the desk is not running) |
| POST | `/desks/{id}/pair` | `{"pin":"1234","name":"Laptop"}` |
| DELETE | `/desks/{id}/clients/{clientId}` | revoke a client |
| GET | `/metrics` | host-level metrics plus per-desk metrics |
| GET | `/logs?component=service&desk=desk-1&tail=200` | array of `{"time","level","component","desk"?,"message"}` |
| GET | `/config/export` | zip download (secrets excluded unless `?includeSecrets=true&password=...`) |
| POST | `/config/import` | zip upload |
| GET | `/ws` | WebSocket: a `desk` event per desk on connect and on every change, `metrics` every 2 s |

## Desk object (response)

```json
{
  "id": "desk-1",
  "name": "Skyrim box",
  "user": "splitplay-desk1",
  "userManaged": true,
  "hasPassword": true,
  "resolutionPendingRestart": false,
  "state": "Stopped",
  "sessionId": null,
  "basePort": 48089,
  "autostart": true,
  "gpu": "auto",
  "profile": { "fps": 120, "width": 1920, "height": 1080, "bitrateKbps": 60000, "codec": "hevc" },
  "metrics": { "fps": 0, "bitrateKbps": 0, "encodeMsP99": 0, "clients": 0 },
  "lastError": null
}
```

`hasPassword` tells whether a password for `user` is stored; the password itself is never returned.
`resolutionPendingRestart` is true when the resolution changed while running and the seat could not resize the session.

`state` is one of the lifecycle states in ARCHITECTURE.md §4:
`Defined, Provisioning, Connecting, SessionReady, DisplayReady, Ready, Streaming, Recovering, Faulted, Stopping, Stopped`.

## Metrics

```json
{
  "host": {
    "cpuPercent": 7.5, "memoryUsedMb": 9120, "memoryTotalMb": 32607, "composeHz": 250,
    "gpus": [{ "luid": "0x0000000000012345", "name": "NVIDIA GeForce RTX 4080", "utilizationPercent": null,
               "encoderPercent": null, "vramUsedMb": null, "vramTotalMb": 16376 }]
  },
  "desks": [{ "id": "desk-1", "fps": 120, "bitrateKbps": 58000, "encodeMsP99": 2.1, "clients": 1 }]
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
| `csrf_failed`, `forbidden` | 403 | missing or wrong `X-CSRF-Token`; not allowed |
| `not_found` | 404 | unknown desk or client |
| `already_exists` | 409 | desk id, user or base port taken |
| `console_user` | 409 | the desk's user is the one signed in at the console |
| `user_not_managed` | 409 | `deleteUser=true` on an account SplitPlay did not create |
| `invalid_state` | 409 | the desk is in the wrong state (for example `PUT` of non-profile fields while running) |
| `child_unavailable` | 409, 504 | the desk's worker is not connected (409) or did not answer in time (504) |
| `child_error` | 502, 501 | the worker answered with an error; the message starts with the IPC code (501 for `unsupported`/`unknown_type`) |
| `not_implemented` | 501 | planned for a later stage (`includeSecrets=true`) |
| `internal` | 500 | unexpected failure |
