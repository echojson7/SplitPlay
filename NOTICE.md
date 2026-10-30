# Third-party components

SplitPlay's own code is MIT-licensed (see `LICENSE`). The worker in `worker/` is a modified LizardByte Sunshine and is GPL-3.0 (see `worker/COPYING`); its complete source, including the patch queue, is published with every release. The components talk to each other only through the process boundaries and documented interfaces in `docs/contracts/`.

| Component | Used in | Licence |
|---|---|---|
| LizardByte Sunshine (pinned, see `worker/upstream.lock`) and its bundled dependencies | worker | GPL-3.0 |
| moonlight-common-c | worker | GPL-3.0 |
| FreeRDP | seat (from Stage 3) | Apache-2.0 |
| TermWrap | installer (from Stage 3) | MIT |
| ViGEmBus 1.22.0 (official signed setup, unmodified) | installer (gamepads, installed only if missing) | BSD-3-Clause |
| libvirtualgamepad | frozen pad driver sources in `drivers/pad` (not shipped) | MIT |
| nlohmann/json 3.11.3 | native (common library, seat, stub worker) | MIT |
| Vue, Vue Router, Vite | web UI | MIT |
| Inno Setup | installer (build tool, not redistributed as a library) | Inno Setup licence |
| .NET runtime and ASP.NET Core | service, CLI, tray | MIT |
| Microsoft WebView2 | tray | Microsoft software licence (redistributable runtime) |

Code under the LizardByte Source-Available License (libvirtualhid) is deliberately excluded and must not be added.

## ViGEmBus

The installer bundles the ViGEmBus setup (https://github.com/nefarius/ViGEmBus), distributed under this licence:

```
BSD 3-Clause License

Copyright (c) 2016-2020, Nefarius Software Solutions e.U.
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```
