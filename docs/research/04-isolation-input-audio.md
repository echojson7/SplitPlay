# Research 4: session isolation, input, gamepads, audio, supervision
[V]=verified against cited source, [U]=unverified.

## 1. Keyboard/mouse
- Physical input belongs to console session; RDP sessions get "remote" devices from protocol provider [V] (learn.microsoft.com/en-us/windows/win32/termserv/consoles-vs-terminals). Host KB/mouse never reach RDP-loopback sessions.
- SendInput is per-session (win32k per-session input queue) [U, but Chrome Remote Desktop & Sunshine rely on it]. Subject to UIPI: injector must be high/System integrity in target session [V] (SendInput docs).
- Virtual HID KB/mouse (Sunshine master libvirtualhid, UMDF2+VHF) are real HID devices → routed to console session → leak to host. Use SendInput for extra desktops [V/U].
- Raw Input: SendInput produces WM_INPUT with hDevice=0, relative deltas OK [U]. GetRawInputDeviceList can be empty in RDP sessions [V, single forum report OSR]. SDL disables RawInput gamepads under RDP, falls back to XInput [V].
- Secure desktop: per-session winlogon; agent must be SYSTEM-token in session and OpenInputDesktop/SetThreadDesktop before inject (Chrome Remote Desktop session_input_injector.cc) [V]. Sunshine service launches sunshine with duplicated LocalSystem token with TokenSessionId set [V] (tools/sunshinesvc.cpp).
- Ctrl+Alt+Del via SendSAS (service caller + software SAS GPO; goes to impersonated token session) [V].
- Cursor: with RdpIdd/IddCx frames, cursor shape from IddCx HW cursor or GetCursorInfo in session [U].

## 2. Gamepads
- ViGEmBus retired/archived after 2023 trademark settlement [V] (docs.nefarius.at/projects/ViGEm/End-of-Life/). Devices are system-wide.
- XInput: global 4-slot pool shared across all sessions [V] (Duo wiki Known-Issues).
- Official isolation: DEVPKEY_Device_SessionId — nonzero → device accessible only in that TS session; set via SetupDiSetDeviceProperty [V]. Duo stamps it on all devnodes in container + disable/enable. Caveats [V]: XInput (xusb) pads can't be restarted in software without reboot; processes started before stamping still see device; ControllerToVKMapping ignores session attribute — Duo disabled it (v1.5.1).
- Duo now [V releases]: v1.5.7 replaced ViGEmBus with "Microsoft's synthetic controller API" (likely InputInjector.InitializeGamepadInjection [U], needs inputInjectionBrokered restricted capability [V]); process patching for "XInput driver focus limitations"; v1.5.8 DualSense Edge via custom UMDF driver; v1.5.9 DS4/DualSense, Elite paddles via GameInput.
- libvirtualhid (LizardByte, UMDF2/VHF): Xbox One/Series, DS4, DualSense, Switch Pro as HID; no XUSB/X360 via VHF; XInput enumerates Xbox profile; no session awareness; device lifetime tied to creating handle [V].
- GameInput redistributable now works in remote sessions (GDC 2025) [V].
- Physical host pads: reWASD says physical HID not seen in RDP sessions [V vendor forum]; Duo says host pads "may bleed" esp. XInput [V].
- APIs to cover: XInput, DirectInput, Windows.Gaming.Input/GameInput, SDL2/3 (HIDAPI/RawInput, XInput fallback under RDP), Steam Input (needs isolation too; Duo lists "Steam isolation" fixes).

## 3. Audio
- RDP audio per session: audiomode:i:0 → session renders to its own "Remote Audio" endpoint; 1 → host devices (leak); 2 → none [V] (AVD rdp-properties). Isolation needs audiomode=0 + capture inside session.
- Remote Audio is stereo 44.1 kHz only; Duo patches rdpendp.dll (CRdpEndpoint::ValidateWaveFormatEx) + registry for 5.1/7.1 48 kHz; blocked by process-signature mitigations; global setting [V] (Duo #382, Known Issues).
- Sunshine: WASAPI loopback of default render endpoint, prefers "Steam Streaming Speakers" sink; captured only when default [V] (Sunshine #4865). Global virtual sinks visible to all sessions → leakage risk [U].
- Process loopback (AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK, include/exclude tree): device-independent [V]; sample targets build 20348+, reportedly Win11 only [V]; GetMixFormat E_NOTIMPL → force format [V]; surround unproven [U]; whether exclude-mode is session-scoped unknown [U]; process trees break with launchers.
- Latency: WASAPI shared loopback ~10–20 ms; RDP adds buffering [U].
- Mic: upstream Moonlight has no mic channel; forks exist (moonlight-mic, Apollo PR #1428) [U]. Host options: RDP audiocapturemode:i:1 or session-scoped virtual mic driver [U].

## 4. Supervision
- sunshinesvc pattern [V]: polls every 3 s; duplicates SYSTEM token, sets TokenSessionId, CreateProcessAsUserW on winsta0\default; one Job object per process ("Job objects cannot span sessions"), kill-on-close + breakaway; relaunch on crash/console session change; graceful CTRL_C, 20 s timeout.
- WTSQueryUserToken requires LocalSystem + SE_TCB [V] — use for user-context launches; SYSTEM-in-session token for the input/capture agent.
- Precedent: Chrome Remote Desktop curtain mode creates RDP session via localhost RDP through COM RDP client; per-session desktop process elevated with uiAccess [V]. Duo: headless localhost RDP + in-memory patches to termsrv.dll, IddCx.dll, RdpIdd.dll [V] (Known Issues).
- Disconnect/logoff [U]: when loopback client drops, session disconnects, display & Remote Audio vanish, injection fails → service must own & supervise loopback RDP client per desktop; disable MaxDisconnectionTime/MaxIdleTime; mind fSingleSessionPerUser.
- Auto-logon: Winlogon AutoAdminLogon only console [U]; extra users logged on by service opening loopback RDP sessions with credentials in LSA secret; Duo adds users to Remote Desktop Users [V]; boot-order RDP bugs (Duo #339) [V].

## 5. Recommendations
- KB/mouse: per-session agent with SYSTEM token in session, SendInput (relative, scancodes), SetThreadDesktop to input desktop; SendSAS from service. No virtual HID KB/mouse for extra desktops. Low–Med complexity; risk: anti-cheat rejecting LLMHF_INJECTED [U], RDP mouse accel quirks.
- Gamepads: custom UMDF/VHF virtual pad driver (fork/reuse libvirtualhid); service stamps DEVPKEY_Device_SessionId at creation (before start); verify across XInput/DInput/WGI/GameInput/SDL; present DS4/DualSense/XboxOne-HID; stamp physical host pads to console (XUSB needs replug/reboot); disable ControllerToVKMapping; XInput 4-slot cap & focus are known limits. High complexity (driver + signing), high risk.
- Audio: v1 in-session WASAPI loopback on session's Remote Audio (audiomode=0), stereo; no global virtual sinks. v2 evaluate process loopback. Surround later (rdpendp patch or session-scoped sink). Mic later. Med/Med.
- Supervision: SYSTEM service, per-desktop state machine: user exists → loopback RDP client → session id → agent (SYSTEM-in-session, one job per session, exponential backoff); per-desktop streaming instance & ports as separate processes; named-pipe health; WTSQueryUserToken for user launches. Med/Med.

## 6. Risks
1. Concurrent sessions on client SKUs need termsrv patching: EULA violation, breaks with CUs (Duo ships per-KB fixes), AV heuristics; "totally unsupported by Microsoft" [V] (virtualhere.com/node/4089).
2. XInput global 4-slot pool & focus model; xusb ignores SessionId; some host-pad leakage unfixable without reboot.
3. Driver signing (UMDF needs attestation/EV).
4. Anti-cheat: injected input, RDP session detection, patched system DLLs [U]; Duo added "refuses remote sessions" workaround (v1.5.5) [V].
5. Audio stereo-only, global settings; process loopback cross-session unknown.
6. Games/launchers refusing non-console sessions or using Raw Input enumeration.

## Open questions
- Does SessionId on VHF child devnode hide pad from XInput/DInput/WGI/GameInput/SDL-HIDAPI in other sessions?
- Does process-loopback exclude-tree capture other sessions' audio?
- What exactly is Duo's synthetic controller API; does XInput see it?
- Does XInput deliver input to a non-console session's foreground window?
- Is Remote Audio available when the headless loopback client drops audio channel?

Key sources: github.com/DuoStream/Duo (+wiki Known-Issues, releases, #382, #339); learn.microsoft.com devpkey-device-sessionid; InputInjector; SendInput; SendSAS; WTSQueryUserToken; docs.lizardbyte.dev libvirtualhid; LizardByte/Sunshine tools/sunshinesvc.cpp, src/platform/windows/input.cpp, #4865; chromium remoting session_input_injector.cc, desktop_session_win.cc; docs.nefarius.at ViGEm EOL; GDC 2025 GameInput; AVD rdp-properties; applicationloopbackaudio sample.
