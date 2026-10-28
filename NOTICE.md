# Third-party components

SplitPlay's own code is MIT-licensed (see `LICENSE`). The worker in `worker/` is a modified LizardByte Sunshine and is GPL-3.0 (see `worker/COPYING`); its complete source, including the patch queue, is published with every release. The components talk to each other only through the process boundaries and documented interfaces in `docs/contracts/`.

| Component | Used in | Licence |
|---|---|---|
| LizardByte Sunshine (pinned, see `worker/upstream.lock`) and its bundled dependencies | worker | GPL-3.0 |
| moonlight-common-c | worker | GPL-3.0 |
| FreeRDP | seat (from Stage 3) | Apache-2.0 |
| TermWrap | installer (from Stage 3) | MIT |
| libvirtualgamepad | pad driver (from Stage 4) | MIT |
| HidHide | installer (from Stage 4) | MIT |
| nlohmann/json 3.11.3 | native (common library, seat, stub worker) | MIT |
| Vue, Vue Router, Vite | web UI | MIT |
| Inno Setup | installer (build tool, not redistributed as a library) | Inno Setup licence |
| .NET runtime and ASP.NET Core | service, CLI, tray | MIT |
| Microsoft WebView2 | tray | Microsoft software licence (redistributable runtime) |

Code under the LizardByte Source-Available License (libvirtualhid) is deliberately excluded and must not be added.
