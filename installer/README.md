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
    splitplay.exe             CLI (single-file, self-contained); the installer runs `splitplay.exe service install` / `service uninstall`
    ...                       the service's DLLs, runtime, runtimeconfig, etc.
    wwwroot\                  contents of web/dist (`npm run build` in web/); index.html must exist
  tray\                       dotnet publish output of SplitPlay.Tray (self-contained, own WindowsDesktop runtime)
    SplitPlay.Tray.exe        tray / GUI (WebView2)
  native\
    splitplay-seat.exe
    splitplay-rdsprov.dll
    splitplay-stubworker.exe
  worker\                     GPL-3.0 part, built from worker/
    splitplay-worker.exe      and its DLLs and assets
    COPYING                   GPL-3.0 text (required)
    README-source.txt         written source offer / link to the exact source and patch queue
  drivers\                    driver packages (optional; copied to Program Files but not installed in Stage 2)
  termwrap\                   TermWrap x64 release build (llccd/TermWrap, MIT), installed as the "termwrap" component
    TermWrap.dll              loaded by TermService in place of termsrv.dll (concurrent sessions)
    UmWrap.dll                loaded by UmRdpService on Home and Server editions only
    Zydis.dll                 disassembler used by both
    EndpWrap.dll              optional; shipped if present but not wired up by SplitPlay
    LICENSE                   TermWrap's MIT licence (should be staged with the DLLs)
  vigembus\
    ViGEmBus_1.22.0_x64_x86_arm64.exe   official signed ViGEmBus setup (nefarius/ViGEmBus, BSD-3-Clause), unmodified;
                              ci.yml downloads it from the pinned release URL and checks its SHA256
```

Required files (checked at compile time): `service\SplitPlay.Service.exe`, `service\splitplay.exe`,
`tray\SplitPlay.Tray.exe`, `service\wwwroot\index.html`, the three files in `native\`,
`worker\splitplay-worker.exe`, `worker\COPYING`, `termwrap\TermWrap.dll`, `termwrap\UmWrap.dll` and the ViGEmBus
setup in `vigembus\` (file name from `/DViGEmBusSetup`, default `ViGEmBus_1.22.0_x64_x86_arm64.exe`). The
repository's `LICENSE` and `NOTICE.md` are added by the script.

`termwrap\` is a top-level stage folder, not part of `native\`: if the native build's artifact carries TermWrap in a
`termwrap` subfolder, the stage step copies it to `<StageDir>\termwrap`.

## What the installer does

1. Requires administrator rights and 64-bit Windows (x64). Refuses Windows older than Windows 11 22H2
   (build 22621), asks for confirmation below 24H2 (build 26100) and on Windows Server.
2. Licence page: a notice that SplitPlay modifies Windows Remote Desktop Services to allow concurrent
   sessions, which violates the Microsoft licence terms for Windows client editions, followed by `LICENSE`.
   English and Russian.
3. Components: the SplitPlay host (fixed) and **TermWrap** (selected by default in both setup types).
4. Installs to `%ProgramFiles%\SplitPlay\{service,tray,native,worker,drivers,termwrap}`. TermService keeps
   `TermWrap.dll` loaded, so an upgrade replaces it at the next restart (`restartreplace`).
5. Creates `%ProgramData%\SplitPlay` (with `desks\` and `logs\`) and replaces its ACL with full control for
   SYSTEM and Administrators only, inherited by everything below.
6. Adds the firewall rule `SplitPlay Web UI` (TCP 38299 inbound, local subnet, private/domain profiles,
   for `SplitPlay.Service.exe`) **disabled**: by default the web UI listens on loopback only, which the
   firewall does not filter. The service enables the rule when LAN access is turned on. Per-desk rules
   (`SplitPlay desk <id> (TCP|UDP)`, the desk's Sunshine ports, private/domain) are created and removed by the service.
7. Runs `splitplay.exe host prepare` (with `--skip-termwrap` when the TermWrap component is not selected or
   `/SkipTermWrap=1` is given), before the service starts. It turns Remote Desktop on (`fDenyTSConnections=0`,
   `fSingleSessionPerUser=0`, NLA on), sets the session policies desks need (no idle or disconnection limits,
   `bEnumerateHWBeforeSW=1`, `DWMFRAMEINTERVAL` from `host.composeIntervalMs`), sets TermService to start
   automatically and, with TermWrap, points the `ServiceDll` of TermService (and of UmRdpService on Home and Server
   editions) at `{app}\termwrap\` exactly as TermWrap's own `Install_termwrap_*.reg` files do. Every previous value
   is recorded in `%ProgramData%\SplitPlay\host-tuning.json`. When the ServiceDll changed while TermService was
   running, the command exits with 3010 and setup asks for a restart (`NeedRestart`; `/NORESTART` suppresses it).
   Until that restart desks go to Faulted with "RDP host not prepared: reboot required". A failure shows a message
   and setup continues; `splitplay host prepare` can be run again later, and `splitplay host status` shows each item.
8. ViGEmBus (interim gamepads until SplitPlay's per-session pad driver in Stage 4; its virtual pads are visible to
   every session): when no ViGEmBus driver is present (`System32\drivers\ViGEmBus.sys`, or the binary of the
   `ViGEmBus` service), runs the bundled setup silently with `/exenoui /qn /norestart /exelog
   %ProgramData%\SplitPlay\logs\vigembus-setup.log` (Advanced Installer bootstrapper: no bootstrapper UI, msiexec
   without UI, no restart). If the driver is then present, setup records `ViGEmBusInstalledBySplitPlay=1` and the
   MSI product code (`ViGEmBusProductCode`) in `HKLM\Software\SplitPlay`. A failure is only logged; SplitPlay is
   installed anyway and desks have no gamepads. An existing ViGEmBus is left alone. Exit code 3010 asks for a restart.
   The setup never blocks SplitPlay's: it runs through a PowerShell wrapper with a timeout (`/ViGEmBusTimeout`,
   default 120 s); on timeout the wrapper kills the MSI's `nefconw.exe` helper, then the setup's process tree, and
   nothing is recorded. On Windows Server the step is skipped: the ViGEmBus 1.22.0 MSI has a launch condition that
   refuses 64-bit Windows Server, and on the windows-2022 runner its setup never returned.
9. Runs `splitplay.exe service install` (only if the service is not registered yet) and starts the service.
   On upgrade the service is stopped before files are replaced and started again afterwards.
10. Start menu: `SplitPlay` (tray) and `SplitPlay Web UI` (http://127.0.0.1:38299/). Optional task
   (on by default): start the tray at sign-in for all users (`HKLM\...\Run\SplitPlayTray`, value
   `"...\SplitPlay.Tray.exe" --autostart`). Started from the Start menu or the final "Launch SplitPlay" checkbox,
   the tray opens its dashboard window; with `--autostart` it stays an icon. A second start brings the running
   tray's window to the front instead of adding another icon.
11. WebView2: if the Evergreen runtime is missing, downloads Microsoft's bootstrapper and runs it silently.
   A failed download only shows a warning.

Uninstall stops and removes the service (`splitplay.exe service uninstall`, falling back to `net stop` and
`sc delete`; stopping the service stops every desk and logs its session off), runs `splitplay.exe host revert`
(restores the exact values recorded in `host-tuning.json`, deleting the ones that did not exist before; when it
reverted a loaded ServiceDll it exits with 3010 and the uninstaller asks for a restart, and `TermWrap.dll` is deleted
at that restart), removes ViGEmBus with `msiexec /x <ViGEmBusProductCode> /qn /norestart` (same timeout) only if
`HKLM\Software\SplitPlay` records that SplitPlay installed it (a failed removal is logged and keeps the record),
closes the tray, removes the `SplitPlay Web UI` rule and every per-desk `SplitPlay desk *` rule,
the shortcuts and autostart entry, and asks whether to keep `%ProgramData%\SplitPlay` (silent uninstall keeps it).
Windows accounts created for desks are not removed by the uninstaller; `splitplay desk delete <id> --delete-user`
(or `DELETE /api/v1/desks/{id}?deleteUser=true`) removes one together with its stored password.

## Command-line parameters

Standard Inno Setup switches (`/SILENT`, `/VERYSILENT`, `/SUPPRESSMSGBOXES`, `/LOG=...`, `/TASKS=...`, `/DIR=...`) plus:

| Program | Parameter | Effect |
|---|---|---|
| setup | `/AllowUnsupportedOS=1` | skip the Windows version and edition checks (CI, testing) |
| setup | `/SkipWebView2=1` | never download WebView2 |
| setup | `/SkipViGEmBus=1` | do not install ViGEmBus even if it is missing (desks then have no gamepads) |
| setup | `/ViGEmBusOnServer=1` | run the ViGEmBus setup on Windows Server too (testing the timeout path; it refuses Server) |
| setup, unins000.exe | `/ViGEmBusTimeout=<s>` | seconds to wait for the ViGEmBus setup or its removal before killing it (default 120, minimum 5) |
| setup | `/SkipTermWrap=1` | install the TermWrap files but run `host prepare --skip-termwrap` (TermService untouched; CI) |
| setup | `/COMPONENTS=core` | do not install TermWrap at all |
| setup | `/TASKS="!trayautostart"` | do not start the tray at sign-in |
| unins000.exe | `/PurgeData=1` | also delete `%ProgramData%\SplitPlay` |

## CI

`.github/workflows/web-installer.yml` builds `web/dist`, creates a fake stage with
[`ci/New-FakeStage.ps1`](ci/New-FakeStage.ps1) (every executable is the small stub from [`ci/stub.cs`](ci/stub.cs),
compiled with the .NET Framework `csc.exe` that ships with Windows; its `service install` registers a real,
idle `SplitPlay` service), checks that ISCC refuses an empty stage, compiles the installer and runs
[`ci/Test-Installer.ps1`](ci/Test-Installer.ps1): silent install, upgrade over a running service, uninstall
keeping data, reinstall without the TermWrap component and uninstall with `/PurgeData=1`, checking files, service
state, firewall rules (including removal of per-desk rules), ProgramData ACL, shortcuts, the `--autostart` Run value,
that setup ran `host prepare --skip-termwrap` before starting the service and the uninstaller ran `host revert` (the
stub records its calls in `%ProgramData%\SplitPlay-ci-stub-calls.log`), that setup ran the (stub) ViGEmBus setup with
the silent switches without recording it as installed, that the uninstaller removes a recorded ViGEmBus product and
only then runs msiexec, and that the runner's TermService `ServiceDll` never changes. Every install there uses
`/SkipTermWrap=1`, and `/ViGEmBusOnServer=1` so that the stub runs on the Windows Server runner. The
`installer-preview` artifact is the resulting setup built from stub binaries, so it is only good for trying the wizard.

In the integration workflow `Test-Installer.ps1 -RealStage` runs the setup built from the real artifacts. Its first
install passes `/ViGEmBusOnServer=1 /ViGEmBusTimeout=30`, so the real ViGEmBus setup runs on the runner once: setup
must still exit 0, leave no ViGEmBus setup or `nefconw.exe` process behind and log the outcome (on Windows Server that
is the timeout); if the driver did install, the record must match and the uninstall must remove it. The later passes
and `Test-EndToEnd.ps1` take the default Windows Server skip.

[`ci/Test-EndToEnd.ps1`](ci/Test-EndToEnd.ps1) runs in the integration workflow on the setup built from the real
artifacts (also with `/SkipTermWrap=1`): it checks the installer's host preparation, switches the service to the stub
worker without a seat through the service's `Environment` registry value (`Supervisor__Worker=stub`,
`Supervisor__Seat=none`, `Supervisor__WorkerSession=console`), runs a desk (account provisioning, per-desk firewall
rules, worker in the console session, crash recovery), deletes it with `--delete-user` and checks that the purging
uninstall reverted the host settings.
