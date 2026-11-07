# TermWrap in SplitPlay

[TermWrap](https://github.com/llccd/TermWrap) (MIT, by llccd) wraps `termsrv.dll` so that Windows client editions
accept more than one interactive RDP session. SplitPlay needs that for its RDP-loopback desks. It also wraps
`umrdp.dll` (UmWrap) on Home and Server editions.

Nothing from TermWrap is vendored in this repository. `.github/workflows/native.yml` fetches it at build time and
builds it from source.

## Build

| | |
|---|---|
| Source | `https://github.com/llccd/TermWrap`, commit `34029fcd46de1db06b7f0815b99a765e4c7ca471` (tag `v0.7`), checked by hash |
| Submodules | `zydis` at `938b5158fd7db5043f88285b23470c8b3b02108a`, and its `dependencies/zycore` at `75a36c45ae1ad382b0f4e0ede0af84c11ee69928` |
| Toolchain | MSBuild, VS 2022 (v143), x64 Release |
| Zydis | `zydis\msvc\zydis\Zydis.vcxproj`, configuration `Release MT DLL`: `Zydis.dll` with the static CRT (`/MT`) |
| Wrappers | `TermWrap.sln`, targets `TermWrap`, `UmWrap` and `EndpWrap`, `Release|x64`, unchanged upstream settings |

The wrappers need no VC++ redistributable. Upstream's Release configuration does not use the VC runtime at all: it
links the system `msvcrt.dll` through `TermWrap\msvcrt.def`, with its own `DllMain` entry point. Upstream ships the
`Release MD DLL` build of Zydis, which needs `vcruntime140.dll`; we build Zydis with `/MT` instead. CI checks every
DLL with `dumpbin /dependents` and fails if one imports `vcruntime*`, `msvcp*` or `concrt*`.

Imports of the shipped DLLs, as measured in CI:

| DLL | Imports |
|---|---|
| `TermWrap.dll` | `Zydis.dll`, `KERNEL32.dll`, `ADVAPI32.dll`, `msvcrt.dll` |
| `UmWrap.dll` | `Zydis.dll`, `KERNEL32.dll`, `msvcrt.dll` |
| `EndpWrap.dll` | `Zydis.dll`, `KERNEL32.dll`, `msvcrt.dll` |
| `Zydis.dll` | `KERNEL32.dll` |

## Files in `native-win-x64/termwrap/`

| File | Purpose |
|---|---|
| `TermWrap.dll` | Replaces `termsrv.dll` as `TermService`'s `ServiceDll`; loads the real `%SystemRoot%\System32\termsrv.dll` and patches it in memory |
| `UmWrap.dll` | Replaces `umrdp.dll` as `UmRdpService`'s `ServiceDll` (Home and Server editions only) |
| `EndpWrap.dll` | Optional: audio *recording* redirection on Home and Server editions (see below). SplitPlay does not install it |
| `Zydis.dll` | Disassembler used by all three wrappers; must sit next to them |
| `termwrap-probe.exe` | Upstream's `TermWrapTest` project built with our `probe.cpp` as its main: runs TermWrap's patch search on a private copy of `termsrv.dll` and prints what TermWrap reports (see below) |
| `LICENSE` | TermWrap's MIT licence |
| `LICENSE.zydis` | Zydis' MIT licence |
| `VERSION.txt` | The TermWrap and Zydis commits the files were built from |

The installer puts them in `{app}\termwrap\`. `TermWrap.dll` and `UmWrap.dll` load `Zydis.dll` from their own folder.

## Registry values set by TermWrap's install files

TermWrap v0.7 ships these files in its release asset `TermWrap-0.7.zip`. They are reproduced verbatim below, because
`splitplay host prepare` must set the same values, with the path changed to `{app}\termwrap\`. The `hex(2)` data is
`REG_EXPAND_SZ` in UTF-16LE with a terminating NUL. The decoded string is given under each file.

TermWrap's own instructions are: copy the DLLs to `%ProgramFiles%\RDP Wrapper\`, merge
`Install_termwrap_umwrap.reg` or `Install_termwrap_only.reg`, then reboot. To uninstall, merge
`Revert_to_default.reg`, reboot, then delete the files.

### `Install_termwrap_only.reg` (Pro, Enterprise, Education)

```reg
Windows Registry Editor Version 5.00

[HKEY_LOCAL_MACHINE\SYSTEM]

[HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet]

[HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services]

[HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\TermService]

[HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\TermService\Parameters]
"ServiceDll"=hex(2):25,00,50,00,72,00,6f,00,67,00,72,00,61,00,6d,00,46,00,69,\
  00,6c,00,65,00,73,00,25,00,5c,00,52,00,44,00,50,00,20,00,57,00,72,00,61,00,\
  70,00,70,00,65,00,72,00,5c,00,54,00,65,00,72,00,6d,00,57,00,72,00,61,00,70,\
  00,2e,00,64,00,6c,00,6c,00,00,00
```

- `HKLM\SYSTEM\CurrentControlSet\Services\TermService\Parameters`, `ServiceDll` (REG_EXPAND_SZ):
  `%ProgramFiles%\RDP Wrapper\TermWrap.dll`

### `Install_termwrap_umwrap.reg` (Home and Server editions)

```reg
Windows Registry Editor Version 5.00

[HKEY_LOCAL_MACHINE\SYSTEM]

[HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet]

[HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services]

[HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\UmRdpService]

[HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\UmRdpService\Parameters]
"ServiceDll"=hex(2):25,00,50,00,72,00,6f,00,67,00,72,00,61,00,6d,00,46,00,69,\
  00,6c,00,65,00,73,00,25,00,5c,00,52,00,44,00,50,00,20,00,57,00,72,00,61,00,\
  70,00,70,00,65,00,72,00,5c,00,55,00,6d,00,57,00,72,00,61,00,70,00,2e,00,64,\
  00,6c,00,6c,00,00,00

[HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\TermService]

[HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\TermService\Parameters]
"ServiceDll"=hex(2):25,00,50,00,72,00,6f,00,67,00,72,00,61,00,6d,00,46,00,69,\
  00,6c,00,65,00,73,00,25,00,5c,00,52,00,44,00,50,00,20,00,57,00,72,00,61,00,\
  70,00,70,00,65,00,72,00,5c,00,54,00,65,00,72,00,6d,00,57,00,72,00,61,00,70,\
  00,2e,00,64,00,6c,00,6c,00,00,00
```

- `HKLM\SYSTEM\CurrentControlSet\Services\UmRdpService\Parameters`, `ServiceDll` (REG_EXPAND_SZ):
  `%ProgramFiles%\RDP Wrapper\UmWrap.dll`
- `HKLM\SYSTEM\CurrentControlSet\Services\TermService\Parameters`, `ServiceDll` (REG_EXPAND_SZ):
  `%ProgramFiles%\RDP Wrapper\TermWrap.dll`

### `Revert_to_default.reg`

```reg
Windows Registry Editor Version 5.00

[HKEY_LOCAL_MACHINE\SYSTEM]

[HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet]

[HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services]

[HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\UmRdpService]

[HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\UmRdpService\Parameters]
"ServiceDll"=hex(2):25,00,53,00,79,00,73,00,74,00,65,00,6d,00,52,00,6f,00,6f,\
  00,74,00,25,00,5c,00,53,00,79,00,73,00,74,00,65,00,6d,00,33,00,32,00,5c,00,\
  75,00,6d,00,72,00,64,00,70,00,2e,00,64,00,6c,00,6c,00,00,00

[HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\TermService]

[HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\TermService\Parameters]
"ServiceDll"=hex(2):25,00,53,00,79,00,73,00,74,00,65,00,6d,00,52,00,6f,00,6f,\
  00,74,00,25,00,5c,00,53,00,79,00,73,00,74,00,65,00,6d,00,33,00,32,00,5c,00,\
  74,00,65,00,72,00,6d,00,73,00,72,00,76,00,2e,00,64,00,6c,00,6c,00,00,00
```

- `HKLM\SYSTEM\CurrentControlSet\Services\UmRdpService\Parameters`, `ServiceDll` (REG_EXPAND_SZ):
  `%SystemRoot%\System32\umrdp.dll`
- `HKLM\SYSTEM\CurrentControlSet\Services\TermService\Parameters`, `ServiceDll` (REG_EXPAND_SZ):
  `%SystemRoot%\System32\termsrv.dll`

The archive also contains `Revert_to_rdpwrap.reg`, which points `TermService` back at
`%ProgramFiles%\RDP Wrapper\rdpwrap.dll`. SplitPlay does not use it.

## What `splitplay host prepare` should do

This mirrors the `.reg` files, with SplitPlay's own install path.

1. Record the current `ServiceDll` values of `TermService\Parameters` and `UmRdpService\Parameters`, including the
   type, normally REG_EXPAND_SZ. Store them in `%ProgramData%\SplitPlay\host-tuning.json`.
2. Set `HKLM\SYSTEM\CurrentControlSet\Services\TermService\Parameters\ServiceDll` (REG_EXPAND_SZ) to
   `{app}\termwrap\TermWrap.dll`. Use the absolute path, or `%ProgramFiles%\SplitPlay\termwrap\TermWrap.dll` for the
   default location.
3. On Home and Server editions only, also set
   `HKLM\SYSTEM\CurrentControlSet\Services\UmRdpService\Parameters\ServiceDll` (REG_EXPAND_SZ) to
   `{app}\termwrap\UmWrap.dll`. Pro, Enterprise and Education editions already enable those features.
4. Report "reboot required". `TermService` keeps the old DLL loaded until it restarts, and a reboot is the reliable
   way to restart it.

`revert` restores the recorded values. Without a record, it falls back to the defaults from `Revert_to_default.reg`
(`%SystemRoot%\System32\termsrv.dll` and `%SystemRoot%\System32\umrdp.dll`). Then it reports "reboot required".

`status` compares the registry value with the `ServiceDll` that the running `TermService` actually loaded, which
`sc qc` does not show. If they differ, it reports "reboot required". One way to read the loaded DLL is to check
whether `TermWrap.dll` appears in the module list of `TermService`'s svchost process.

### RDP listener settings the seat needs

These are not part of TermWrap, but `host prepare` sets them at the same time. All of them are under
`HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server`, and all are measured in CI (`native.yml`, RDP step):

| Value | Set to | Why |
|---|---|---|
| `fDenyTSConnections` | `0` | Enables the RDP listener |
| `WinStations\RDP-Tcp\UserAuthentication` | `1` | NLA (the seat only does NLA) |
| `WinStations\RDP-Tcp\fInheritColorDepth` | `1` | Uses the colour depth the client asks for (32 bpp) |
| `WinStations\RDP-Tcp\ColorDepth` | `5` | Raises the cap to 32 bpp. The default `3` with `fInheritColorDepth=0` gives the desk session **16 bpp**. With `fInheritColorDepth=1` alone, it stays at 16 bpp |

TermService reads these values only when it starts, so the reboot after `host prepare` covers them. The seat logs
the depth it got (`RDP: session colour depth N bpp`). Like `ServiceDll`, `revert` restores the recorded values.

### EndpWrap (optional, not installed by default)

Upstream's notes cover audio recording (microphone) redirection on Home and Server editions. It needs `EndpWrap.dll`
and `Zydis.dll` copied to `System32` (and the x86 builds to `SysWOW64`). It also needs
`HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp\AudioEnumeratorDll` changed from
`rdpendp.dll` to `EndpWrap.dll`. SplitPlay streams audio out of the desk only (rdpsnd playback), so it does not need
this. Only the x64 build is shipped.

## Surviving Windows updates

Windows updates break TermWrap in two ways. A feature update writes TermService's `ServiceDll` back to `termsrv.dll`.
A cumulative update may ship a `termsrv.dll` in which TermWrap does not find its patch points. Either way the next
desk logon would end the console session or fail with an obscure error. The service's `TermWrapGuard`
(`service/src/SplitPlay.Core/Host/TermWrapGuard.cs`) runs when the service starts, every 5 minutes, and before each
desk start:

1. If a recorded TermWrap `ServiceDll` differs, it is written back (only on a host prepared with TermWrap) and the
   change is marked "reboot required", as after `host prepare`.
2. Once per `termsrv.dll` version (and probe build), `termwrap-probe.exe` runs. Its verdict is cached in
   `%ProgramData%\SplitPlay\termwrap-check.json`. Messages from the patches concurrent sessions need
   (`DefPolicyPatch…`, `CDefPolicy_Query not found`, `CSLQuery_Initialize not found`, `bInitialized not found`) or a
   crash mean "unsupported". The other messages (single session per user, licence type, PnP, non-RDP stack) are
   only logged.
3. The running TermService must have `TermWrap.dll` loaded; if not, a reboot is required.

The result is one line in the web UI (`host.warnings`: `termWrapReboot`, `termWrapUnsupported`, `termWrapMissing`,
`termWrapRepairFailed`) and in the tray, and the same reason in a desk's `lastError` when it is started.

The probe prints `probe: termsrv <path>`, TermWrap's messages one per line, then `probe: done`. Exit codes: 0 the
search ran to the end, 1 `termsrv.dll` cannot be loaded, 3 the search crashed. CI runs it on the runner's
`termsrv.dll` through a pipe.
