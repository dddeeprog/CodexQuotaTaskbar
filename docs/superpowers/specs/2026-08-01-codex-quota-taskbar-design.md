# Codex Quota Taskbar — Design Specification

**Date:** 2026-08-01

**Status:** Independently reviewed and approved for implementation planning

**Target:** Windows 11 x64

**Working title:** Codex Quota Taskbar

## 1. Summary

Codex Quota Taskbar is an independent, per-user Windows application that inserts an iOS-inspired “Liquid Capsule” quota display into the Windows 11 taskbar. It shows the remaining Codex short-window and weekly quota, opens a detailed glass popover on click, and degrades safely to a normal tray icon when the current Windows taskbar implementation is unsupported.

The application will use the official Codex app-server protocol for account and rate-limit data. It will not scrape ChatGPT pages, call private web endpoints, read browser cookies, or copy `~/.codex/auth.json`.

## 2. Confirmed Product Decisions

The user approved the following decisions during visual and requirements review:

1. Deliver an independent installable application; do not require Windhawk.
2. Use the “Liquid Capsule” design:
   - approximately 190 px wide and 36 px tall;
   - translucent dark glass shell;
   - Codex orb at the left;
   - two thin, rounded progress bars;
   - short-window and weekly values visible at all times.
3. Clicking the capsule opens an iOS-style detail card above the taskbar.
4. Percentages mean **remaining quota**, not consumed quota.
5. Normal quota uses cool violet/blue and mint colors:
   - remaining quota >= 30%: cool colors;
   - 10–29%: amber;
   - below 10%: red, with one optional Windows notification per quota window.
6. The first version uses the currently logged-in Codex account and supports multiple Windows taskbars/monitors.

## 3. Open-Source Evaluation and Selected Base

### 3.1 Taskbar AI Quota Bars

[Taskbar AI Quota Bars](https://windhawk.net/mods/taskbar-ai-quota) already implements much of the required quota behavior and true taskbar placement. It is not selected as the product base because it requires Windhawk and runs its provider and UI logic inside Explorer. It remains useful as a behavior reference.

### 3.2 TaskbarWidgets

[TaskbarWidgets](https://github.com/pfcdev/TaskbarWidgets) is selected as the primary open-source base. It is MIT licensed, supports true Windows 11 taskbar widgets, and separates provider/control work from the small native module loaded into Explorer. Its documented architecture and fail-closed compatibility approach match this product’s safety requirements:

- [Architecture](https://github.com/pfcdev/TaskbarWidgets/blob/main/docs/architecture.md)
- [Windows private API risks](https://github.com/pfcdev/TaskbarWidgets/blob/main/docs/windows-private-api-risks.md)

The fork will preserve required MIT notices. Upstream code will be imported in a traceable commit so future upstream changes can be compared cleanly.

The import is deliberately narrower than the upstream product. Production code must delete or exclude all upstream features that manage Codex accounts, including:

- account-manager and account-switching code;
- account and IDE-profile migration code;
- `Data/Accounts` storage and related settings surfaces;
- any host or bridge code that resolves, parses, opens, copies, or replaces `auth.json`.

A build-time forbidden-dependency check and a process-level file-access test enforce this boundary. The rule applies to this product’s host and native bridge; the official `codex app-server` child process remains responsible for its own authentication and may access Codex-owned credentials internally.

### 3.3 Provider change from upstream

The existing TaskbarWidgets Codex provider uses an experimental WebSocket app-server listener. This product will replace that transport with the official default `stdio` transport. The official Codex app-server documentation describes `stdio` as newline-delimited JSON and marks WebSocket transport as experimental and unsupported:

- [Codex app-server](https://github.com/openai/codex/blob/main/codex-rs/app-server/README.md)

This change removes a local TCP listener, avoids choosing or exposing a port, and follows the stable local transport path.

## 4. Goals and Non-Goals

### 4.1 Goals

- Insert a compact, native-feeling quota capsule beside the Windows notification area.
- Read current Codex quota through the installed Codex CLI’s official app-server.
- Display remaining percentage and reset time accurately.
- Keep authentication, network, parsing, settings, and the detail popover outside Explorer.
- Recover from normal Codex and Explorer restarts without user intervention; require explicit recovery after a safety breaker trips.
- Fail safely after incompatible Windows updates.
- Ship as a per-user installer and a portable diagnostic build.
- Remain usable without telemetry or a cloud service operated by this project.

### 4.2 Non-goals for v1

- Windows 10 support.
- ARM64 support.
- Multiple Codex account storage or account switching.
- Reading or copying Codex authentication files.
- Reset-credit redemption, billing, token-spend analytics, or workspace administration.
- Providers other than Codex.
- Automatic self-update.
- A public plugin/widget SDK.
- Replacing or modifying Windows taskbar binaries on disk.

## 5. System Architecture

The application has two trust zones and four principal components.

```text
Installed Codex CLI
    │  stdio JSONL (official app-server protocol)
    ▼
Host application (outside Explorer)
    ├── CodexRateLimitProvider
    ├── Snapshot normalizer + settings + notifications
    └── Detail popover / tray fallback
             │
             │ versioned, bounded atomic state/command files
             ▼
Native taskbar bridge (inside Explorer)
    └── Liquid Capsule renderer + click forwarding only
```

### 5.1 Host application

The host is the long-lived per-user process. It owns:

- Codex process discovery and app-server lifecycle;
- protocol initialization and JSONL parsing;
- quota normalization;
- refresh scheduling and reconnection;
- settings and local diagnostics;
- low-quota notifications;
- taskbar bridge lifecycle;
- the detail popover and tray fallback.

The host must continue running when Explorer restarts and reconnect the taskbar bridge when a compatible taskbar becomes available.

### 5.2 CodexRateLimitProvider

The provider starts the installed `codex app-server` with redirected standard input, output, and error streams. It performs the documented initialization handshake, then calls:

- `account/read` to confirm authentication state;
- `account/rateLimits/read` for the initial snapshot and manual/periodic refresh;
- listens for `account/rateLimits/updated` as an invalidation signal that triggers a debounced full read;
- listens for `account/updated`, immediately clears data belonging to the previous account, and re-reads account and quota state.

The provider never launches a login flow itself. When Codex reports a logged-out state, the UI instructs the user to sign in through Codex. An authenticated non-ChatGPT mode such as API-key or Bedrock auth is reported as “当前登录方式不提供 ChatGPT 额度,” not as logged out.

Every account-state change increments an in-memory account generation. Each account and quota request captures the generation at dispatch; its response may update identity, quota, notification state, or files only when that generation still matches. Late responses from the prior account are discarded.

The first release has a bounded verified Codex CLI range: 0.142.0 through the highest version tested at packaging time. Startup performs a capability and schema probe for the initialization handshake, `account/read`, and `account/rateLimits/read`; version alone is not treated as proof of compatibility. Versions newer than the recorded maximum may run only when the probes pass. A CLI below 0.142.0 or missing the required methods produces “请更新 Codex”; a newer but incompatible protocol produces “请更新本应用，当前 Codex 组合暂不支持.” Contract tests cover 0.142.0, the packaging-time maximum, unknown additive fields, and an incompatible newer schema.

### 5.3 Snapshot normalizer

The normalizer converts app-server results into an immutable display snapshot containing only:

- schema version and sequence number;
- provider state;
- two display windows;
- remaining percentage;
- window duration;
- reset timestamp;
- last successful refresh timestamp;
- stale/error category;
- threshold color state.

No access token, raw protocol message, cookie, authorization header, full email address, or filesystem authentication path may enter the snapshot.

Account identity stays in host memory for the host-owned detail card and never crosses into the taskbar state file. When available, the card displays plan type plus a masked address such as `t***@e***.com`. The full address is never logged or persisted, and an `account/updated` event clears it before any new account read begins.

### 5.4 Native taskbar bridge

The native bridge is the only code loaded into Explorer. It may:

- locate and validate the supported Windows 11 taskbar visual tree;
- insert/remove the capsule visual;
- render an already-normalized snapshot;
- forward click and context-menu events to the host;
- report compatibility and lifecycle status.

It may not:

- start Codex;
- access the network;
- read files containing credentials;
- parse arbitrary JSON from Codex;
- own settings or notification policy;
- host the expanded detail card.

### 5.5 Detail popover

The expanded detail card is an app-owned, per-monitor, DPI-aware tool window anchored above the clicked capsule. It is not hosted inside Explorer. It:

- opens on primary click;
- shows both quota windows, remaining percentage, and localized reset time;
- shows the current masked account and plan when the app-server provides them;
- shows freshness state such as “刚刚更新” or “更新于 10:28”;
- provides “立即刷新” and “打开 Codex” actions;
- closes on outside click, `Esc`, a second capsule click, taskbar recreation, or display topology change.

The explicit capsule click may activate the popover, which makes `Esc` and keyboard navigation available. A popover reopened only because of a background refresh or display event must not take focus from another application.

## 6. App-Server Data Contract

The official response supplies a backward-compatible `rateLimits` bucket and may also supply `rateLimitsByLimitId`. Each window reports `usedPercent`, `windowDurationMins`, and `resetsAt`.

### 6.1 Bucket selection

1. Prefer `rateLimitsByLimitId["codex"]` when present.
2. Otherwise use the backward-compatible `rateLimits` value.
3. Flatten non-null `primary` and `secondary` windows.
4. Sort display windows by `windowDurationMins`.
5. Use the shortest window for the first row and the longest distinct window for the second row.

The UI must derive labels from duration rather than assuming every account receives the same windows:

- 300 minutes renders as `5 小时`;
- 10,080 minutes renders as `每周`;
- other durations render honestly as hours or days.

If only one window is returned, the second row renders an em dash and an unavailable track instead of inventing data.

### 6.2 Remaining quota calculation

```text
remainingPercent = 100 - usedPercent
```

`usedPercent` must be finite and within the inclusive range 0–100 before the calculation. Missing, non-finite, or out-of-range input is rejected rather than clamped. A rejected response does not overwrite the last valid snapshot.

### 6.3 Reset time

`resetsAt` is treated as Unix time in seconds and converted to the current Windows time zone for display. The capsule itself shows only percentages; the popover shows a relative reset string and may additionally show the absolute local time in a tooltip.

### 6.4 Refresh policy

- Initial read immediately after successful app-server initialization.
- `account/rateLimits/updated` triggers a debounced `account/rateLimits/read`; sparse notification payloads are never treated as complete state.
- `account/updated` immediately clears the previous account snapshot and notification-deduplication state, then triggers full account and quota reads.
- Five-minute fallback read to heal missed notifications.
- Manual refresh debounced to one request per five seconds.
- A snapshot becomes stale after ten minutes without a successful read.

## 7. Visual and Interaction Specification

### 7.1 Collapsed capsule

- Nominal size: 190 × 36 device-independent pixels.
- Radius: 18 px.
- Shell: dark translucent fill, thin light border, subtle inner highlight and shadow.
- Left: 25 px Codex orb.
- Right: two rows consisting of label, 4 px rounded track, and right-aligned percentage.
- Text must remain legible at Windows scaling values from 100% through 200%.
- The capsule participates in taskbar layout and must not float above unrelated applications.

### 7.2 Color rules

Each quota window is colored independently:

- >= 30% remaining: first row violet/blue, second row mint;
- 10–29% remaining: amber;
- < 10% remaining: red;
- unavailable, logged out, or never loaded: neutral gray;
- stale: retain the last value at reduced saturation and show a small clock marker.

Color is not the sole status signal: percentage text and stale/unavailable glyphs remain present for accessibility.

### 7.3 Pointer actions

- Primary click: toggle detail popover.
- Right click: open a compact menu with Refresh, Open Codex, Settings, Start with Windows, and Exit.
- Hover: show a tooltip with remaining quota, reset time, and freshness without opening the full card.
- No action is bound to double-click.

### 7.4 Notifications

- Notifications are enabled by default and configurable.
- Notify once when either window crosses from >= 10% to < 10% remaining.
- Deduplicate using the window identity plus `resetsAt`.
- Persist the deduplication key across host restarts.
- Re-arm only when the reset timestamp changes or remaining quota returns to >= 10%.
- Never notify repeatedly because the provider reconnects.

## 8. IPC and Process Safety

The product retains TaskbarWidgets’ atomic file channel instead of adding a second general-purpose IPC mechanism.

- State is written only to a fixed file under `%LOCALAPPDATA%\CodexQuotaTaskbar\Data\State`.
- The host writes a temporary file in the same directory, flushes it, and atomically replaces the state file.
- The native bridge accepts at most 16 KiB, rejects reparse points, and validates schema version, strings, percentages, timestamps, enums, and dimensions before rendering.
- Commands are written only under a fixed `%LOCALAPPDATA%\CodexQuotaTaskbar\Data\Commands` directory as unique, bounded files of at most 4 KiB.
- The data root disables inherited write access and grants access only to the current user and `SYSTEM`. The design does not claim to defend against a malicious process already running as the same user.
- Each activation receives a random, non-persistent session nonce delivered to the bridge during activation. Command files must contain that nonce, a fresh timestamp, a known action, and the originating capsule instance.
- A click command may carry a validated screen-pixel anchor rectangle, monitor identifier, effective DPI, and taskbar edge so the host can position the popover reliably. The anchor must intersect the target monitor bounds and remain inside the already-validated taskbar window bounds. After choosing an edge-relative placement, the host clamps the detail card itself to the monitor work area.
- The bridge writes each command to a temporary file in the command directory, flushes it, and atomically renames it to a unique final command name. The host atomically renames a final command into a processing name before reading it, so only one consumer can claim it.
- On startup and periodically, the host removes expired temporary, final, and processing command files older than five minutes without executing them.
- The renderer keeps its previous valid snapshot when a state file is rejected.
- Unknown schema versions and commands are ignored safely.
- Fixed directories and filenames are opened without following reparse points; no arbitrary command, executable, or caller-supplied path crosses the boundary.

A named shutdown event and a bounded host-lease timestamp are permitted only for lifecycle control. They do not carry provider data or arbitrary messages.

## 9. Compatibility and Fail-Closed Behavior

True insertion into the Windows 11 taskbar depends on undocumented implementation details. Compatibility is therefore explicit rather than optimistic.

### 9.1 Version gate

- Maintain an allowlist of verified Windows build ranges and taskbar signatures inherited from or validated against upstream.
- Treat the user’s current Windows build `10.0.26200` and its exact Explorer/taskbar signatures as the first release gate.
- Make a minimal attach, insert, layout, remove, Explorer-restart, and multi-monitor probe the first implementation milestone. Record the tested signatures with the build artifact.
- If that probe cannot embed and detach safely on build 26200, stop before full provider/UI implementation and return to design review with the user; tray fallback alone does not satisfy the core request on this machine.
- Validate required symbols and expected visual-tree structure before inserting any visual.
- If validation fails, do not partially patch the taskbar.

### 9.2 Fallback mode

On unsupported or failed injection:

- keep the host and Codex provider available;
- display a normal notification-area icon;
- expose the same detail card from the icon;
- show a clear “当前 Windows 版本暂不支持任务栏嵌入” status;
- offer diagnostics, not repeated automatic retries.

### 9.3 Circuit breaker

Safety state is persisted outside Explorer. Before activation, the host records a pending activation containing the app version, Windows build/signature, Explorer PID, and timestamp. It marks the record clean only after a successful detach or a stable activation interval.

- A previous pending/unclean activation starts the next host or Windows session in tray-only safe mode.
- The bridge monitors a host lease and removes its visual, stops command processing, and detaches when the lease expires for 30 seconds or the named shutdown event is signaled.
- Activation must complete within ten seconds; timeout enters safe mode.
- Three consecutive Explorer responsiveness probe failures or two unexpected Explorer exits within ten minutes of activation trip the breaker.
- After the breaker trips, automatic injection remains disabled across host and Windows restarts until the user explicitly retries or a new app version/signature rule passes the minimal compatibility probe.
- Install a Start-menu “Codex Quota Taskbar（安全模式）” entry and support `--safe-mode` so the user can always launch tray-only and change settings.

### 9.4 Explorer and display changes

- Detect Explorer restart and reattach only after the new taskbar passes validation.
- Recreate one capsule instance per supported taskbar/monitor.
- Anchor the detail popover to the instance that was clicked.
- Recalculate placement after DPI, resolution, taskbar alignment, or work-area changes.

## 10. Error-State UX

| State | Capsule | Detail card / action |
|---|---|---|
| Codex CLI not found | Gray `—` | Explain requirement and offer to open installation guidance |
| Codex CLI below 0.142.0/missing required methods | Gray update glyph | Explain the minimum capability and offer official Codex update guidance |
| Newer Codex protocol fails schema probe | Gray compatibility glyph | Explain that this combination is unsupported and ask the user to update this application |
| Logged out | Gray lock state | Ask user to sign in through Codex |
| Non-ChatGPT auth mode | Gray `—` | Explain that this auth mode does not expose ChatGPT quota |
| Initial connection | Muted animated track | “正在连接 Codex…” |
| Temporary app-server failure | Last valid values, stale marker | Show last update and automatic retry state |
| Invalid response | Preserve last valid values | Record sanitized diagnostic; retry later |
| One quota window missing | Available row plus unavailable row | Explain that the account returned only one window |
| Unsupported Windows build | Tray fallback only | Explain compatibility gate and expose diagnostics |
| Host exiting | Remove capsule cleanly | No stale taskbar visual remains |

## 11. Reconnection and Backoff

When app-server exits or initialization fails:

1. preserve the last valid snapshot;
2. retry after 1, 2, 5, 10, 30, then 60 seconds;
3. add small random jitter;
4. reset the backoff after five minutes of stable operation;
5. stop retrying while the host is shutting down;
6. treat an explicit logged-out response as a user-action state, not a crash loop.

Only one provider process may be active per host instance. A per-user single-instance lock prevents duplicate capsules and duplicate notifications.

## 12. Settings and Local Data

First-version settings:

- Start with Windows: on by default after installation, user-toggleable.
- Low-quota notification: on by default.
- Fallback refresh interval: 5 minutes default, configurable from 1–30 minutes.
- Display on: all taskbars by default; primary-only option.
- Launch Codex action: prefer the installed Codex desktop app when discoverable, otherwise open a configured command/surface.

Settings and notification-deduplication state are stored per user. Diagnostics use a bounded rolling log with no project-operated telemetry. Logs must not contain raw app-server JSON or stderr, tokens, authorization values, full account identifiers, or environment-variable dumps.

The product is not an offline quota source: the official `codex app-server` child uses the user’s existing Codex login to request quota from OpenAI. Its initialization `clientInfo` may appear in OpenAI compliance logs. Product documentation must disclose this flow and state that this host persists only settings, safety state, notification keys, and sanitized derived quota values.

## 13. Installation and Removal

- Ship an x64 per-user installer and a portable diagnostic archive.
- Do not require a Windows service.
- Runtime taskbar integration must not require administrator elevation.
- Register startup only after the user completes installation; expose the toggle in settings and the context menu.
- Uninstall removes application files, startup registration, settings on explicit request, and injected bridge state.
- The first locally built package will be unsigned unless a code-signing certificate is supplied; documentation must warn about the resulting SmartScreen prompt without suggesting unsafe global policy changes.

## 14. Testing Strategy

### 14.1 Provider unit tests

- Parse successful `account/read` and `account/rateLimits/read` responses.
- Prefer the `codex` multi-bucket entry and fall back correctly.
- Map primary/secondary windows by duration.
- Calculate remaining percentage at the inclusive 0% and 100% boundaries.
- Reject used percentages outside 0–100 rather than clamping them.
- Convert reset timestamps across time zones and daylight-saving changes.
- Reject malformed JSON, non-finite numbers, oversized strings, and unknown shapes.
- Preserve the previous snapshot after an invalid update.
- Deduplicate low-quota notifications by window/reset identity.
- Persist and restore notification deduplication across host restarts.
- Treat `account/rateLimits/updated` as invalidation and perform a full read.
- Clear previous identity, quota, and deduplication state on `account/updated`.
- Discard late account/quota responses whose captured account generation is no longer current.
- Distinguish logged out, ChatGPT auth, API-key auth, and Bedrock auth.
- Mask account labels and prove the full identifier never enters state files or logs.

### 14.2 Provider integration tests

Use a fake JSONL app-server process to cover:

- initialization handshake;
- initial read and update notifications;
- sparse update notification followed by a complete rate-limit read;
- request/response correlation;
- stderr noise isolation;
- process exit and backoff;
- hung process cancellation;
- manual refresh debounce;
- orderly shutdown with no orphan child process.
- capability failure on an old/missing endpoint and tolerance of unknown additive fields.
- incompatible newer-schema guidance that asks for an application update rather than a Codex downgrade/update.

No integration test requires a real user token.

A separate host-process file-access test uses a fake app-server and an audited sentinel `auth.json` path to prove that the host and bridge never open it. A real-Codex diagnostic trace must filter the official app-server child PID separately, because that child legitimately owns its authentication behavior.

### 14.3 IPC/native tests

- Round-trip each supported state and command schema version through atomic files.
- Prove command temp-write, flush, atomic publish, single-consumer claim, and stale-file cleanup semantics.
- Reject reparse points, wrong owners/ACLs, truncated files, oversized files, stale/nonced commands, unknown versions, and invalid enums.
- Fuzz the native state and command decoders.
- Verify no auth-bearing fields exist in either file schema.
- Validate anchor rectangles and DPI against current monitor geometry.
- Exercise repeated connect/disconnect and Explorer-restart simulation.

### 14.4 Windows UI tests

Manual and automated smoke coverage on supported Windows 11 x64 builds:

- left/center taskbar alignment;
- 100%, 125%, 150%, and 200% scaling;
- one and multiple monitors;
- bottom taskbar geometry validated against monitor/taskbar bounds while the detail card is clamped to the work area;
- auto-hide taskbar geometry and mixed-DPI monitor placement;
- top-level taskbar recreation after Explorer restart;
- light/dark Windows theme and transparency disabled;
- auto-hide taskbar;
- capsule click, right click, tooltip, outside click, and `Esc`;
- no overlap with notification icons or clock;
- fallback tray behavior on deliberately failed compatibility validation.
- high-contrast mode and transparency disabled;
- reduced-motion settings with no nonessential animation;
- UI Automation names, percentage values, and Invoke patterns;
- keyboard navigation, focus order, and `Esc` after the clicked popover is activated.

### 14.5 Reliability checks

- Publish 1,000 snapshots over at least 30 minutes with no crash or hang; after warm-up, Explorer GDI and USER handles must each finish within +10 of baseline and private bytes within +20 MiB.
- Run provider reconnect and Explorer-restart soak tests.
- Verify the host has no busy loop while idle.
- Verify uninstall and host exit remove all live capsule instances.
- Simulate host death and lease expiry, then verify the bridge removes its UI within 30 seconds.
- Simulate an unclean activation across host and Windows-session restart and verify startup remains tray-only until explicit retry.

## 15. Acceptance Criteria

The first release candidate is acceptable when all of the following are true:

1. On the user’s Windows 11 x64 build `10.0.26200` with the recorded Explorer/taskbar signatures, the capsule is inserted into each selected taskbar, detaches cleanly, and matches the approved Liquid Capsule layout.
2. Values match the installed Codex CLI’s app-server response after converting from used to remaining percentage.
3. Clicking the capsule opens the approved detail card at the clicked monitor with masked account/plan, localized reset times, Refresh, and Open Codex actions.
4. Static checks and process-level file tracing prove that the host and bridge do not contain upstream account-management/migration code and do not open `auth.json`, browser cookies, or private ChatGPT endpoints.
5. Network/auth/provider code does not execute inside Explorer.
6. CLI capability probing works across the bounded verified range from Codex 0.142.0 through the packaging-time maximum; compatible additive fields are tolerated, while old, newer-incompatible, and non-ChatGPT modes produce distinct guidance.
7. Sparse notifications, deliberately reordered old-account responses, account changes, provider restarts, Explorer restarts, and a missing second window produce the specified recoverable states without leaking the prior account.
8. Unsupported validation, activation timeout, Explorer unresponsiveness, host death, and an unclean prior activation enter persistent tray-only safe mode without an Explorer crash/login loop.
9. Unit, provider integration, state/command validation, accessibility, reliability, and packaging smoke tests pass.
10. Installer and portable build launch successfully on a clean Windows 11 x64 test account with a ChatGPT-authenticated Codex version inside the recorded verified range; a newer version is accepted only after capability and schema probes pass.
11. Documentation states the private-taskbar compatibility risk, unsigned-build warning, supported signatures, OpenAI/app-server data flow, local persistence, safe-mode recovery, and uninstall behavior.

## 16. Principal Risks

| Risk | Mitigation |
|---|---|
| Windows update changes private taskbar internals | Explicit build/signature validation, upstream tracking, fail-closed tray fallback |
| Code inside Explorer destabilizes the shell | Keep bridge minimal, validate all IPC, circuit breaker, no provider/network logic in bridge |
| App-server schema evolves | Publish a bounded verified CLI range, probe capabilities/schema, tolerate compatible additive fields, and direct newer incompatibilities to an application update |
| User expects percentage to mean “used” | Label all detailed values as “剩余额度”; tests lock `100 - usedPercent` behavior |
| Duplicate notifications after reconnect/restart | Persist deduplication using threshold crossing plus reset timestamp |
| Missing/renamed quota windows | Derive labels from duration and render missing data honestly |
| Imported upstream account features touch credentials | Curated import, forbidden-dependency scan, and process-level file-access test |
| Bad bridge activation repeats at login | Persistent activation journal, lease-based detach, breaker, and safe-mode launcher |
| Unsigned installer warning | Provide checksums and source-built artifacts; pursue signing separately rather than weakening Windows security |

## 17. Implementation Planning Boundary

Implementation planning may now determine exact project/solution names, dependency versions, upstream import mechanics, and test framework choices. It must not change the approved product behavior, process boundaries, official `stdio` data source, remaining-quota semantics, or fail-closed compatibility policy without a new design review.
