# Research 6: independent review of reports 01–05

Reviewer checked key claims against local clones (Sunshine d7e5fe0 2026-10-28, moonlight-qt, moonlight-common-c, libvirtualhid, Apollo/Vibepollo, OpenStreamMS 33794bf, nefarius/HidHide, Nonary/libvirtualgamepad) and 4 web lookups.

## Plan-changing findings
1. libvirtualhid is unusable: Windows UMDF driver, broker and entitlement sources are under LizardByte Source-Available License 1.0 (view/non-commercial evaluation only; no forks, redistribution, commercial or "Competitive Purpose" use). SAL-licensed headers are compiled into Sunshine's Windows build (windows_license.cpp → lvh_windows_broker_config.hpp → lvh_broker_license_policy.hpp; windows_backend.cpp → keyboard/mouse_protocol.hpp, SPDX LicenseRef-LizardByte-SAL-1.0). A distributed fork must remove the libvirtualhid Windows backend or base on a pre-libvirtualhid commit.
2. The own-remote-IDD route still needs a termsrv patch (TermWrap NonRDPPatch) on client Windows; first real-session success 2026-10-27 — R&D, not field-proven.
3. libvirtualgamepad (Nonary, MIT, active 2026-10-24): in-box VHF from UMDF2, no custom kernel bus; Xbox One/Series (Series HWID loads in-box xinputhid → XInput sees it), DS4, DualSense, Switch Pro; released when owning handle closes. No X360/XUSB.

## Corrections
| Claim | Verdict | Evidence |
|---|---|---|
| Reuse libvirtualhid for pads | Wrong | SAL-1.0, see above |
| Use ViGEm? | Don't ship | EOL 2023; X360 = XUSB ignores session isolation, can't restart in software; OpenStreamMS moved to DS4 targets; use libvirtualgamepad |
| GeForce NVENC limit 8 vs 12 | 12 per system confirmed | NVENC app note 13.1; driver load-balances across engines; one session ≤ one engine without SFE; OBS/ShadowPlay/Sunshine probing count |
| OpenStreamMS remote IDD field-proven, p99 0.6 ms 4K240 | Overstated | 0.6 ms from synthetic RemoteIddPerf; first real session 2026-10-27, AMD, 1080p, 60 Hz TV; no HDR, no HW cursor; one session hung (logoff/rwinsta failed, reboot, orphaned WUDFHost) |
| Remote-session IDD needs custom protocol provider | Confirmed, nuance | Provider wraps rdpcorets (RDP-Tcp CLSID), delegates all, handles only IWRdsWddmIddProps(1)::GetHardwareId and OnDriverLoad. (i) client SKUs: termsrv refuses non-MS stacks (IsAllowNonRDPStack) → TermWrap NonRDPPatch required; (ii) separate listener not started on Pro (event 1136) → provider on RDP-Tcp filtering loopback; (iii) IDDCX_TRANSMISSION_TYPE_NETWORK_OTHER rejected by 26100 → use OTHER; (iv) OnDriverLoad not always called → derive session from instance ID (…&SessionId_000N); (v) OnDriverLoad gets interface path; (vi) worker must be SYSTEM to open shared texture. Duo hybrid: DuoRds.dll provider + DuoIdd.dll injected into RdpIdd with offset hooks + own TermWrap |
| DWMFRAMEINTERVAL sets fps; 500 Hz reachable | Mechanism confirmed; 500 Hz unverified | integer ms; OpenStreamMS measured 17→58 Hz, 15→66 Hz; machine-global; Citrix KB applies to VDA → likely limits custom stacks; value 2 untested publicly; OpenStreamMS caps at 4 (250 Hz) due to compose cost, claims <2 ignored; steps 500/333/250 Hz; 60/120/144/240 need faster compose + encoder pacing; cost × N |
| DEVPKEY_Device_SessionId isolates pads | Documented; unverified for VHF/xinputhid children | HidHide (MIT, active 2026-07) per-session jail: blacklist HID\…!N, Logic.c Blacklisted() compares PsGetProcessSessionId; open-time only. Plan: stamp SessionId before start + HidHide backstop |
| Per-session audio via Remote Audio | Confirmed with gaps | stereo 44.1k; headless client must advertise rdpsnd; client in session 0 must use null sound backend or host speakers play; disable Sunshine virtual_sink (Steam Streaming Speakers) |
| HEVC level / Moonlight limits 1080p500 | Confirmed, refined | HEVC L5.2 ≈515 fps 1080p; H.264 needs L6.0 (4,177,920/8160 ≈ 512); Sunshine NVENC sets no level (auto); AMF forces 5.1/5.2 or auto; moonlight-qt fps 10–9999, bitrate 150 (500 unlocked); WGC 250 Hz cap irrelevant with IDD capture |
| GPL boundary via IPC | Confirmed if arm's-length | CLI args, config files, HTTP/JSON, simple named-pipe messages; never link Sunshine into service; ship worker source; IDD/provider separate works (MS-PL sample outside GPL); excise SAL headers. TermWrap MIT, FreeRDP Apache-2.0, HidHide MIT, libvirtualgamepad MIT |
| termsrv patch EULA breach; Server RDSH clean | Confirmed; Server niche "pro" | needs Server core licences + RDS CALs; GeForce Game Ready not officially supported on Server (cloud vendors ship images); Store/Game Pass/some anti-cheat not on Server; whether Server loads non-MS stack without patch unverified |
| DDA in RDP sessions | Works as fallback | OpenStreamMS default path: Sunshine DDA/WGC inside RdpIdd sessions ~58–66 Hz; limited by RDP flow control; loopback RDP encode shares HW encoder; AVC420 micro-stutter. MVP only |
| Stack | .NET 10 service/UI; native for TermService-loaded code | provider DLL in TermService svchost must be small native C++ (no CLR); IDD + gamepad C++/UMDF; worker C++/MinGW |
| Session creation needs aardwolf | Not needed | OpenStreamMS uses FreeRDP from session 0; CRD uses mstscax COM; recommended libfreerdp "seat connector" per desktop |

## Missed items
- Crash-isolation ceiling: holds for user-mode (game, worker, session dwm, IDD WUDFHost with ProcessSharingDisabled); not for TermService crash/restart/patch re-apply, GPU TDR (all sessions incl console), kernel drivers, IDD teardown deadlock.
- TermService SPOF; RDP Wrapper autoupdate task restoring rdpwrap.dll as ServiceDll.
- Windows update cadence: CUs replace termsrv/rdpcorets/IddCx; TermWrap Zydis runtime patching; Release Preview CI ring; startup self-test; fallback to console-only.
- HVCI/Secure Boot/Smart App Control: TermWrap user-mode; svchost signature mitigation risk (cf. audiodg, Duo #382); test-signing dev only; EV attestation for all drivers.
- Anti-cheat: SM_REMOTESESSION, foreign DLL in TermService, LLMHF_INJECTED, virtual HID.
- Input via loopback RDP connection: genuine session devices (Raw Input hDevice≠0, not injected) — test.
- Session 0: UI via tray/web; worker SYSTEM in session → harden (localhost admin, per-instance creds).
- NLA: service-created local accounts, random passwords in LSA secrets; MS accounts fail.
- Firewall: per-instance ranges stride 100; restrict 3389 to loopback; disable UPnP; unique mDNS names.
- Redirection leaks: disable clipboard, drives, printers, USB, camera, mic on loopback connection.
- Cursor: no HW cursor → DWM draws into frame (fine).
- No exclusive fullscreen/VRR in remote sessions.
- Steam/Steam Input isolation; game licences for concurrent play.
- XInput 4-slot machine-wide; prefer DS4/DualSense profiles for extra desktops.
- Windows Home unknown; target Win11 Pro/Enterprise 24H2 (26100)+; HDR needs IddCx 1.10.

## Ranked risks
1 Legal (EULA; SAL). 2 Windows updates. 3 Remote IDD is a day-old spike. 4 500 Hz: guarantee 250, 500 best effort 1–2 desktops Ada/Blackwell. 5 Anti-cheat/AV. 6 Gamepad gaps. 7 GPU contention, TDR. 8 Audio stereo/leak. 9 Driver signing lead time. 10 Upstream churn.

## Effort (3–4 engineers, one with driver/RDS internals), person-weeks
Skeleton 8–10; MVP one desktop 10–14; multi-desktop + Moonlight integration 6–8; remote IDD + provider production 12–16; audio hardening 3–4; gamepads 10–14; HDR 5–8; central pairing + GUI 6–8; stabilisation 14–20. Total ~74–102 (≈9–12 months calendar). Legal review external.

## Lab tests for MVP
1 DWMFRAMEINTERVAL 2/3/4 × IDD 500/333/250 Hz, N=1/2/4, DWM cost. 2 IDD lifecycle (reconnect, TermService restart, logoff, kill WUDFHost/worker/game). 3 Loopback RDP load RdpIdd vs own IDD. 4 SendInput vs RDP-path input (hDevice, LLMHF_INJECTED, latency via camera/LDAT). 5 Gamepad matrix (VHF profiles × SessionId/HidHide × XInput/DInput/WGI/GameInput/SDL3/Steam Input; host pads; XInput slots). 6 Audio endpoint/leak/latency/reconnect. 7 NVENC capacity, 12-session cap, engine balancing, console game impact with realtime+HAGS. 8 E2E 1080p500 (HEVC L5.2, H.264 L6.0 on moonlight-qt; pps/FEC; 1080p60 glass-to-glass ≤50 ms). 9 Update survival (Patch Tuesday, Release Preview). 10 Anti-cheat (EAC, BattlEye, Vanguard, FACEIT, Ricochet). 11 Defender/SAC/SmartScreen. 12 TDR blast radius. 13 Server 2025 RDSH. 14 Home edition; multi-GPU render adapter per session.
