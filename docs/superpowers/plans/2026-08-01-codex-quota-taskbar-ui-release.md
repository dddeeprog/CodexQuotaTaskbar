# Codex Quota Taskbar UI and Release Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Connect the proven build-26200 bridge to the working Codex host, render the approved Liquid Capsule in every selected taskbar, enforce persistent safety controls, and ship installer plus portable release artifacts.

**Architecture:** Promote the minimal probe bridge into a focused Explorer-side renderer that reads one bounded atomic state file and writes bounded atomic command files. The .NET host injects only after compatibility and safety gates, owns account/provider/popover/settings/notifications, validates click geometry, and falls back to its already-working tray UI. A persistent activation journal, host lease, responsiveness breaker, and safe-mode launcher prevent repeated shell failures.

**Tech Stack:** .NET 10 WPF/WinForms host, C++23/CMake/MSVC bridge, Windows XAML Diagnostics/TAP, JSON state/command files, xUnit + CTest, NSIS 3, PowerShell release scripts.

---

**Prerequisites:**

- `docs/compatibility/26200-report.json` says `Compatible`.
- [Provider and Tray Host Plan](2026-08-01-codex-quota-taskbar-provider-host.md) is complete and its real-Codex tray milestone passes.
- All changes continue to satisfy `build/verify-sensitive-boundary.ps1`.

### Task 1: Promote the probe bridge into a versioned state renderer

**Files:**
- Rename: `src/native/taskbar-bridge/probe_capsule.*` → `src/native/taskbar-bridge/liquid_capsule_renderer.*`
- Create: `src/native/taskbar-bridge/quota_state_contract.h`
- Create: `src/native/taskbar-bridge/quota_state_reader.h`
- Create: `src/native/taskbar-bridge/quota_state_reader.cpp`
- Create: `src/native/taskbar-bridge/data_root_resolver.h`
- Create: `src/native/taskbar-bridge/data_root_resolver.cpp`
- Modify: `src/native/taskbar-bridge/bridge_exports.h`
- Modify: `src/native/taskbar-bridge/bridge_runtime.*`
- Create: `src/native/tests/quota_state_reader_tests.cpp`
- Create: `src/native/tests/quota_state_fuzz_tests.cpp`
- Create: `tests/fixtures/quota-state-72-41.json`
- Modify: `src/native/CMakeLists.txt`
- Modify: `src/CodexQuotaTaskbar.Host/Storage/QuotaStateContract.cs`
- Modify: `src/CodexQuotaTaskbar.Host/CodexQuotaTaskbar.Host.csproj`
- Modify: `tools/CodexQuotaTaskbar.CompatibilityProbe/CodexQuotaTaskbar.CompatibilityProbe.csproj`
- Modify: `tools/CodexQuotaTaskbar.CompatibilityProbe/Program.cs`
- Modify: `build/build-native.ps1`
- Modify: `build/verify.ps1`
- Modify: `.gitignore`

- [ ] **Step 1: Freeze the cross-language v1 state schema**

Use identical field names and limits in C# and C++:

```json
{
  "schema": 1,
  "sequence": 42,
  "providerState": "ready",
  "leaseUnixMs": 1785519000000,
  "stale": false,
  "windows": [
    {"label":"5 小时","remaining":72.0,"durationMins":300,"resetsAt":1785526800,"color":"primary"},
    {"label":"每周","remaining":41.0,"durationMins":10080,"resetsAt":1786123200,"color":"secondary"}
  ]
}
```

Limits: 16 KiB file, two windows, label 16 UTF-16 code units after conversion, finite percentage 0–100, valid enums only, sane Unix timestamp range, monotonically increasing sequence.

- [ ] **Step 2: Write failing native decoder tests and fuzz corpus**

Cover valid state, one window, unavailable, stale, truncated JSON, >16 KiB, invalid UTF-8, unknown schema, extra fields, invalid enum, NaN-like token, negative/101%, oversized label, old sequence, reparse file, and expired lease.

Run:

```powershell
.\build.ps1 -Target Verify -Configuration Release
```

Expected: native tests FAIL.

- [ ] **Step 3: Implement fixed-root resolution and safe reads**

Derive `%LOCALAPPDATA%\CodexQuotaTaskbar\Data` from the injected module’s versioned runtime location; do not consume an arbitrary path from a command or state payload. Open the fixed state file without following reparse points, verify owner/ACL expectations, cap bytes before parsing, and keep the previous valid snapshot on rejection.

- [ ] **Step 4: Implement sequence and lease behavior**

Ignore non-increasing sequence numbers. If `leaseUnixMs` is more than 30 seconds old, run the same quiesce/callback-drain/self-unload path as explicit shutdown; remove all inserted UI, stop command processing, and unload the versioned module. Do not keep displaying a stale capsule or mapped bridge after host death.

Update the compatibility probe resource to the promoted bridge and add `--mock-state <fixture>` so native visual smoke tests can publish a bounded state without starting the real provider.

- [ ] **Step 5: Establish the production bridge-resource pipeline now**

`build/build-native.ps1` writes the Release x64 DLL and SHA-256 to `artifacts/native/Release/`. The Host project embeds that exact file as logical resource `CodexQuotaTaskbar.Bridge.dll` when `CodexQuotaBridgePath` is supplied; its build target fails if the file or matching hash is absent. Ignore the generated DLL/hash under source `Resources`—do not commit binaries.

Change `build/verify.ps1` ordering to build and test native code first, then run .NET tests with the verified `CodexQuotaBridgePath`. Add a resource-presence test so every subsequent BridgeExtractor/supervisor/live test exercises the production embedded-resource path. This wiring must exist before Tasks 4–5; packaging only consumes it later.

- [ ] **Step 6: Run tests and commit**

```powershell
.\build.ps1 -Target Verify -Configuration Release
git add .gitignore build src/native src/CodexQuotaTaskbar.Host/CodexQuotaTaskbar.Host.csproj src/CodexQuotaTaskbar.Host/Storage tools/CodexQuotaTaskbar.CompatibilityProbe
git commit -m "feat: add bounded native quota state reader"
```

### Task 2: Implement the approved Liquid Capsule renderer

**Files:**
- Modify: `src/native/taskbar-bridge/liquid_capsule_renderer.h`
- Modify: `src/native/taskbar-bridge/liquid_capsule_renderer.cpp`
- Create: `src/native/taskbar-bridge/capsule_layout.h`
- Create: `src/native/taskbar-bridge/capsule_colors.h`
- Create: `src/native/tests/capsule_layout_tests.cpp`
- Create: `src/native/tests/capsule_color_tests.cpp`
- Create: `docs/compatibility/liquid-capsule-visual-checklist.md`

- [ ] **Step 1: Write failing layout and color tests**

Lock:

- 190 × 36 DIP outer size and 18 DIP radius;
- 25 DIP orb;
- two rows with 4 DIP rounded tracks;
- 72%/41% fill geometry;
- >=30% cool row colors, 10–29% amber, <10% red;
- neutral unavailable and reduced-saturation stale state;
- width clamping at 100–200% DPI without overlap.

- [ ] **Step 2: Implement layout as pure math first**

`CapsuleLayout ComputeCapsuleLayout(float dpiScale, Size available)` returns rectangles for shell, orb, labels, tracks, fills, and values. The XAML adapter only consumes this result.

- [ ] **Step 3: Build native XAML elements inside the validated Grid**

Create one rounded glass-like `Border`, left orb, two label/value rows, and clipped progress fills. Use native taskbar XAML types already proven by the compatibility report. Add a native tooltip derived only from the sanitized snapshot, containing remaining percentages, reset times, and freshness. No WebView, HTML, script engine, account text, tooltip network action, or popup exists in the bridge.

- [ ] **Step 4: Add accessibility and system-setting fallbacks**

Expose an accessible name such as `Codex 额度，5 小时剩余 72%，每周剩余 41%`. In high contrast or transparency-disabled mode, use solid system-compatible surfaces. Disable nonessential transition animation when reduced motion is requested.

- [ ] **Step 5: Run native tests and a mock-state visual smoke**

```powershell
.\build.ps1 -Target Verify -Configuration Release
.\build.ps1 -Target Probe -Configuration Release
.\artifacts\probe\CodexQuotaTaskbar.CompatibilityProbe.exe --live --mock-state .\tests\fixtures\quota-state-72-41.json --display-seconds 15 --explicit-retry --output .\artifacts\probe\liquid-capsule-visual.json
```

Expected: the true taskbar capsule matches the approved A mockup at 100%, 125%, 150%, and 200% scaling, then detaches cleanly on exit.

- [ ] **Step 6: Commit the renderer**

```powershell
git add src/native docs/compatibility/liquid-capsule-visual-checklist.md
git commit -m "feat: render liquid Codex quota capsule"
```

### Task 3: Implement atomic click commands and safe popover placement

**Files:**
- Create: `src/native/taskbar-bridge/command_contract.h`
- Create: `src/native/taskbar-bridge/command_writer.h`
- Create: `src/native/taskbar-bridge/command_writer.cpp`
- Create: `src/CodexQuotaTaskbar.Host/Bridge/ActivationBlock.cs`
- Create: `src/CodexQuotaTaskbar.Host/Bridge/ActivationMapping.cs`
- Create: `src/CodexQuotaTaskbar.Host/Commands/CommandContract.cs`
- Create: `src/CodexQuotaTaskbar.Host/Commands/CommandInbox.cs`
- Create: `src/CodexQuotaTaskbar.Host/UI/PopoverPlacement.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/Commands/CommandInboxTests.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/UI/PopoverPlacementTests.cs`
- Create: `src/native/tests/command_writer_tests.cpp`

- [ ] **Step 1: Define the v1 command and activation contracts**

The non-persistent activation mapping is named `Local\CodexQuotaTaskbar.Activation.<explorerPid>` and contains fixed-size version, host PID, 256-bit session nonce, and lifecycle event identifiers. The bridge derives the data root from its module path.

Command JSON contains only:

```json
{
  "schema": 1,
  "nonce": "base64url-session-nonce",
  "commandId": "guid",
  "timestampUnixMs": 1785519000000,
  "action": "togglePopover",
  "instanceId": "validated-instance",
  "monitor": "sanitized-device-id",
  "dpi": 144,
  "taskbarEdge": "bottom",
  "anchorPx": {"left":1600,"top":1030,"right":1790,"bottom":1066}
}
```

Maximum file size is 4 KiB.

- [ ] **Step 2: Write failing atomic writer and inbox tests**

Test GUID uniqueness, `.tmp` + full-write check + `FlushFileBuffers` + write-through atomic rename, session nonce, ±5-minute freshness, 4 KiB, schema/action allowlist, reparse/ACL rejection, host atomic rename to `.processing`, one-consumer claim, deletion after processing, and cleanup of temp/final/processing files older than five minutes.

- [ ] **Step 3: Implement native command publication**

Primary click writes `togglePopover`; right click writes `contextMenu`; no command accepts an executable or path. If any write/flush/rename step fails, delete the temp file and keep Explorer responsive.

- [ ] **Step 4: Implement host consumption and geometry validation**

The anchor must intersect the identified monitor bounds and remain inside the already-validated taskbar window bounds. `PopoverPlacement` chooses an edge-relative position and clamps the **popover**, not the anchor, to the monitor work area. Reject impossible DPI, rectangles, monitor changes, nonce, or stale timestamps.

- [ ] **Step 5: Connect approved popover behavior**

The explicit capsule click activates the host-owned popover, enabling keyboard navigation and `Esc`; second click toggles it closed. Right click opens the host menu at the validated anchor. Background quota refresh never steals focus.

- [ ] **Step 6: Run tests and commit**

```powershell
.\build.ps1 -Target Verify -Configuration Release
git add src/native src/CodexQuotaTaskbar.Host/Bridge src/CodexQuotaTaskbar.Host/Commands src/CodexQuotaTaskbar.Host/UI tests/CodexQuotaTaskbar.Host.Tests
git commit -m "feat: connect taskbar capsule commands"
```

### Task 4: Promote compatibility probing into the persistent bridge supervisor

**Files:**
- Create: `src/CodexQuotaTaskbar.BridgeControl/Bridge/BridgeAbi.cs`
- Create: `src/CodexQuotaTaskbar.BridgeControl/Bridge/BridgeExtractor.cs`
- Modify: `src/CodexQuotaTaskbar.BridgeControl/Injection/ExplorerInjector.cs`
- Modify: `src/CodexQuotaTaskbar.BridgeControl/Injection/BridgeEventNames.cs`
- Modify: `src/CodexQuotaTaskbar.BridgeControl/Safety/ActivationJournal.cs`
- Create: `src/CodexQuotaTaskbar.BridgeControl/Safety/BridgeLease.cs`
- Create: `src/CodexQuotaTaskbar.BridgeControl/Safety/ExplorerWatchdog.cs`
- Create: `src/CodexQuotaTaskbar.BridgeControl/Safety/SafetyModeState.cs`
- Create: `src/CodexQuotaTaskbar.Host/Bridge/BridgeSupervisor.cs`
- Modify: `src/CodexQuotaTaskbar.Host/CodexQuotaTaskbar.Host.csproj`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/Bridge/BridgeSupervisorTests.cs`
- Create: `tests/CodexQuotaTaskbar.BridgeControl.Tests/BridgeExtractorTests.cs`
- Create: `tests/CodexQuotaTaskbar.BridgeControl.Tests/ActivationJournalTests.cs`
- Create: `tests/CodexQuotaTaskbar.BridgeControl.Tests/ExplorerWatchdogTests.cs`
- Modify: `src/CodexQuotaTaskbar.Host/App.xaml.cs`

- [ ] **Step 1: Write failing persistent-safety tests**

Cover:

- `Pending` journal is written before injection;
- stable interval transitions to `Stable`;
- clean detach transitions to `Clean`;
- prior `Pending`/`Unsafe` forces tray-only on next host start;
- `--safe-mode` never injects;
- only `--explicit-retry` or a new approved app/signature rule clears the gate;
- ready timeout after ten seconds trips the breaker;
- three responsiveness failures or two Explorer exits in ten minutes persist `Unsafe`;
- host lease expiry removes bridge UI within 30 seconds;
- normal Explorer restart reattaches only after full compatibility validation;
- `DetachAndExit` acknowledges only after `quiesced` and the bridge module is absent from every target Explorer process;
- launching `--safe-mode` while a normal primary owns the mutex sends `EnterSafeMode`, persists the gate before detach, removes every capsule, and leaves that primary running tray-only without a second host.

- [ ] **Step 2: Extend the existing shared BridgeControl services**

Keep injector/signature/journal logic in the BCL-only `CodexQuotaTaskbar.BridgeControl` project created by the probe plan. Add extraction, ABI, lease, and watchdog services there; do not move them into or reference the WPF Host. Both the probe and Host remain thin callers of the same library, and the Host adds only orchestration/UI. Validate the embedded resource hash and exported ABI before activation.

- [ ] **Step 3: Implement the activation journal and lease**

Journal fields: app version, build/signature ID, Explorer PID, activation timestamp, state, and failure category. No absolute path or account data. Publish a lease update at most every ten seconds through the state file. Bridge self-detaches after 30 seconds stale.

- [ ] **Step 4: Implement normal and safe startup**

Default startup:

1. initialize tray/provider;
2. audit data root;
3. load compatibility evidence;
4. inspect prior activation journal;
5. stay tray-only or activate bridge;
6. publish first state only after bridge ready.

Safe mode always stops after step 2 and exposes diagnostics/retry.

Bind the provider-plan `IHostDetachCoordinator` to BridgeSupervisor. `--detach --wait 10` stops new activations, signals shutdown, waits for `quiesced`, verifies the DLL is no longer mapped, writes `Clean`, and only then lets HostControlServer acknowledge and exit. A timeout returns failure and leaves the versioned runtime directory intact for safe recovery.

For `EnterSafeMode`, persist safe-mode intent before touching the bridge, stop lease renewal, request the same clean detach, and keep the existing host alive with tray/provider/diagnostics only. A normal self-unload is acknowledged after module absence. If explicit unload stalls, the stopped lease must trigger the bridge's quiesce/self-unload path within 30 seconds; the host displays `安全模式（桥接清理中）`, never reinjects, and acknowledges tray-only recovery only after capsule removal and module absence. Test a subsequent normal launch remains gated until explicit retry.

- [ ] **Step 5: Run failure-injection tests and commit**

```powershell
dotnet test tests\CodexQuotaTaskbar.BridgeControl.Tests -c Release --filter "BridgeExtractor|ActivationJournal|ExplorerWatchdog"
dotnet test tests\CodexQuotaTaskbar.Host.Tests -c Release --filter "BridgeSupervisor|HostControl"
.\build.ps1 -Target Verify -Configuration Release
git add src/CodexQuotaTaskbar.BridgeControl src/CodexQuotaTaskbar.Host tools tests/CodexQuotaTaskbar.BridgeControl.Tests tests/CodexQuotaTaskbar.Host.Tests
git commit -m "feat: add persistent taskbar safety supervisor"
```

### Task 5: Complete multi-monitor, Explorer restart, DPI, and taskbar lifecycle support

**Files:**
- Create: `src/native/taskbar-bridge/taskbar_host_enumerator.h`
- Create: `src/native/taskbar-bridge/taskbar_host_enumerator.cpp`
- Create: `src/native/taskbar-bridge/taskbar_instance.h`
- Create: `src/native/taskbar-bridge/taskbar_instance.cpp`
- Create: `src/native/taskbar-bridge/taskbar_geometry.h`
- Create: `src/native/tests/taskbar_host_enumerator_tests.cpp`
- Create: `src/native/tests/taskbar_geometry_tests.cpp`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/Bridge/DisplayTopologyTests.cs`
- Modify: `src/CodexQuotaTaskbar.Host/Settings/AppSettings.cs`

- [ ] **Step 1: Write failing host-enumeration tests**

Use replaceable Win32/XAML enumeration boundaries to test one primary host, two XAML hosts, irrelevant host windows, destroyed host during enumeration, Explorer restart with new PIDs, and primary-only/all-taskbar setting.

- [ ] **Step 2: Enumerate actual XAML taskbar hosts, not only `Shell_TrayWnd`**

The pinned upstream discovers all `XamlExplorerHostIslandWindow` hosts but only associates the primary `Shell_TrayWnd`. Extend the proven probe logic to validate each XAML host through exact type/parent structure and monitor/taskbar bounds. Do not assume `Shell_SecondaryTrayWnd` exists on current Windows 11.

- [ ] **Step 3: Give every inserted capsule a stable session instance ID**

Build it from Explorer PID, XAML thread ID, monitor device ID, and an activation nonce. Never persist HWND values across Explorer restart. Commands identify the current instance only.

- [ ] **Step 4: Handle topology changes**

On display/DPI/taskbar recreation: close the popover, detach affected instances, re-enumerate, revalidate, then reinsert. Auto-hide placement uses current taskbar window bounds; mixed DPI uses the clicked instance’s effective DPI.

- [ ] **Step 5: Run automated and live matrix tests**

Automated:

```powershell
.\build.ps1 -Target Verify -Configuration Release
```

Live matrix on disposable/test Windows sessions:

- primary bottom taskbar;
- auto-hide;
- one and two monitors;
- mixed 100%/150% DPI;
- center/left taskbar alignment;
- Explorer restart;
- light/dark/high-contrast/transparency-disabled.

Expected: one capsule per selected supported taskbar, correct anchor, no clock/icon overlap, clean detach.

- [ ] **Step 6: Commit lifecycle support**

```powershell
git add src/native src/CodexQuotaTaskbar.Host tests
git commit -m "feat: support taskbar lifecycle and multiple displays"
```

### Task 6: Integrate taskbar settings, notifications, diagnostics, and recovery UX

**Files:**
- Modify: `src/CodexQuotaTaskbar.Host/UI/SettingsWindow.xaml`
- Modify: `src/CodexQuotaTaskbar.Host/UI/SettingsWindow.xaml.cs`
- Modify: `src/CodexQuotaTaskbar.Host/UI/TrayIconService.cs`
- Create: `src/CodexQuotaTaskbar.Host/UI/SafetyStatusViewModel.cs`
- Create: `src/CodexQuotaTaskbar.Host/Diagnostics/CompatibilityReportExporter.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/UI/SafetyStatusViewModelTests.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/Diagnostics/CompatibilityReportExporterTests.cs`
- Modify: `docs/troubleshooting.md`

- [ ] **Step 1: Write failing safety-UX tests**

Test unsupported, pending previous activation, lease expiry, responsiveness breaker, explicit retry, retry rejection on changed unverified signature, and sanitized report export.

- [ ] **Step 2: Add visible tray-only status and recovery controls**

Settings and tray explain `任务栏已启用`, `安全模式`, `当前 Windows 签名未验证`, or `上次激活异常`. Provide `重试任务栏嵌入` only after showing the risk; it invokes the same minimal structure probe before production activation.

- [ ] **Step 3: Connect taskbar display preferences**

`全部任务栏` and `仅主任务栏` changes trigger clean detach/re-enumeration. Never mutate live layout before the new selection passes validation.

- [ ] **Step 4: Export sanitized diagnostics**

Include versions, hashes, type signatures, timings, decisions, monitor/DPI counts, and error categories. Exclude account, full paths, environment dump, raw app-server messages, and command-line content.

- [ ] **Step 5: Run tests and commit**

```powershell
dotnet test tests\CodexQuotaTaskbar.Host.Tests -c Release --filter "SafetyStatus|CompatibilityReportExporter"
git add src/CodexQuotaTaskbar.Host tests docs/troubleshooting.md
git commit -m "feat: add taskbar recovery and diagnostics UX"
```

### Task 7: Adapt the build and NSIS installer without legacy surfaces

**Files:**
- Modify: `build.ps1`
- Modify: `build/build-native.ps1`
- Modify: `build/build-product.ps1`
- Create: `build/build-nsis.ps1`
- Create: `build/create-release.ps1`
- Create: `build/sign-artifacts.ps1`
- Create: `installer/nsis/CodexQuotaTaskbar.nsi`
- Create: `assets/icons/codex-quota-taskbar.ico`
- Create: `tests/installer/installer-smoke.ps1`
- Create: `tests/installer/uninstaller-smoke.ps1`

- [ ] **Step 1: Install/resolve NSIS only for packaging**

First check `makensis.exe` and use it only if its version is exactly `3.12`. If absent or different, install the exact pinned version within this authorized build task:

```powershell
winget install --id NSIS.NSIS --exact --version 3.12 --source winget --accept-source-agreements --accept-package-agreements
```

Before running it, verify the selected NSIS 3.12 installer metadata reports SHA-256 `3bc2b06253a7e4957111be152ac6a536e0c7478a706e19da814038db5d706495`; after installation, require `makensis` itself to report 3.12 or stop. Record the fixed version and executable hash in `artifacts/release-manifest.json`; never fall forward to the current winget version and do not modify global security policy.

- [ ] **Step 2: Build native → embed → self-contained host in a fixed order**

`Build` performs:

1. Verify sensitive boundary.
2. Run .NET/native tests.
3. Build `CodexQuotaTaskbar.Bridge.dll` Release x64 through the Task 1 resource pipeline.
4. Verify its SHA-256 and embed the already-established logical Host resource.
5. `dotnet publish` self-contained single-file x64 and test-extract the embedded DLL to a versioned runtime directory.
6. Copy docs and notices to fixed normalized entries; privacy disclosure is exactly `docs/privacy.md`.
7. Unpack/stage every package and scan final files again. Apply `allowed-documentation-paths.txt` only to the literal `auth.json` in that exact Markdown entry; do not exempt any binary, sibling document, or other forbidden pattern.

- [ ] **Step 3: Write installer behavior tests first**

Tests inspect the compiled/unpacked script and a disposable install root for:

- `RequestExecutionLevel user`;
- `%LOCALAPPDATA%\Programs\CodexQuotaTaskbar`;
- startup option checked by default but user-toggleable;
- Start-menu normal and `安全模式` shortcuts;
- upgrade and uninstall run `--detach --wait 10` before replacing/deleting binaries and verify the DLL is no longer mapped;
- detach timeout aborts replacement/deletion and offers a graceful Explorer restart + retry path;
- runtime/bridge files always removed;
- settings/data preserved unless explicit delete checkbox selected;
- no legacy TaskbarStats, file association, updater, community SDK, or account migration;
- packaged `docs/privacy.md` may contain only its one path-scoped `auth.json` disclosure, while the same string in any renamed/other entry fails.

- [ ] **Step 4: Implement installer and portable package**

Installer registers HKCU uninstall information and optional HKCU startup. Portable package never registers startup automatically. Upgrade/uninstall treats a zero detach acknowledgement plus module-absence check as a precondition for deleting any runtime DLL. If it times out, abort safely and offer a user-confirmed graceful Explorer restart before one retry; never force-kill responsive Explorer or report success while the DLL is mapped. After absence is verified, remove every versioned runtime directory. The uninstaller offers to remove retained settings/data separately.

Validate committed `docs/compatibility/26200-report.json` against the report schema, require `result: "Compatible"`, build 26200, and matching bridge/signature evidence, then copy its sanitized approved fields to `artifacts/CodexQuotaTaskbar-compatibility-26200.json`. Abort packaging on absence or mismatch and record the artifact hash in the release manifest.

Run the boundary scanner against normalized unpacked/staged entries, not as a context-free raw-string rejection of required prose. The only packaged-document exception is the exact pair (`docs/privacy.md`, literal `auth.json`); binaries and all other files remain fully scanned.

- [ ] **Step 5: Build package artifacts**

```powershell
.\build.ps1 -Target Package -Configuration Release
```

Expected:

```text
artifacts/CodexQuotaTaskbarSetup-x64.exe
artifacts/CodexQuotaTaskbar-portable-x64.zip
artifacts/CodexQuotaTaskbar-compatibility-26200.json
artifacts/*.sha256
artifacts/release-manifest.json
```

- [ ] **Step 6: Run installer/uninstaller smoke tests**

Use Windows Sandbox or a disposable local/VM account with Codex 0.142.0+ ChatGPT login. Cover install, launch, startup toggle, upgrade, normal self-unload, injected detach timeout + graceful Explorer-restart retry, uninstall-preserve-data, reinstall, uninstall-delete-data, and portable run. Expected: successful paths leave no mapped module, stale capsule, or runtime DLL and require no elevation; the timeout path makes no destructive change before retry.

- [ ] **Step 7: Commit packaging**

```powershell
git add build.ps1 build installer assets tests/installer
git commit -m "build: package Codex quota taskbar"
```

### Task 8: Run security, accessibility, reliability, and privacy release gates

**Files:**
- Create: `tests/security/native-state-fuzz.ps1`
- Create: `tests/security/process-file-access-audit.ps1`
- Create: `tests/reliability/taskbar-soak.ps1`
- Create: `tests/reliability/provider-reconnect-soak.ps1`
- Create: `tests/reliability/idle-activity-soak.ps1`
- Create: `tests/accessibility/ui-automation-smoke.ps1`
- Create: `docs/compatibility/release-test-matrix.md`
- Modify: `docs/privacy.md`

- [ ] **Step 1: Run the static and artifact boundary gates**

```powershell
.\build\verify-sensitive-boundary.ps1
.\tests\security\process-file-access-audit.ps1 -Host artifacts\CodexQuotaTaskbar\CodexQuotaTaskbar.exe -TraceBridgeExplorer
```

Run the trace in a disposable Windows session with the required ETW file-I/O privilege. Enumerate existing Chrome/Chromium, Edge, Brave, Firefox, and WebView2 cookie database paths (`Network\Cookies`, legacy `Cookies`, and `cookies.sqlite`) plus the Codex credential sentinel. Filter Host PID and every Explorer PID targeted by the bridge from activation through refresh, click, detach, and lease-expiry exercises; fail on open/stat/map/directory-enumeration events touching those paths or parents. This conservative Explorer-process assertion is the process-level proof for injected bridge execution.

Expected: zero Host/target-Explorer file-I/O events for `auth.json` or browser cookie stores; artifacts contain no banned code or private endpoint. Preserve only normalized category/pass-fail counts, not private paths.

- [ ] **Step 2: Run decoder fuzz and malformed-file tests**

Feed truncated, oversized, invalid UTF-8, reparse, wrong-owner, stale nonce, invalid geometry, enum, timestamp, and percentage inputs. Expected: bridge keeps prior valid state or ignores command; Explorer remains responsive.

- [ ] **Step 3: Run accessibility smoke**

Verify UI Automation names/values/Invoke, keyboard traversal, focus order, `Esc`, high contrast, reduced motion, and color-independent status. Record results for capsule, popover, settings, tray menu, and safety dialog.

- [ ] **Step 4: Run the 30-minute/1,000-snapshot soak**

```powershell
.\tests\reliability\taskbar-soak.ps1 -Snapshots 1000 -DurationMinutes 30
```

Pass criteria after warm-up:

- zero Explorer crash/hang;
- GDI handles <= baseline +10;
- USER handles <= baseline +10;
- Explorer private bytes <= baseline +20 MiB;
- host death removes bridge UI within 30 seconds;
- next start after simulated unclean activation stays tray-only until explicit retry.

- [ ] **Step 5: Run provider-reconnect and idle-activity soaks**

```powershell
.\tests\reliability\provider-reconnect-soak.ps1 -Cycles 30 -DurationMinutes 15
.\tests\reliability\idle-activity-soak.ps1 -DurationMinutes 10 -MaxCpuSeconds 3
```

The reconnect soak uses real child processes with the script-driven fake server and cycles through clean EOF, crash, hung request/forced tree cleanup, retryable overload, stderr noise, and recovery; finish with one restart smoke for each pinned real Codex binary. Pass criteria: all 30 cycles recover within the configured backoff + request timeout, generation/epoch guards reject every late response, no stale-account publication or duplicate low-quota notification occurs, no child is orphaned, and Host handles/private bytes return within +10/+20 MiB of post-warm-up baseline.

The idle soak begins after a successful refresh and records Host/app-server CPU time, provider requests, state/lease publishes, thread wake activity, and log growth. Over ten minutes, Host CPU-time delta must be <=3 seconds, average CPU must remain <1% of one logical processor, scheduled quota reads must not exceed three, state/lease writes must not exceed 65, logs must not grow without a state transition, and no thread may exhibit sustained wakeups above 10 Hz. Any breached bound is a busy-loop failure, not a warning.

- [ ] **Step 6: Run the complete display/restart matrix**

Record build/signature, primary/secondary taskbar, mixed DPI, auto-hide, Explorer restart, transparency off, and high contrast. Every selected taskbar must pass insertion, click anchoring, and detach.

- [ ] **Step 7: Commit release evidence**

```powershell
git add tests/security tests/reliability tests/accessibility docs/compatibility/release-test-matrix.md docs/privacy.md
git commit -m "test: verify taskbar release safety"
```

### Task 9: Produce and verify the release candidate

**Files:**
- Create: `README.md`
- Create: `CHANGELOG.md`
- Create: `docs/install.md`
- Modify: `docs/superpowers/plans/2026-08-01-codex-quota-taskbar-ui-release.md`
- Modify: `VERSION`

- [ ] **Step 1: Write user documentation**

Document Windows 11 x64 support, verified build/signatures, Codex verified version range, remaining-percentage semantics, official app-server data flow, unsigned SmartScreen warning, startup setting, safe-mode shortcut, diagnostics export, update compatibility risk, and complete uninstall.

- [ ] **Step 2: Set the release-candidate version and rebuild from clean outputs**

```powershell
Remove-Item -LiteralPath .\artifacts -Recurse -Force
.\build.ps1 -Target Verify -Configuration Release
.\build\acquire-verified-codex.ps1 -Manifest .\eng\verified-codex-versions.json
.\build\verify-codex-version-matrix.ps1 -Manifest .\eng\verified-codex-versions.json -CheckLatestStable -RunLiveSmoke
.\build.ps1 -Target Package -Configuration Release
```

Before deletion, resolve and verify the absolute `artifacts` path is inside this worktree. Expected: clean reproducible artifact set and exact official Codex 0.142.0 plus 0.146.0 both pass schema/stdio checks. If official stable is no longer 0.146.0, stop for a reviewed manifest/fixture update; never publish a stale or silently widened verified range.

- [ ] **Step 3: Verify hashes and manifest**

```powershell
Get-FileHash artifacts\CodexQuotaTaskbarSetup-x64.exe -Algorithm SHA256
Get-FileHash artifacts\CodexQuotaTaskbar-portable-x64.zip -Algorithm SHA256
Get-Content artifacts\CodexQuotaTaskbar-compatibility-26200.json
Get-Content artifacts\release-manifest.json
```

Expected: values exactly match `.sha256` files and manifest; the compatibility artifact is the validated sanitized build-26200 evidence and its hash matches the manifest; manifest records source commit, upstream pins, .NET/MSVC/CMake/NSIS versions, exact verified Codex range 0.142.0–0.146.0 with package/tag hashes, and unsigned/signed state.

- [ ] **Step 4: Install the release candidate on the user machine**

Run the installer, verify the Liquid Capsule against real Codex values, open/close the detail card, refresh, open Codex, toggle startup, exercise safe mode, restart Explorer once, and uninstall/reinstall once. Preserve user settings only when selected.

- [ ] **Step 5: Run final verification and inspect the diff**

```powershell
.\build.ps1 -Target Verify -Configuration Release
git status --short
git diff --check
git log --oneline --decorate -15
```

Expected: all gates PASS and only intentional plan checkbox/release-document changes remain.

- [ ] **Step 6: Commit release candidate metadata**

```powershell
git add README.md CHANGELOG.md docs VERSION docs/superpowers/plans/2026-08-01-codex-quota-taskbar-ui-release.md
git commit -m "release: prepare Codex quota taskbar candidate"
```

- [ ] **Step 7: Request final code review before publishing**

Use `superpowers:requesting-code-review`, address blocking findings, rerun all relevant gates, then use `superpowers:finishing-a-development-branch` to decide merge/push/PR handling.
