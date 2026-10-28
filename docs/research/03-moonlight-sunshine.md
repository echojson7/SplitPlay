# Research 3: Moonlight/GameStream protocol; Sunshine fork vs own server
Sources read 2026-10-28 from shallow clones. [unverified] marked.

Bottom line: fork upstream LizardByte/Sunshine (not Apollo), one Sunshine worker per desktop in that user's session. Own server = several times longer, no gain on hard parts.

## 1. Protocol
No official spec. Client ref moonlight-common-c, server ref Sunshine, best written docs: Wolf (games-on-whales, MIT) docs/modules/protocols.
- nvhttp (src/nvhttp.cpp:1728-1749): HTTP on base port (47989), HTTPS base−5 (47984) mutual TLS with pinned client cert. Endpoints /serverinfo, /pair, /applist, /appasset, /launch, /resume, /cancel. serverinfo returns uniqueid, HttpsPort, ExternalPort, codecs, pair state.
- Pairing (nvhttp.cpp:752-1170): getservercert (salt+client cert; host waits for PIN; AES key = SHA-256(salt‖PIN)), clientchallenge, serverchallengeresp, clientpairingsecret (signature check), pairchallenge over HTTPS.
- Sunshine web API (confighttp.cpp:2491-2504): POST /api/pin; GET/DELETE /api/pin; GET /api/clients/list; POST /api/clients/unpair, /unpair-all.
- Launch: mode=WxHxFPS, rikey/rikeyid, surroundAudioInfo, HDR flags; returns sessionUrl0=rtsp://ip:port.
- RTSP TCP base+21 (48010): OPTIONS, DESCRIBE, SETUP audio/video/control, ANNOUNCE (SDP: x-nv-video[0].maxFPS, clientRefreshRateX100, bitrate, codec, FEC, RFI), PLAY. Sunshine extensions x-ss-general.featureFlags, encryptionSupported (rtsp.cpp:878-897). RTSP may be AES-GCM encrypted.
- Video UDP base+9 (47998): RTP + NV header, Reed-Solomon FEC (nanors) per block (RtpVideoQueue.c:261-332), optional AES-GCM (defaults lan_encryption_mode=0, wan=1, config.cpp:844). Codecs H.264, HEVC Main/Main10, Sunshine ext AV1 and 4:4:4 (Limelight.h:506-522).
- Control base+10 (47999): ENet/UDP, AES-GCM, 12-byte IV from seq (ControlStream.c:569-651). RFI/LTR acks, IDR requests, loss stats, pings, HDR metadata, rumble/trigger rumble/LED, all input packets. Input formats in Wolf input-data.adoc: keyboard, rel/abs mouse, scroll, hscroll, touch, pen, multi-controller with arrival packet (LI_CCAP_*), motion, battery, UTF-8 text.
- Audio UDP base+11 (48000): Opus 48k stereo/5.1/7.1 multistream; AES-CBC with rikey (stream.cpp:367-374) + RS FEC.
- Port discovery: HTTPS port from serverinfo, RTSP from sessionUrl0, A/V/control from server_port= in RTSP SETUP Transport (RtspConnection.c, Connection.c). Non-default port bases fully supported end to end.

## 2. Multiple hosts on one machine
- Sunshine `port` = base; offsets (network.cpp:243): HTTPS −5, HTTP 0, Web UI +1, Video +9, Control +10, Audio +11, RTSP +21. ~27 ports/instance → stride ≥100.
- Per instance: port, sunshine_name, file_state (uniqueid + paired clients), pkey/cert, apps file, log_path, credentials_file. Apollo wiki documents multi-instance recipe.
- Moonlight: qt manual add host:port (computermanager.cpp:729-742); Android AddComputerManually.java:136-143; iOS HttpManager.m:53-101 (e2e [unverified]). Hosts keyed by uuid = serverinfo uniqueid → distinct file_state → distinct PCs; per-host pinned cert.
- mDNS _nvstream._tcp: Windows Sunshine names instance after hostname not sunshine_name (network.cpp:260; platform/windows/publish.cpp:160-165) → collisions; DnsServiceRegister behaviour [unverified]. Patch to HOST-<desktop> or disable mDNS.
- Duo (closed source; README+wiki; Desomnia integration reads registry): embedded Sunshine per RDP session per local user; HKLM\SOFTWARE\Duo\Instances\<name> (Port, UserName, Sandboxed); manager web UI 38299; mic port base+13 (Duo ext); termsrv.dll in-memory patch + headless loopback RDP; IddCx.dll/RdpIdd.dll patches to grab uncompressed swap chain; password required (NLA); input isolation via DEVPKEY_Device_SessionId; audio via Remote Audio (stereo only).
- Apollo: SudoVDA virtual display per client (matching res/refresh, fixed identity), per-client permissions (crypto.h:45 PERM enum), clipboard sync; older base ~51k LOC; 17 commits last 12 months (last 2026-05-21).
- Vibepollo (Nonary, GPL-3.0): Apollo fork, very active (2026-10-25), ~227k LOC; own VDD (8 clients), WGC in service mode, free virtual gamepad driver, PyroWave codec (needs their Moonlight fork). Too heavy/divergent.

## 3. Sunshine architecture
- ~74k LOC src (~12k Windows-specific); C++23, CMake ≥3.24, MSYS2 UCRT64 (MinGW), not MSVC. Bundles moonlight-common-c, ViGEmClient, Simple-Web-Server, libdisplaydevice, libvirtualhid.
- Very active: 859 commits/12 months, date-stamped tags (latest v2026.1028.x).
- Reusable: nvhttp, rtsp, stream (protocol/FEC/crypto), video.cpp + nvenc/ (NVENC, AMF, QSV via FFmpeg/native), DXGI DDA (DuplicateOutput1) & WGC capture, WASAPI audio, crypto, confighttp REST.
- Input: newest routes KB/mouse via libvirtualhid (system-wide virtual HID; driver-backed devices require a paid licence on Windows); gamepads fall back to ViGEmBus, KB/mouse fall back to SendInput. For multi-session need per-session SendInput (Apollo still uses) + session-confined gamepads.
- Service: tools/sunshinesvc.cpp (396 lines) SunshineService duplicates SYSTEM token, TokenSessionId=WTSGetActiveConsoleSessionId(), launches Sunshine.exe in job, restarts on console change. platform/windows/misc.cpp:325-333 assumes console session for user token on app launch. Both must be retargeted to a specific session.
- Licence GPL-3.0 (Apollo, Vibepollo too). Worker exe must ship with corresponding source. Separate service/UI/installer talking via IPC/HTTP/CLI may keep own licence (mere aggregation, GPL FAQ). Linking Sunshine code makes it GPL. Check FFmpeg build config and libvirtualhid mixed licence.

## 4. FPS caps
- Protocol: fps plain int in mode= and x-nv-video[0].maxFPS (SdpGenerator.c:320) + clientRefreshRateX100.
- Sunshine reads as int (rtsp.cpp:1136-1145); no hard cap found; fps affects codec level selection and VBV sizing only.
- moonlight-qt custom fps 10–9999 (SettingsView.qml IntValidator). Android: 30/60/90/120 + native refresh, "unlock FPS".
- Duo advertises up to 500 Hz (supporter tier) on embedded Sunshine.
- Real limits: capture tied to display refresh (virtual display must expose 500 Hz), encoder throughput shared across N desktops, RDP sessions default ~30 fps unless frame path replaced (Duo), timer/pacing precision, client decode.
- Needed: validate capture loop/pacing at 2 ms, per-frame packet/FEC overhead, 500 Hz modes on virtual display.

## 5. Recommendation
- Fork upstream Sunshine as "SplitPlay worker", thin rebased patch set; port Apollo bits selectively.
- Central SYSTEM service (own code, any licence): creates/keeps one session per user; port base per desktop (47989+100·i); per-instance config dir; launches worker via CreateProcessAsUser with SYSTEM token TokenSessionId=desktop session in per-instance job; restart on crash; web UI localhost:38299 with auth.
- Pairing via each worker's /api/pin and /api/clients/* over loopback with per-instance generated creds; worker web UI bound to localhost.
- Worker patches: own session id (misc.cpp), unique mDNS name, SendInput-only KB/mouse + session-scoped gamepads, per-session audio endpoint, optional mic port, 500 Hz validation.
- Effort: fork streaming side ~2–4 person-months (excluding session creation, non-console capture, virtual display). Own implementation ~9–15 person-months to parity [estimate] + compatibility tail. Wolf (MIT) Linux/GStreamer-only.

## 6. Risks
1. Capture in non-console sessions (dominant): Sunshine assumes console; DDA in RDP sessions likely unsupported [unverified]; RDP ~30 fps default; Duo uses fragile in-memory patches (Windows updates, AV, EULA).
2. Audio: Remote Audio stereo only.
3. Input isolation: virtual HID system-wide; DEVPKEY_Device_SessionId + re-enumeration; XInput 4-pad shared limit; ViGEmBus retired.
4. mDNS collisions.
5. Encoder session limits/throughput for N streams at high fps [unverified current limit].
6. Upstream churn (859 commits/yr).
7. GPL compliance.
8. Firewall rules for N port ranges; disable UPnP per instance.
