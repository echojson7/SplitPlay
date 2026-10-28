# Research 1: isolated desktops, capture, virtual display
[n] = source numbers listed at bottom. (unverified) marked. Fetch budget ran out near end.

## 1. N interactive sessions on client Windows
- Duo [1][2][3]: TermWrap-style in-memory termsrv.dll patch (TermWrap = RDPWrap rewrite finding offsets at runtime [4]); adds users to Remote Desktop Users; headless localhost RDP connections via Python `aardwolf` library [3]; patches IddCx.dll + RdpIdd.dll in memory to capture uncompressed swap-chain frames [1]; restarts TermService, removes RDP Wrapper; recovery logs off all non-console sessions (session IDs not stable) [3]; NLA required → passwords; MS accounts often fail NLA, no Kerberos [5]; min Win11 24H2 [6].
- OpenStreamMS PR #2 merged 2026-10-27 [7] (maturity unverified): still needs TermWrap for concurrency; registers provider as RDP-Tcp LoadableProtocol_Object wrapping Microsoft rdpcorets; seat connections via loopback 127.0.0.2; answers IWRdsWddmIddProps with its own remote IddCx 1.4 driver, passes session ID + resolution via IOCTL; frames to Sunshine via shared NT-handle texture + keyed mutex; no DDA/WGC/hooks/RdpIdd patch.
- Windows Server + RDS: legal with CALs; HW GPU rendering needs policy "Use hardware default graphics adapter for all RDS sessions" (bEnumerateHWBeforeSW) [8][9]; consumer GPU drivers on Server (unverified).
- Win11 Enterprise multi-session: forbidden outside AVD [10].
- CreateDesktop in one session insufficient: one DWM, display config, audio session; one input desktop per winsta (SwitchDesktop) [11, own knowledge].
- Licensing: Win11 terms §2(c)(iv) work around restrictions, §2(c)(v) simultaneous use over network / server for remote access; §2(d)(v) remote access for licensed user [12]. termsrv patch very likely EULA breach [13]. Clean: Server+RDS CALs or legal opinion.

## 2. Non-console session display & capture
- Each remote session gets a remote WDDM IDD instance (up to 16 monitors); Microsoft's is RdpIdd ("Microsoft Remote Display Adapter"), since GDI remote display drivers dropped in Win10 2004 [14][15]. Physical GPU monitors always console; GPU enumerated in remote session without monitors [14]. Rendering in remote session: WARP or render GPU paired with remote IDD per SKU default/GPO [14]. "Use hardware default graphics adapter" affects only default GPU on multi-GPU [9]. "Use WDDM graphics display driver for RDC" must not be disabled (XDDM breaks Duo capture) [5].
- DirectX games GPU-accelerated but NO exclusive fullscreen; Duo workarounds borderless/DXVK/Borderless Gaming [5]; app-compat shims for games detecting remote sessions [16].
- DWM in remote sessions capped 30 fps by default; HKLM\...\Terminal Server\WinStations\DWMFRAMEINTERVAL=15 → 60 fps [17]. Integer ms [7]: 500 Hz=2, 250 Hz=4, 240 not exact; OpenStreamMS composes at 250 Hz and paces in encoder. Value=2 → 500 Hz (unverified), Duo advertises 500 Hz [6]. Likely global, not per session (unverified).
- DDA: max 4 duplicating processes per session; DXGI_ERROR_SESSION_DISCONNECTED when disconnected; UNSUPPORTED in non-DWM [18]; reported to break after RDP reconnect [19]; on RdpIdd outputs (unverified); RDP stack would still encode frames for loopback client (waste).
- WGC in RDP sessions: unverified.
- IDD-side capture (Duo patched RdpIdd, OpenStreamMS own remote IDD) = only field-proven approach.
- RDP graphics pipeline (AVC/RemoteFX) unusable for Moonlight.

## 3. IddCx virtual display
- Console IDDs (VDD, SudoVDA, parsec-vdd) cannot attach to non-console session [14].
- Remote session IDD: IDDCX_ADAPTER_FLAGS_REMOTE_SESSION_DRIVER (IddCx 1.4); cannot support console monitors; IddCxAdapterInitAsync fails unless devnode created by OS remote desktop stack for that session [20][21]; OS creates one remote adapter devnode per session, new on each reconnect. Must set USE_SMALLEST_MODE, progressive only, ProcessSharingDisabled → each session's IDD in own UMDF host process (crash isolation) [21].
- Modes via IddCxDisplayConfigUpdate; ChangeDisplaySettings ignored in WDDM remote sessions [21]; Duo uses own shared-memory API from its Sunshine fork for resolution/refresh/colour space [22].
- Microsoft's recommended replacement: custom Remote Protocol Provider + IDD [15][23] (= OpenStreamMS design).
- Apollo/SudoVDA: per-client virtual display matching res/fps, HDR 23H2+ [24]. VDD: custom modes, float refresh, IddCx 1.10 HDR, render GPU by LUID [25][26]; fork lists up to 480/500 Hz [27]. parsec-vdd up to 240 Hz [28]. All console-only.
- Refresh: rational vsync; 500 Hz modes exist [27][6]; IddCx 1.10 remote drivers can set REMOTE_ALL_TARGET_MODES_MONITOR_COMPATIBLE [20]; no OS cap found (unverified); practical limit DWMFRAMEINTERVAL.
- HDR: IddCx 1.10 CAN_PROCESS_FP16; remote drivers must supply colorimetry and SDR white level [20]; Win11 23H2+ [24][25].
- Zero-copy: IddCxAdapterSetRenderAdapter picks render GPU (preference, changeable, console and remote) [29]; frames via IddCxSwapChainReleaseAndAcquireBuffer(2) [30]; buffers must be returned → one GPU copy into shared texture ring; OpenStreamMS p99 present-to-pixel ≈0.6 ms at 4K240 [7].

## 4. Recommendation
1. Console user A not captured (if ever needed: console IDD + DDA/WGC).
2. Each remote desktop: termsrv loopback session + SplitPlay's own remote IDD via protocol-provider shim on RDP-Tcp (OpenStreamMS-like). Avoid Duo-style in-memory RdpIdd/IddCx patching. IDD: Moonlight-driven mode via IddCxDisplayConfigUpdate, FP16/HDR, GPU copy into 2–3 slot shared texture ring (NT handle, keyed mutex/fence), signal per-session encoder process (SYSTEM, Sunshine-derived). Service in session 0 supervises.
3. Pacing: DWMFRAMEINTERVAL=2; IDD only gets frames on change → encoder paces/repeats at client fps. Disable RDP stack encoding for seat connections.
4. GPU per desktop: IddCxAdapterSetRenderAdapter(LUID) before monitor arrival; per-user HKCU UserGpuPreferences for games (remote-session behaviour unverified); encode on same adapter; at 500 Hz encoder is bottleneck [6].

## 5. Risks
EULA (§2(c)(iv),(v)); multi-session outside AVD forbidden; fragility across updates (termsrv, rdpcorets, IddCx); AV false positives; OpenStreamMS pins TermWrap hashes, disables asmtron auto-update task [7]; no exclusive fullscreen; anti-cheat may block remote sessions (unverified); session churn (reconnect destroys IDD devnode [21], unstable session IDs [3]); XInput 4 pads shared; HID isolation via SessionId; remote audio stereo [1]; frame interval integer ms; DDA 4 processes; UMDF IDD signing (VDD uses SignPath) — attestation requirement unverified; WARP fallback without HW rendering policy [8][14].

## Open questions
DWMFRAMEINTERVAL=2 → 500 Hz? global or per session? DDA/WGC in loopback RDP sessions? min OS for remote IDD FP16 HDR, cursor & HDR metadata? per-session render GPU for games? protocol provider on client SKU still needs termsrv patch (OpenStreamMS: yes)? signing for remote IDD? Server+RDS as "pro" SKU?

## Sources
1 github.com/DuoStream/Duo/wiki/Known-Issues; 2 github.com/DuoStream/Duo; 3 DuoStream/Duo/issues/186; 4 deepwiki.com/cpdx4/TermWrap; 5 Duo wiki Troubleshooting; 6 Duo wiki + Setup-Guide; 7 github.com/mlopezsegura/OpenStreamMS/pull/2; 8 docs.nvidia.com/vgpu/19.0/known-issues/bug-no-id-no-gpu-utilization-windows-server-guests.html; 9 gp.kapilarya.com use-the-hardware-default-graphics-adapter...; 10 learn.microsoft.com azure/virtual-desktop/windows-multisession-faq; 11 learn.microsoft.com winstation/desktops (search only); 12 microsoft.com UseTerms OEM Windows 11; 13 ncomputing.com rdp-wrapper-safe; 14 learn.microsoft.com termserv/consoles-vs-terminals; 15 learn.microsoft.com windows-hardware/drivers/display/remote-display-drivers; 16 Duo wiki Settings; 17 learn.microsoft.com troubleshoot/windows-server/remote/frame-rate-limited-to-30-fps; 18 learn.microsoft.com idxgioutput1-duplicateoutput; 19 MS Q&A 1068807 (title only); 20 learn.microsoft.com iddcx ne-iddcx-iddcx_adapter_flags; 21 learn.microsoft.com display/iddcx1.4-updates-for-remote-idds; 22 DuoStream/Duo/issues/240; 23 learn.microsoft.com termserv/custom-remote-protocol-interfaces; 24 github.com/ClassicOldSong/Apollo; 25 github.com/VirtualDrivers/Virtual-Display-Driver; 26 mintlify VDD architecture; 27 github.com/timminator/Virtual-Display-Driver; 28 github.com/nomi-san/parsec-vdd; 29 ntdoc.m417z.com/iddcxadaptersetrenderadapter; 30 learn.microsoft.com iddcxswapchainreleaseandacquirebuffer2 (search only); 31 blog.usro.net GPU priority registry (search only).
