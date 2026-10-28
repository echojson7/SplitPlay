# SplitPlay web UI

Vue 3 + Vite + TypeScript single-page app for the SplitPlay host: desks list, desk details
(start/stop/restart, Pair New Client), create/edit desk, host settings, logs, metrics and
login / first-time password setup. The UI is in Russian; English strings are the fallback.

The SPA talks only to Web API v1 ([`docs/contracts/web-api.md`](../docs/contracts/web-api.md)) through the
typed client in `src/api/client.ts`.

## Commands

Node.js 22.12+ and npm (the lockfile is `package-lock.json`).

| Command | What it does |
|---|---|
| `npm ci` | install exact dependencies |
| `npm run dev:mock` | dev server on http://localhost:5173 with the in-memory mock API, no service needed |
| `npm run dev` | dev server that proxies `/api` (HTTP and WebSocket) to a service on `127.0.0.1:38299` |
| `npm run lint` | ESLint (flat config, `eslint-plugin-vue` + `typescript-eslint`) |
| `npm run type-check` | `vue-tsc --build` |
| `npm test` | Vitest unit tests (jsdom) |
| `npm run build` | production bundle in `web/dist` |
| `npm run check` | all of the above in CI order |

## Packaging

`npm run build` writes a self-contained static bundle to **`web/dist`** (relative asset URLs, hash-based
routing, so no server-side fallback route is needed). The service does not build the web UI itself:
at packaging time the contents of `web/dist` are copied into the service's `wwwroot` folder
(installed as `Program Files\SplitPlay\service\wwwroot\`), and `SplitPlay.Service` serves it as static
files next to `/api/v1`. In CI the `web-dist` artifact of `.github/workflows/web-installer.yml` is that folder.

## Layout

```
src/api/types.ts      API types (desk, metrics, logs, auth); assumed shapes are marked in comments
src/api/client.ts     typed fetch client: CSRF header, error mapping (ApiError), list unwrapping, WebSocket URL
src/api/validate.ts   client-side desk config rules from config.md
src/i18n/messages.ts  every UI string: `en` (complete, fallback) and `ru` (UI language)
src/store.ts          reactive desks/metrics/session; live updates via /ws with REST polling fallback
src/router.ts         routes and the auth guard (setup -> login -> app)
src/views/            pages
src/components/       StateBadge, PairDialog, DeskForm
mock/backend.ts       in-memory Web API used by dev:mock and by the tests
mock/vite-plugin.ts   mounts the mock under /api/v1 in the Vite dev server
tests/                Vitest specs (API client, validation, i18n, components, whole-app smoke)
```

## Mock API

`mock/backend.ts` implements every endpoint of web-api.md (except the `/ws` socket; the UI falls back to
polling every 2 s) with three seeded desks, simulated lifecycle transitions and metrics. Pairing accepts any
4-digit PIN on a running desk; `0000` simulates a timeout. No admin password is set initially, so the UI
starts on the first-time setup page.

## API assumptions

web-api.md leaves some response shapes open; the client accepts these and degrades gracefully:

- `GET /auth/session` -> `{ setupRequired, authenticated, csrfToken? }`; `POST /auth/login` -> `{ csrfToken }`.
- `POST /auth/password` body `{ newPassword, currentPassword? }` (no `currentPassword` for the first password).
- `GET /desks`, `/desks/{id}/clients`, `/logs`: a bare array or `{ desks | clients | lines: [...] }`.
- Client: `{ id, name, pairedAt? }`. Log line: `{ time, level, component, desk?, message }`.
- `GET /metrics` -> `{ host: { cpuPercent?, memoryUsedMb?, memoryTotalMb?, composeHz?, gpus: [{ luid, name, utilizationPercent?, encoderPercent?, vramUsedMb?, vramTotalMb? }] }, desks: [{ id, fps, bitrateKbps, encodeMsP99, clients }] }`.
  The desk form's GPU list comes from `host.gpus`.
- Host settings use `GET/PUT /host` (config.json `host` object). This endpoint is not in web-api.md yet;
  when the service answers 404 the settings page says the service does not support it.
