# Third-Party Notices

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
