# Web API v1

Base URL: `http://127.0.0.1:38299/api/v1` (LAN access, when enabled, is HTTPS on the same port).
JSON in and out, `camelCase` field names. Errors: HTTP 4xx/5xx with `{"error":{"code":"...","message":"..."}}`.

## Authentication

- Requests from loopback carrying a valid token pass. The tray app and CLI obtain a token from the service over
  `\\.\pipe\splitplay\admin` (DACL: Administrators and SYSTEM) and send it as `Authorization: Bearer <token>`.
- The browser UI logs in with the admin password: `POST /auth/login {"password":"..."}` sets an HttpOnly session cookie;
  `POST /auth/logout` clears it. State-changing requests from the browser also need the `X-CSRF-Token` header
  whose value is returned by login and by `GET /auth/session`.
- Stage 2 skeleton: if no admin password is set yet, loopback requests are allowed without auth and
  `GET /auth/session` returns `{"setupRequired":true}`.

## Endpoints

| Method | Path | Purpose |
|---|---|---|
| GET | `/health` | `{"status":"ok","version":"0.1.0","uptimeSeconds":12}`; no auth |
| GET | `/auth/session` | current auth state |
| POST | `/auth/login`, `/auth/logout`, `/auth/password` | login, logout, set or change the admin password |
| GET | `/desks` | list of desks with live state |
| POST | `/desks` | create a desk (body = desk config, see config.md) |
| GET | `/desks/{id}` | one desk with live state |
| PUT | `/desks/{id}` | replace desk config (only while stopped, except `profile`) |
| DELETE | `/desks/{id}` | delete a stopped desk (`?deleteUser=true` also removes the Windows account) |
| POST | `/desks/{id}/start`, `/desks/{id}/stop`, `/desks/{id}/restart` | lifecycle; return 202 with the desk |
| GET | `/desks/{id}/clients` | paired Moonlight clients |
| POST | `/desks/{id}/pair` | `{"pin":"1234","name":"Laptop"}` |
| DELETE | `/desks/{id}/clients/{clientId}` | revoke a client |
| GET | `/metrics` | host-level metrics plus per-desk metrics |
| GET | `/logs?component=service&desk=desk-1&tail=200` | recent log lines |
| GET | `/config/export` | zip download (secrets excluded unless `?includeSecrets=true&password=...`) |
| POST | `/config/import` | zip upload |
| GET | `/ws` | WebSocket: pushes `{"type":"desk","desk":{...}}` and `{"type":"metrics",...}` events |

## Desk object (response)

```json
{
  "id": "desk-1",
  "name": "Skyrim box",
  "user": "splitplay-desk1",
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

`state` is one of the lifecycle states in ARCHITECTURE.md §4:
`Defined, Provisioning, Connecting, SessionReady, DisplayReady, Ready, Streaming, Recovering, Faulted, Stopping, Stopped`.
