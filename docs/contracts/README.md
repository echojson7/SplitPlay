# SplitPlay contracts

This folder fixes the interfaces between components so that each one can be built and tested on its own.
Architecture rationale lives in [`../ARCHITECTURE.md`](../ARCHITECTURE.md). Code comments are in English across the repository.

| Contract | File | Between |
|---|---|---|
| Repository layout and licences | this file | everyone |
| Control IPC (named pipes, JSON lines) | [`ipc.md`](ipc.md) | service ↔ worker, service ↔ seat |
| Web API (`http://127.0.0.1:38299/api/v1`) | [`web-api.md`](web-api.md) | service ↔ web UI, tray, CLI |
| Configuration file | [`config.md`](config.md) | service, CLI, import/export |
| Capture ring (shared memory + D3D11 shared textures) | [`../../native/include/splitplay/capture_ring.h`](../../native/include/splitplay/capture_ring.h) | IDD ↔ worker |
| Input ring (shared memory) | [`../../native/include/splitplay/input_ring.h`](../../native/include/splitplay/input_ring.h) | worker ↔ seat |
| Frame tracing | [`../../native/include/splitplay/trace.h`](../../native/include/splitplay/trace.h) | IDD, worker, seat |

## Repository layout

```
LICENSE                 MIT, covers everything except worker/
NOTICE.md               third-party components and their licences
service/                .NET 10: SplitPlay.Core, SplitPlay.Service, SplitPlay.Cli, SplitPlay.Tray, tests
web/                    Vue 3 + Vite SPA, built into service wwwroot
native/                 CMake + MSVC: shared headers, splitplay-seat, splitplay-rdsprov, stub pipeline, tests
drivers/                UMDF2 drivers built with the WDK NuGet packages: idd/ (remote-session IddCx), pad/ (VHF gamepads)
worker/                 GPL-3.0: pinned upstream Sunshine + patch queue + MSYS2 build script
installer/              Inno Setup 6 script producing SplitPlay-Setup-<version>.exe
.github/workflows/      one workflow per component plus the integration workflow
docs/                   architecture, research, contracts
```

## Licence boundary

- `worker/` and everything built from it is GPL-3.0 (`worker/COPYING`). Its source, including the patch queue, ships with every release.
- Everything else is MIT. MIT code never links Sunshine code. The worker talks to the rest only through the contracts above
  (process arguments, files, JSON over named pipes, the documented shared-memory layouts, HTTP on loopback).
- The headers in `native/include/splitplay/` are MIT and may be included by GPL code (the worker), not the other way round.
- No file under the LizardByte Source-Available License may enter the repository or the worker build
  (`SPDX-License-Identifier: LicenseRef-LizardByte-SAL-1.0`). CI fails if one appears.

## Naming and ids

- Desk id: lowercase `[a-z0-9-]{1,32}`, stable, chosen at creation (`desk-1`, `gaming`).
- Process names: `SplitPlay.Service.exe`, `splitplay.exe` (CLI), `SplitPlay.Tray.exe`, `splitplay-worker.exe`, `splitplay-seat.exe`, `splitplay-stubworker.exe`.
- Windows service name: `SplitPlay`, display name `SplitPlay Host Service`.
- Data root: `%ProgramData%\SplitPlay` (`config.json`, `desks\<id>\`, `logs\`).
- Kernel object names: `Global\SplitPlay.<kind>.<desk-id>`; pipes: `\\.\pipe\splitplay\<role>-<desk-id>`.
  The admin token pipe is `\\.\pipe\splitplay\admin` (web-api.md "Authentication").
