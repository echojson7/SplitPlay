# Research 2: video encoding up to 500 fps
Fetch budget cut short; (derived)/(unverified) marked. Source code read locally: Sunshine d7e5fe0, moonlight-qt ac29686, moonlight-common-c 5d3c0ec, Apollo display_wgc.cpp, Duo wiki.

## Summary
1080p500 achievable on NVIDIA Turing/Ampere/Ada/Blackwell with NVENC P1 ultra-low-latency, HEVC (or AV1 Ada/Blackwell), wired Ethernet, PC Moonlight client with high-refresh display. Real ceiling: game GPU load on same GPU, GeForce 12-session limit, network packet rate, client decoder/display. 1440p500 borderline; 4K500 impractical. AMD/Intel: plan ~1080p240–360 until benchmarked.

## 1. Encoder throughput
- NVENC per engine, 1080p 4:2:0 8-bit P1 CBR low-latency (SDK 13.0 app note) fps H.264/HEVC/AV1: Pascal 667/539/–; Turing 855/932/–; Ampere 868/943/–; Ada 910/1055/1090; Blackwell 977/1134/1076.
- P3 LL: ~600–720 H.264, ~460–530 HEVC, AV1 774 Ada/798 Blackwell; P5+ <350. → 500 Hz mode = P1 (P2–P3 at most on Ada/Blackwell).
- One session ≤ one engine unless SFE. Engines: AD102/103/104-400 2; AD107 1; GB202 3; GB203 2; GB207 1; Turing/Ampere GeForce likely 1 (unverified).
- Pixel-scaled estimates (unverified): 1440p ~510–640 fps/engine P1 Ada/Blackwell; 720p >1500; 4K ~230–280.
- SFE: SDK 12.0 Ada, explicit control SDK 12.1 (NV_ENC_SPLIT_*_MODE); HEVC/AV1 only; ~1.8× 2-way, up to 2.95× HEVC / 2.31× AV1 3-way; BD-rate ~2%/~4%; auto mode only ≥4K, 2+ engines, P1–P4; forced via Sunshine nvenc_split_encode=enabled; arXiv: no added e2e latency at 4K. Not needed for 1080p500.
- GeForce concurrent sessions: SDK 13.0 note says 8; SDK 13.1 note says 12 per system (driver 591.44, Dec 2025, vMix). Quadro/RTX Pro uncapped. All encoders count (OBS, ShadowPlay).
- Per-frame encode latency ~1 ms 1080p (forum, non-staff). FFmpeg may add 1–2 frames buffering (async_depth/delay) unless tuned.
- AMD AMF: no published fps; Navi 21/31/32 2 VCN, Navi 33/44/48 1; AV1 on one VCN only; no SFE; Tom's ~370 fps 1080p RX 6950 XT (decode-bound).
- Intel QSV: Arc A380 ~480–520 fps 1080p (Tom's). 1080p500 marginal.

## 2. Bitrate & network
- moonlight-qt bitrate slider max 150 Mbps; "Unlock bitrate limit (Experimental)" → 500 Mbps (SettingsView.qml). Configured bitrate includes FEC → encoder gets ~80% (Limelight.h). moonlight-common-c caps GFE SDP fields at 100 Mbps but sends full bitrate to Sunshine separately. Sunshine max_bitrate default 0 (unlimited).
- 1080p500 needs: Moonlight default formula ~58 Mbps; realistic 60–120 Mbps HEVC/AV1, 100–150+ H.264 (estimate). 100 Mbps → ~25 KB/frame ≈ 18 packets of 1392 B.
- FEC 20% default, min 2 parity/frame → effective 22–40% at 500 fps. Packet rate ~11k pps @100 Mbps, ~16k @150, ~55k @500. Sunshine paces each frame at ~800 Mbps per session (stream.cpp). Internet streams 1024-byte packets.
- IDR 100–300 KB (est) takes 1–3 ms → stutter. Sunshine: infinite GOP, RFI (nvEncInvalidateRefFrames), intra-refresh on request (x-ss-video[0].intraRefresh).
- Wired Ethernet required; Wi-Fi jitter > 2 ms (unverified). N desktops × (60–150 Mbps)+FEC; host uplink 2.5 GbE; per-session 800 Mbps pacing assumption can oversubscribe 1 GbE.

## 3. Client & Sunshine limits
- moonlight-qt custom fps 10–9999 (IntValidator); display refresh rates auto-added; V-sync auto-off when fps > display Hz + 5 (session.cpp); v5.0 added custom fps/res.
- Moonlight Android 30/60/90/120 default (arrays.xml).
- NVDEC per engine 1080p: H.264 748–2172, HEVC 1316–1872, AV1 790–1119 (Turing→Blackwell). Phone/TV SoC unlikely (unverified). Decode avg < 2 ms needed.
- Codec levels: HEVC L5.2 max luma sample rate 1,069,547,520 ≈ 515 fps at 1080p (same constants in Sunshine video.cpp); 1440p500 needs L6.1. H.264 L5.2 (2,073,600 MB/s) ≈ 254 fps 1080p; 1080p500 needs L6.0+ (derived, verify). Android MediaCodec may reject >5.2 (unverified).
- Sunshine no hard fps cap; capture loop paces with high-res waitable timer + NtSetTimerResolution. Pitfalls: WGC path hard-codes MinUpdateInterval(4ms) → 250 Hz cap (display_wgc.cpp); Apollo uses 1/(2×fps). minimum_fps_target default fps/2 → idle 500 Hz desktop still encodes 250 fps.

## 4. Capture
- DDA/WGC deliver only when DWM composes (≤ monitor refresh); DDA only from within captured session. Win11 24H2 WGC odd with MinUpdateInterval <1 ms, drops frames under GPU load (robmikh/Win32CaptureSample#82).
- IddCx swap-chain capture best: Duo patches IddCx.dll/RdpIdd.dll, advertises 500 Hz; VDD (IddCx 1.10) custom modes 30–500 Hz. 500 Hz requires 500 Hz virtual mode per session; DWM cost grows.
- Zero-copy: IddCxAdapterSetRenderAdapter → shader NV12/P010 → NVENC-registered input; ~5.5 GB/s at 1080p500 (derived), ~0.05–0.2 ms GPU/frame (est).
- Alongside games: DWM + conversion shader compete with game; scheduling delay several ms. Sunshine sets D3DKMT_SCHEDULINGPRIORITYCLASS_REALTIME + SetGPUThreadPriority(7); docs warn realtime+HAGS can freeze near-full VRAM (nvenc_realtime_hags). GPU render capacity, not NVENC, is practical multi-desktop ceiling. NVENC engine balancing unverified.

## 5. Recommendation
- Sunshine direct NVENC SDK path (SDK 13, NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY, async). AMF/QSV via FFmpeg secondary.
- CBR, VBV one frame, infinite GOP, frameIntervalP=1, no lookahead, RFI, optional intra-refresh, slices/tiles per client.
- Codec: HEVC default; AV1 on Ada/Blackwell when client decodes; H.264 only ≤1080p240 (level).
- 500 Hz mode: 1080p (or 720p/900p) HEVC P1 60–150 Mbps; 500 Hz IddCx mode per session; capture from IddCx swap chain; minimum_fps_target ~10–30; Ethernet; PC client V-sync off, 360–500 Hz display.
- Admission control per-GPU budget in 1080p-equivalent encode fps ~900–1100/engine P1 (RTX 4090 ~2000 → ~3×1080p500 or 12×1080p120 at session cap).
- Ceilings one 1080p stream: Ada/Blackwell 1080p500 yes (~2× headroom), 1440p500 marginal, 4K ~240; Turing/Ampere 1080p500 yes; Pascal marginal; Intel Arc ~1080p500 marginal, 720p500 OK; AMD ~1080p240–360 (unverified).
- Latency 1080p500: capture ~1 + convert 0.2 + encode ~1 + send 0.3 + LAN 0.5 + decode 1–3 + display ~1 ≈ 5–8 ms + game; 1080p60 ≈ 20–35 ms (estimates).

## 6. Risks
1. Games competing for GPU; realtime priority + HAGS freeze. 2. GeForce 12-session cap driver policy. 3. Duo-style patching fragile. 4. Client decoders/levels at 500 fps; Android 120 list; 500 Hz displays rare. 5. Network pps, 1 GbE oversubscription, IDR bursts, Wi-Fi jitter. 6. Timer/scheduling jitter vs 2 ms; DWM cost at 500 Hz × N. 7. AMD/Intel unmeasured.

## Open questions
Forced SFE latency at 1080p/1440p? NVENC auto-balances engines? RdpIdd swap chain sustain 500 Hz? Real decode latency at 500 fps? Decoders accept HEVC/H.264 L6.x? AMD/Intel benchmarks.

Sources: NVENC app notes SDK 13.0 & 13.1 (docs.nvidia.com); blog.vmix.com/?p=4331; en.wikipedia.org/wiki/NVENC; NVIDIA SFE blog; arxiv.org/pdf/2511.18687; NVDEC app note 13.1; NVIDIA forum 143954; AMF wiki HW features; Tom's Hardware Arc A380 review p5; moonlight-qt v5.0.0 release, SettingsView.qml; moonlight-android arrays.xml; robmikh/Win32CaptureSample#82; VDD mintlify; Sunshine performance tuning docs.
