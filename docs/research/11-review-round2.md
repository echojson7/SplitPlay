# Research 11: independent review of round-2 reports 07–10
Code checked in local clones (Sunshine d7e5fe0, moonlight-common-c 5d3c0ec, moonlight-qt ac29686, nv-codec-headers); 3 fetches, 4 searches.

## Verdicts
| # | Claim | Verdict | Evidence / correction |
|---|---|---|---|
| a | Slot sampler adds ~½ stream interval; encode-on-arrival removes it | Confirmed with correction | display_base.cpp:225-330: sleep to group_start+k/fps, snapshot(0 ms); on a miss the group resets and a 200 ms blocking snapshot takes the next frame on arrival. Delay = age of newest frame at slot ≤ min(source, stream interval); ½ interval only when source ≥ stream rate with drifting phase. With DWM faster than stream (DWMFRAMEINTERVAL 2–4) loss is ½ DWM interval (1–2 ms). 07 #1 and #11 recover the same latency — not additive. minimum_fps_target default framerate/2 (video.cpp:2438) → idle 500 Hz desk re-encodes ~250 fps. |
| b | Block-by-block send accepted by unmodified Moonlight | Confirmed in code; wire test needed | RtpVideoQueue.c:585-736, VideoDepacketizer.c:766-910, stream.cpp:1588-1700: last-block index in every packet → block count fixed at first packet; frame type known pre-encode; lastPayloadLen only non-H.264/HEVC → AV1 excluded; host-latency field partial (harmless); non-last blocks packet-aligned (carry remainder, no mid-frame zero-pad); FEC per block (≤255 shards) → oversized last block can drop to 0% FEC; tiny frames: 1 block after completion; HW-decoder clients request 1 slice (moonlight-qt ffmpeg.cpp:100-107) → host overrides slicesPerFrame and pays bitrate. Gain only encode/wire overlap: ~0.5–1.5 ms @60 high bitrate, ~0.1–0.2 ms @500. 08's "needs fork" applies to client slice decoding only. |
| c | Rumble delayed up to ~100 ms | Confirmed | feedback_queue/hdr_queue flushed once per iteration (stream.cpp:1366-1381); iterate(150 ms) (:1395, :762-765); client ping 100 ms (ControlStream.c:302). Worst ~100 ms, avg ~50 ms when idle; HDR metadata same. Doorbell or 1 ms timeout. |
| d | DPB 5 → IDR; DPB 16 safe | Confirmed, stronger | DPB 5/8 (nvenc_base.cpp:372/408/449); RFI range ≥ DPB → IDR (:810-823). moonlight-common-c sends maxNumReferenceFrames=0 for RFI-capable decoders; comment: such decoders support codec max; 16 refs broke only non-RFI decoders, which get 1 (SdpGenerator.c:463-475). → use 16 whenever client sends 0. Cost ~16 recon surfaces (~50 MB 1080p, ~200 MB 4K est) — count in admission. Test Android/Apple. |
| e | KB/mouse via loopback RDP: relative mouse on 24H2, no LLMHF_INJECTED, real hDevice | Unverified | TS_FP_RELPOINTER_EVENT exists (MS-RDPBCGR, 2023-09), FreeRDP 3 sends it; no product-behavior note on which servers advertise it → hybrid fallback (relative via SendInput) mandatory. |
| f | Per-session audio enumerator via provider | API confirmed; client SKU unverified; risk understated | RDS calls QueryProperty(WTS_QUERY_AUDIOENUM_DLL); DLL exports entry point, implements IAudioOutputEndpointRT/IAudioInputEndpointRT, E_NOINTERFACE falls back; "must be signed so that secure processes can load it"; documented from Server 2008 R2 "as part of a protocol provider", no client OS mentioned. DLL "can be loaded into the audio system and other applications" → MMDevAPI loads it into every process in the session that enumerates audio, incl. games (crash kills game; anti-cheat sees foreign module). Attestation-signed, opt-in, lab-test on Win11 Pro + TermWrap. |
| g | RemoteFX/RDPEUSB on Win11 Pro over loopback | Unverified | Only Win7/Server docs; server needs "Do not allow supported PnP device redirection" = Disabled; FreeRDP urbdrc ignores client-side GPO; isoch/latency unknown. 1-week spike first. |
| h | D3D12 GLOBAL_REALTIME; GPU priority for dwm.exe | Semantics confirmed; scope overstated; dwm unverified | Needs privilege, fails rather than downgrades, executes before any other queue, needs preemption. Covers only worker's own convert/copy queues — not NVENC engine, not session DWM. GeForce support unknown. Sunshine REALTIME→HIGH on NVIDIA+HAGS confirmed (display_base.cpp:689-705), only GetCurrentProcess(). Sunshine already calls DwmEnableMMCSS(true) (misc.cpp:1202). |
| i | HKCU UserGpuPreferences in remote sessions | Unverified | Community-documented only; lab test incl. DXGI adapter order. |
| j | moonlight-qt V-sync default on; off saves 8–16 ms @60 | Default confirmed; size corrected | streamingpreferences.cpp:132 (V-sync on), :140 (pacing off). D3D11VA 5-buffer FLIP_DISCARD, Present(0,0), DWM discards excess (d3d11va.cpp:557-602, :780-827) → no queue. Cost = wait to next vblank ≈ ½ refresh avg (8.3 ms @60, 2 ms @240), max 1 refresh; removable only in independent flip/MPO; in DWM-composed window ≈ nothing. 07's 16–33 ms overstated; still largest item for 60 Hz clients; VRR below panel max gets most of it without tearing. |
Other confirmed: pacing 80% of 1 Gbps (~71 pkt/ms), small frames unpaced (stream.cpp:1673); ≤4 FEC blocks; nvprefs bound to sunshine.exe (driver_settings.cpp:14); task_pool.start(1) (main.cpp:387); MMCSS only audio (audio.cpp:701); lan_encryption_mode never (config.cpp:844); reportSliceOffsets sync-only, SFE vs subframe/video-memory output (nvEncodeAPI.h:2263-2275). pSurface A8R8G8B8 holds for SDR; check HDR path (IddCx 1.10).

## Conflicts resolved
1. Slice streaming: host early per-block send OK unmodified (H.264/HEVC, constraints above); client early decode needs fork. Stage 5, ~1 ms @60.
2. RDP-path input vs SendInput: SendInput in MVP; Stage 2 input-sink interface (SendInput + worker→seat shared-memory ring); Stage 4 RDP path behind flag, hybrid (keys/buttons/wheel/SAS via RDP, relative motion via SendInput if not advertised). Seat becomes latency-critical (MMCSS, input thread, watchdog).
3. ARGB-to-NVENC vs late cursor: own single GPU pass (BGRA→NV12/P010 + cursor composite + later HDR) on high-priority D3D12 queue; NVENC internal CUDA conversion runs at uncontrollable priority and blocks late cursor/HDR/4:4:4. Direct ARGB only as experiment for cursorless desks.
4. DWMFRAMEINTERVAL vs VSync caps: DWMFRAMEINTERVAL global → not a per-desk cap. Lab test: does DWM pace to IDD mode per session? Yes → IDD mode = stream fps per desk, DWMFRAMEINTERVAL global floor. No → DWMFRAMEINTERVAL for highest tier, cap games via in-game limiter/Reflex, DRS fallback (per exe, machine-wide). Encode-on-arrival + fast DWM recover ½ interval once.
5. GPU priority: HIGH default; REALTIME/GLOBAL_REALTIME opt-in with >15% VRAM headroom.
6. DPB 16 unconditionally for RFI-capable clients.

## Roadmap (gains est.)
| Stage | Item | Gain | pw | Risk |
|---|---|---|---|---|
| 2 | Per-frame trace IDs + QPC from IDD → worker → RTP; ETW in all components; PresentMon consumer; frame_processing_latency from IDD present time | everything measurable | 1–1.5 | Low |
| 2 | Event-driven capture interface (arrival callback + QPC); IDD exposes HW cursor shape/position separately | enables ring + late cursor | 0.5 | Low |
| 2 | Input-sink interface + worker→seat ring spec (documented binary, arm's-length) | enables RDP input | 0.5 | Low |
| 2 | Per-desk tuning profile (pacing, FEC, DPB, min fps, GPU/CPU prio, caps) applied live over pipe | auto-tuning | 0.5–1 | Low |
| 3 | Rumble/HDR doorbell | ≤100 ms → <1 ms | 0.1 | None |
| 3 | Client launch pack + independent-flip check | ≈8 ms avg @60 Hz | 0.5–1 | Low |
| 3 | Scheduling hardening (power-throttling opt-out, MMCSS encode/send/input, input thread, MOVE_NOCOALESCE) | p99 several ms → sub-ms | 1–1.5 | Low |
| 3 | DPB 16; min fps 10–20; DRS on worker exe; LAN encryption opportunistic | fewer IDRs, NVENC headroom, clocks | 0.5 | Low |
| 3 | Configurable pacing | 1–2 ms on 2.5 GbE @60 | 0.5 | Low |
| 4 | IDD ring: event-driven, 3 textures, shared fences, one-pass convert + late cursor, high-prio/GLOBAL_REALTIME queue | 1–8 ms (phase); 0.1–0.5 ms/frame; cursor 1–5 ms | 3–4 | Med |
| 4 | Frame-cap manager (IDD mode, Reflex/limiter guidance, DRS fallback) | biggest shared-GPU lever | 1–2 | Med |
| 4 | Host NIC budget + phase stagger; loss telemetry → adaptive FEC/bitrate | 0.5–3 ms; 10–15% bw | 3 | Med |
| 4 | Admission control + telemetry UI (PDH, NVML, VRAM, NVENC budget); TDR recovery | no overcommit stutter | 3–4 | Low |
| 4 | CPU sets/job per session; X3D CCD & hybrid policy | lower variance | 1–2 | Med |
| 4 | RDP-path KB/mouse (flag, hybrid) | real devices, UAC/SAS | 2–3 | Med |
| 4 | SFE option for one fast desk | 1440p500 | 0.2 | Low |
| 5 | Per-session audio enumerator (opt-in, attestation-signed) | 5.1/7.1, no rdpsnd, no leak | 4–6 | High |
| 5 | Early per-block send | ≈0.5–1.5 ms @60 | 2–4 | Med |
| 5 | Data shards before parity; haptics PCM → rumble envelope | 0.05–1 ms | 1.5 | Low |
| 5 | Per-title shims; multi-GPU per heavy desk; iGPU tier | compat, hard isolation | 4–6 + hw | Med/High |
| opt | Moonlight fork: unreliable mouse, sub-ms batching, slice decode, mic | 0.3–2 ms | 3–6 | fork upkeep |
| opt | RDPEUSB virtual USB (spike); usbip-win2 DualSense haptics | genuine pads, haptics | 6–10 | High |
| opt | Encoder in IDD host; Present-hook capture; RTX PRO/MIG tier | 0.2–8 ms | High | Very high |

## Architecture changes to decide now
1. Capture contract: IDD owns ring + fences, signals arrival with QPC; worker never polls; worker does convert + cursor in one pass; cursor and HDR metadata separate interface fields.
2. Input through seat: seat becomes real-time (priority, watchdog, restart semantics); GPL worker ↔ seat via documented shared-memory protocol (arm's-length).
3. Service = host resource coordinator: owns DWMFRAMEINTERVAL, DRS, NIC budget, NVENC/VRAM admission, CPU sets; worker pipe gets live-update channel.
4. Audio enumerator DLL = new component class (loads into every process in desk session + audio engine); attestation-signed driver-package file; opt-in per desk; PCM interface shared memory now.
5. Installer: reversible "host tuning manifest" (power plan/core parking, NIC EEE/pause, DRS, MMCSS registry, DWMFRAMEINTERVAL tier, optional TDR limits), restored on uninstall; also generates client launch pack.
6. Licences: Vibepollo/usbip GPL-3 only in worker or shipped separately; any NVENC code in IDD must be clean MIT; FreeRDP urbdrc Apache.
7. Measurement first: hardware-in-the-loop CI (Win11 + NVIDIA) and glass-to-glass rig (LDAT/OSLTT) are prerequisites for Stage 4.

## Missed items
Auto-tuning (per-client calibration → desk profile; closed loop on encode time picks preset: P4–P5 at 60 fps ≈2 ms for quality, P1 for 500 Hz); image quality at fixed latency (spatial/temporal AQ, VBV per tier, 10-bit even for SDR, matrix/range correctness); YUV 4:4:4 (Sunshine has it, moonlight-qt exposes it) "desktop/text" mode + decoder matrix; HDR details (P010, metadata passthrough, SDR-in-HDR white, tone-map in same pass); NVENC capacity per engine (~2 desks @500 Hz per engine; idle duplicate encodes); end-to-end VRR (encode-on-arrival + VRR client ≈ VFR stream); client-side upscaling (RTX VSR) for lower bitrate; audio latency & A/V sync budget (Opus frame, buffers, clock drift IDD/DWM vs audio); pre-warmed session pool; frame generation (DLSS FG/AFMF, Reflex) in borderless-only remote sessions; regression/benchmark suite (p50/p99 per release, Release Preview ring); Wi-Fi AP airtime with several desks.

## Lab tests (updated)
1 DWM pacing to IDD mode per session; compose rate at DWMFRAMEINTERVAL 2/3/4, N=1/2/4. 2 Capture phase: slot sampler vs encode-on-arrival at equal/faster/slower DWM. 3 IDD lifecycle. 4 RDP-path input (relative mouse 26100+, LLMHF_INJECTED, hDevice, UAC/SAS, latency). 5 Early per-block send on moonlight-qt/Android/iOS/embedded; slice bitrate cost. 6 DPB 16 decoders; RFI vs IDR under loss @500. 7 Rumble/HDR latency before/after doorbell. 8 Client V-sync/VRR/independent flip glass-to-glass 60/144/240. 9 GPU priority HIGH vs REALTIME ± HAGS; GLOBAL_REALTIME on GeForce; dwm.exe; VRAM freezes. 10 NVENC capacity, 12-cap, idle duplicate encodes, SFE latency. 11 Audio enumerator on Win11 Pro + TermWrap. 12 RDPEUSB spike. 13 Per-user GPU preference, DXGI order, cross-adapter cost. 14 EcoQoS/E-core, N MMCSS workers, CPU sets vs anti-cheat. 15 USO NICs, DSCP from SYSTEM non-console, 2.5G pause frames, multi-desk pacing on 1 GbE. 16 TDR blast radius. 17 Anti-cheat matrix per optional module. 18 Patch Tuesday / Release Preview.

Sources: MS-RDPBCGR TS_FP_RELPOINTER_EVENT & Appendix A; learn.microsoft.com implementing-an-audio-endpoint-enumerator; D3D12_COMMAND_QUEUE_PRIORITY; Citrix CTX693180.
