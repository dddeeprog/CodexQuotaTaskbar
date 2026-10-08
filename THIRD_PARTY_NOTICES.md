# Third-Party Notices

## Codex Resets public data

- Data source and attribution: [Codex Resets](https://codex-resets.com/zh-CN)
- API documentation: https://codex-resets.com/api/docs
- Use: latest public reset announcements and the site's Chinese display text, matched to the canonical API by event identity; no third-party source code is included. The app retains linked Codex Resets credit but does not display X links.
- The public API is free to use without a key and requests a linked credit wherever its data is displayed. The island and details both include this credit. Announcement text remains attributed to its original source; API availability and accuracy are not guaranteed by this application.

## Microsoft.Web.WebView2

- Package: https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4258.31
- Version: `1.0.4258.31`; used only in the visible subscription login window.
- The Microsoft Edge WebView2 Runtime is distributed separately under Microsoft's terms.

```text
Copyright (C) Microsoft Corporation. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

   * Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.
   * Redistributions in binary form must reproduce the above
copyright notice, this list of conditions and the following disclaimer
in the documentation and/or other materials provided with the
distribution.
   * The name of Microsoft Corporation, or the names of its contributors
may not be used to endorse or promote products derived from this
software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

This project adapts only the described lifecycle and provider patterns from the pinned references below. It does not import upstream account-management code, migration or generic-widget implementations, Tauri code, media helpers, updater code, or a community SDK.

## TaskbarWidgets

- Repository: https://github.com/pfcdev/TaskbarWidgets
- Pinned commit: `517d655d54fae3974690b3f9461bbf6539c13a30`
- Adapted pattern: taskbar probe, attach/detach lifecycle, and atomic file-channel design only.
- MIT notice: Copyright (c) 2026 PFC

The TaskbarWidgets lifecycle patterns adapted by the native compatibility
probe remain covered by this license:

```text
MIT License

Copyright (c) 2026 PFC

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## QuotaTray

- Repository: https://github.com/ukr8b3g-cmyk/QuotaTray
- Pinned commit: `be39c92432324307fe412a1611cc9000dcddfbda`
- Adapted pattern: official Codex stdio-process and JSONL provider lifecycle only.
- MIT notice: Copyright (c) 2026 QuantaTray contributors

Both references are licensed under the MIT License. Their copyright and permission notices must be retained with any substantial adapted portions.

## OpenAI Codex provenance reference

- Repository: https://github.com/openai/codex
- Pinned commit: `3a76f3ac68c8949d1cac6ea769b6ec7b8953a415` (`rust-v0.142.0`)

## BlurredBackground.WPF

- Repository: https://github.com/V4SS3UR/BlurredBackground.WPF
- Package version: `1.1.0`
- Use: WPF background-blur behavior for the island, session cards, details, and tray menu.

```text
BSD 3-Clause License

Copyright (c) 2024, Anthony Vasseur

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

## Noto Sans SC

- Font files: Noto Sans SC Regular and Medium
- Upstream: https://github.com/notofonts/noto-cjk
- Use: consistent Simplified Chinese typography across Windows installations.
- Copyright: © 2014–2020 Adobe (http://www.adobe.com/).
- Trademark: Noto is a trademark of Google Inc. Font metadata identifies Adobe as the manufacturer.
- License: SIL Open Font License, Version 1.1. The full license is embedded in the application as `Assets/Licenses/Noto-Sans-SC-OFL.txt` and retained in this repository at `src/CodexQuotaTaskbar.Host/Assets/Fonts/OFL.txt`.
