# SplitPlay installer

Inno Setup script [`splitplay.iss`](splitplay.iss) that packages an already built **stage folder** into
`SplitPlay-Setup-<version>.exe`. The script does not build anything itself.

## Building

Inno Setup 6.3 or newer (7.x also works).

```bat
ISCC.exe /DStageDir=C:\src\SplitPlay\build\stage /DAppVersion=0.1.0 /DOutputDir=C:\src\SplitPlay\build\installer installer\splitplay.iss
```

| Define | Default | Meaning |
|---|---|---|
| `StageDir` | `..\build\stage` (relative to this folder) | stage folder, layout below |
| `AppVersion` | `0.1.0` | version shown to the user and used in the file name; a `-suffix` is allowed (`0.1.0-ci.42`) |
| `OutputDir` | `..\build\installer` | where `SplitPlay-Setup-<AppVersion>.exe` is written |
| `WebPort` | `38299` | web UI port used for the firewall rule and the Start menu link |

Compilation fails with a clear error if a required staged file is missing.

## Stage layout

```
<StageDir>\
  service\                    dotnet publish output of SplitPlay.Service (self-contained), plus:
    SplitPlay.Service.exe     Windows service (name SplitPlay, display name "SplitPlay Host Service")
    splitplay.exe             CLI; the installer runs `splitplay.exe service install` / `service uninstall`
    SplitPlay.Tray.exe        tray / GUI (WebView2)
    ...                       their DLLs, runtimeconfig, etc.
    wwwroot\                  contents of web/dist (`npm run build` in web/); index.html must exist
  native\
    splitplay-seat.exe
    splitplay-rdsprov.dll
    splitplay-stubworker.exe
  worker\                     GPL-3.0 part, built from worker/
    splitplay-worker.exe      and its DLLs and assets
    COPYING                   GPL-3.0 text (required)
    README-source.txt         written source offer / link to the exact source and patch queue
  drivers\                    driver packages (optional; copied to Program Files but not installed in Stage 2)
```

Required files (checked at compile time): `service\SplitPlay.Service.exe`, `service\splitplay.exe`,
`service\SplitPlay.Tray.exe`, `service\wwwroot\index.html`, the three files in `native\`,
`worker\splitplay-worker.exe` and `worker\COPYING`. The repository's `LICENSE` and `NOTICE.md` are added by the script.

## What the installer does

1. Requires administrator rights and 64-bit Windows (x64). Refuses Windows older than Windows 11 22H2
   (build 22621), asks for confirmation below 24H2 (build 26100) and on Windows Server.
2. Licence page: a notice that SplitPlay modifies Windows Remote Desktop Services to allow concurrent
   sessions, which violates the Microsoft licence terms for Windows client editions, followed by `LICENSE`.
   English and Russian.
3. Components: the SplitPlay host (fixed) and a **disabled TermWrap placeholder**: TermWrap is not bundled
   yet and Remote Desktop Services are not modified by this build.
4. Installs to `%ProgramFiles%\SplitPlay\{service,native,worker,drivers}`.
5. Creates `%ProgramData%\SplitPlay` (with `desks\` and `logs\`) and replaces its ACL with full control for
   SYSTEM and Administrators only, inherited by everything below.
6. Adds the firewall rule `SplitPlay Web UI` (TCP 38299 inbound, local subnet, private/domain profiles,
   for `SplitPlay.Service.exe`) **disabled**: by default the web UI listens on loopback only, which the
   firewall does not filter. The service enables the rule when LAN access is turned on. Per-desk port-range
   rules will come with multi-desk support.
7. Runs `splitplay.exe service install` (only if the service is not registered yet) and starts the service.
   On upgrade the service is stopped before files are replaced and started again afterwards.
8. Start menu: `SplitPlay` (tray) and `SplitPlay Web UI` (http://127.0.0.1:38299/). Optional task
   (on by default): start the tray at sign-in for all users (`HKLM\...\Run\SplitPlayTray`).
9. WebView2: if the Evergreen runtime is missing, downloads Microsoft's bootstrapper and runs it silently.
   A failed download only shows a warning.

Uninstall stops and removes the service (`splitplay.exe service uninstall`, falling back to `net stop` and
`sc delete`), closes the tray, removes the firewall rule, shortcuts and autostart entry, and asks whether to
keep `%ProgramData%\SplitPlay` (silent uninstall keeps it). Windows accounts created for desks are never removed.

## Command-line parameters

Standard Inno Setup switches (`/SILENT`, `/VERYSILENT`, `/SUPPRESSMSGBOXES`, `/LOG=...`, `/TASKS=...`, `/DIR=...`) plus:

| Program | Parameter | Effect |
|---|---|---|
| setup | `/AllowUnsupportedOS=1` | skip the Windows version and edition checks (CI, testing) |
| setup | `/SkipWebView2=1` | never download WebView2 |
| setup | `/TASKS="!trayautostart"` | do not start the tray at sign-in |
| unins000.exe | `/PurgeData=1` | also delete `%ProgramData%\SplitPlay` |

## CI

`.github/workflows/web-installer.yml` builds `web/dist`, creates a fake stage with
[`ci/New-FakeStage.ps1`](ci/New-FakeStage.ps1) (every executable is the small stub from [`ci/stub.cs`](ci/stub.cs),
compiled with the .NET Framework `csc.exe` that ships with Windows; its `service install` registers a real,
idle `SplitPlay` service), checks that ISCC refuses an empty stage, compiles the installer and runs
[`ci/Test-Installer.ps1`](ci/Test-Installer.ps1): silent install, upgrade over a running service, uninstall
keeping data, reinstall and uninstall with `/PurgeData=1`, checking files, service state, firewall rule,
ProgramData ACL, shortcuts and autostart after every step. The `installer-preview` artifact is the
resulting setup built from stub binaries, so it is only good for trying the wizard.
