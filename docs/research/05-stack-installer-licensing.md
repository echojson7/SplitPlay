# Research 5: stack, build/CI, installer, signing, licensing
Fetch budget ran out late; [unverified] marked.

## 0. How Duo works (wiki Known Issues, Troubleshooting, Setup Guide)
In-memory termsrv.dll patch ("TermWrap") for concurrent sessions; headless localhost RDP to create sessions; in-memory IddCx.dll + RdpIdd.dll patches to capture uncompressed swapchain from RDP session's indirect display; Microsoft remote audio (stereo); dropped ViGEmBus (lost DS4, later re-added); NLA + local accounts with passwords, added to Remote Desktop Users; AV flags it; min Win11 24H2 (25H2 recommended); WebUI port 38299. → Duo largely avoids shipping its own signed kernel drivers.

## 1. Stack
- Streaming core: C++ Sunshine fork (GPL-3.0), thin patch queue, one process per desktop.
- Service/manager/CLI: C# .NET 10 LTS (ASP.NET Core Kestrel for :38299, CsWin32, single-file or NativeAOT). Don't target .NET 8 (support ends Nov 10, 2026 [unverified exact date]). Rust (windows-rs+axum) is lean alternative. Go weaker Win32 ergonomics.
- Web UI: SPA (Vue 3 or Svelte) embedded; Sunshine UI is Vue+Vite.
- GUI: WebView2 shell + tray icon loading same SPA.
- Fork ↔ manager via named pipes / CLI / config; separate programs = clean licence boundary.

## 2. Build/CI
- Sunshine: MSYS2 (UCRT64, CLANGARM64), CMake+Ninja, pacman deps, prebuilt FFmpeg from LizardByte/build-deps (MinGW). No MSVC. Keep MSYS2 for fork; MSVC/dotnet for manager & drivers.
- Sunshine CI (ci-windows.yml): windows-2022 & windows-11-arm, msys2/setup-msys2, RelWithDebInfo Werror, ldd DLL check, GoogleTest, cpack -G WIX + ZIP, Azure Artifact (Trusted) Signing.
- Caching: setup-msys2 cache, ccache/sccache, NuGet, pnpm.
- Drivers: WDK NuGet packages (Microsoft.Windows.WDK.x64) build on hosted runners.
- No GPU / multi-session on hosted runners → self-hosted Win11 GPU runner for e2e.
- Signing: Azure Artifact Signing for user-mode (~$10/mo [unverified]); not usable for driver attestation (EV cert required).

## 3. Driver signing
- Kernel drivers: Microsoft signature via Hardware Dev Center. Attestation: EV cert ~$250–700/yr [unverified], hours processing; Win10/11 desktop only, not Server; MS pushing retail to WHCP. From April 2026 update Windows stops trusting old cross-signed drivers (evaluation mode first); HDC-signed unaffected [inference]. 2–6 weeks calendar for EV + Partner Center.
- UMDF/IddCx: Code Integrity does not require MS signature for user-mode drivers; package .cat must be Authenticode-signed with trusted cert. VirtualDrivers VDD ships SignPath Foundation-signed. Azure Artifact Signing for driver catalogs [unverified], reportedly unsupported.
- Test-signing dev only (watermark, anti-cheat rejects).
- Third-party: Virtual-Display-Driver MIT SignPath-signed, redistributable; SudoVDA MIT/CC0 based on MS IddCx sample (MS-PL), signing unclear; ViGEmBus BSD-3 retired Nov 2023, Server unsupported → dead dependency.

## 4. Installer
- WiX v6/v7 MSI + Burn bundle .exe. ServiceInstall/ServiceControl, Firewall extension, VC++ & WebView2 prereqs, drivers via deferred CA pnputil /add-driver /install with rollback, MajorUpgrade. Caveat: WiX v6+ Open Source Maintenance Fee (for-profit expected to sponsor; EULA acceptance) [unverified].
- Fallback Inno Setup (single exe, Pascal scripting, no MSI).
- NSIS legacy; MSIX unsuitable (no drivers, limited services).
- Uninstall: stop/remove service, driver packages, firewall rules, revert RDP settings (fDenyTSConnections, NLA, RDU membership), remove termsrv hook, offer to keep ProgramData config, never silently delete user accounts.

## 5. Multi-session licensing (critical)
- Windows 11 OEM use terms: §2(a) "one person at a time"; §2(c)(iv) not "work around any technical restrictions"; §2(c)(v) not "make available for simultaneous use by more than one user over a network". termsrv patching (RDPWrap, TermsrvPatcher, Duo TermWrap) = §2(c)(iv) workaround → EULA breach for users; shipping/marketing it = higher legal/reputational risk. Not legal advice; DMCA §1201 unassessed.
- Operational: updates change termsrv.dll; AV false positives; anti-cheat friction; MS may harden.
- Clean path: Windows Server 2022/2025 RD Session Host + RDS CALs (~$119/user CAL). Downsides: ViGEm unsupported, attestation-signed drivers not accepted (WHCP needed), some games/anti-cheat/Store/Game Bar unsupported.
- Windows Enterprise multi-session: AVD-only; not on-prem.
- Recommendation: pluggable "session provider"; Server RDSH = supported compliant mode; client-Windows termsrv patch = explicit opt-in, disclosed, legal review, ideally not in default install.

## 6. Project licence
GPL-3.0-or-later for whole user-mode project (if Sunshine forked). Ship source for every GPL binary.
- GPL-3: Sunshine, moonlight-common-c. MIT: ENet, nv-codec-headers, AMF headers, oneVPL, Vue/Vite, VDD, SudoVDA. Opus BSD-3, Boost BSL-1.0, OpenSSL 3 Apache-2.0, Qt6 LGPL-3. FFmpeg build includes x264/x265 (GPL-2+) → GPL, compatible.
- IddCx sample (Windows-driver-samples) MS-PL — GPL-incompatible → drivers as separate works (MS-PL/MIT).
- WiX MS-RL build tool only. Avoid "Moonlight"/"Sunshine" in branding.

## 7. Effort (infra only, one engineer)
Monorepo+CI 2–3 wk; patch-queue workflow 1–2 wk then ~2 d/month; signing 1–2 wk work, 2–6 wk calendar; driver pipeline 2–3 wk (if own kernel drivers); WiX MSI+Burn 3–5 wk; self-hosted GPU runner + VM matrix 2–3 wk; legal review external. Total ~12–20 person-weeks.

## Open questions
1. Duo-like RdpIdd in-memory capture vs own IddCx per session? Can IddCx monitor attach inside RDP session?
2. Server RDSH first-class or client Windows main market?
3. Gamepads: own VHF/UMDF HID vs XInput isolation?
4. Azure Artifact Signing for UMDF catalogs?
5. WiX OSMF cost?
