# Third-Party Notices

This project adapts only the described lifecycle and provider patterns from the pinned references below. It does not import upstream account-management code, migration or generic-widget implementations, Tauri code, media helpers, updater code, or a community SDK.

## TaskbarWidgets

- Repository: https://github.com/pfcdev/TaskbarWidgets
- Pinned commit: `517d655d54fae3974690b3f9461bbf6539c13a30`
- Adapted pattern: taskbar probe, attach/detach lifecycle, and atomic file-channel design only.
- MIT notice: Copyright (c) 2026 PFC

## QuotaTray

- Repository: https://github.com/ukr8b3g-cmyk/QuotaTray
- Pinned commit: `be39c92432324307fe412a1611cc9000dcddfbda`
- Adapted pattern: official Codex stdio-process and JSONL provider lifecycle only.
- MIT notice: Copyright (c) 2026 QuantaTray contributors

Both references are licensed under the MIT License. Their copyright and permission notices must be retained with any substantial adapted portions.

## OpenAI Codex provenance reference

- Repository: https://github.com/openai/codex
- Pinned commit: `3a76f3ac68c8949d1cac6ea769b6ec7b8953a415` (`rust-v0.142.0`)
