# Codex Quota Provider and Tray Host Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver a fully working tray-only Codex quota application with the approved host-owned detail card, official stdio data, safe local persistence, and no Explorer injection.

**Architecture:** A BCL-only provider launches `codex app-server --stdio`, speaks line-bounded JSONL, and serializes state changes through one event channel guarded by connection epoch and account generation. A pure Core layer validates and normalizes quota data. A .NET 10 WPF host owns the tray icon, focusable glass detail card, settings, notifications, logs, and atomic state snapshot; the native bridge remains disabled until Plan 3.

**Tech Stack:** .NET 10, C#, `System.Text.Json`, `System.Diagnostics.Process`, `Channel<T>`, WPF, WinForms `NotifyIcon`, xUnit 2.9.3, fake JSONL child process.

---

**Prerequisite:** [Compatibility Probe Plan](2026-08-01-codex-quota-taskbar-compatibility-probe.md) is complete and `docs/compatibility/26200-report.json` records `Compatible`.

### Task 1: Build the pure quota domain and fixture suite

**Files:**
- Create: `src/CodexQuotaTaskbar.Core/Quota/AccountMode.cs`
- Create: `src/CodexQuotaTaskbar.Core/Quota/AccountIdentity.cs`
- Create: `src/CodexQuotaTaskbar.Core/Quota/RawRateLimitWindow.cs`
- Create: `src/CodexQuotaTaskbar.Core/Quota/RawRateLimitBucket.cs`
- Create: `src/CodexQuotaTaskbar.Core/Quota/QuotaWindow.cs`
- Create: `src/CodexQuotaTaskbar.Core/Quota/QuotaDisplaySnapshot.cs`
- Create: `src/CodexQuotaTaskbar.Core/Quota/QuotaSnapshotNormalizer.cs`
- Create: `src/CodexQuotaTaskbar.Core/Privacy/AccountLabelMasker.cs`
- Create: `tests/CodexQuotaTaskbar.Core.Tests/Quota/QuotaSnapshotNormalizerTests.cs`
- Create: `tests/CodexQuotaTaskbar.Core.Tests/Privacy/AccountLabelMaskerTests.cs`
- Create: `tests/CodexQuotaTaskbar.Core.Tests/Fixtures/AppServer/0.142.0/*.jsonl`

- [ ] **Step 1: Add exact 0.142.0 and additive-field fixtures**

Include:

```text
account-read.logged-out.response.jsonl
account-read.chatgpt.response.jsonl
account-read.api-key.response.jsonl
account-read.bedrock.response.jsonl
rate-limits.legacy.response.jsonl
rate-limits.codex-multibucket.response.jsonl
rate-limits.one-window.response.jsonl
rate-limits.reset-count-only.response.jsonl
rate-limits.unknown-additive-fields.response.jsonl
rate-limits.invalid-percent.response.jsonl
rate-limits.updated-sparse.notification.jsonl
method-not-found.error.jsonl
```

Generate the baseline from the installed binary into a temporary directory:

```powershell
$schemaDir = Join-Path $env:TEMP 'codex-quota-schema-0.142.0'
codex app-server generate-json-schema --out $schemaDir
codex --version
```

Expected: `codex-cli 0.142.0`; copy only minimal, reviewed fixture fields into the repository, not generated user paths.

- [ ] **Step 2: Write failing normalization tests**

```csharp
[Fact]
public void Uses_codex_bucket_and_converts_used_to_remaining()
{
    var raw = Fixture.LoadRateLimits("rate-limits.codex-multibucket.response.jsonl");

    var result = QuotaSnapshotNormalizer.Normalize(raw, DateTimeOffset.UnixEpoch);

    Assert.Equal(2, result.Windows.Count);
    Assert.Equal(75d, result.Windows[0].RemainingPercent);
    Assert.Equal("5 小时", result.Windows[0].Label);
}

[Theory]
[InlineData(-1)]
[InlineData(101)]
[InlineData(double.NaN)]
public void Rejects_invalid_used_percent(double usedPercent)
{
    Assert.Throws<QuotaDataException>(() =>
        QuotaSnapshotNormalizer.Normalize(Fixture.WithUsedPercent(usedPercent), DateTimeOffset.UnixEpoch));
}
```

Also test exact `rateLimitsByLimitId["codex"]` preference, legacy fallback, one missing window, shortest/longest distinct duration, 300-minute/10,080-minute labels, timestamp conversion, unknown fields, and rejection without overwriting the previous snapshot.

Run:

```powershell
dotnet test tests\CodexQuotaTaskbar.Core.Tests -c Release --filter QuotaSnapshotNormalizerTests
```

Expected: FAIL.

- [ ] **Step 3: Implement the immutable domain and strict normalizer**

The core calculation is intentionally not clamped:

```csharp
private static double ToRemaining(double usedPercent)
{
    if (!double.IsFinite(usedPercent) || usedPercent is < 0 or > 100)
        throw new QuotaDataException("usedPercent must be finite and between 0 and 100.");
    return 100d - usedPercent;
}
```

Account modes are `LoggedOut`, `ChatGpt`, `ApiKey`, `AmazonBedrock`, and `UnknownAuthenticated`.

- [ ] **Step 4: Write and implement account masking tests**

`tomato@example.com` renders `t***@e***.com`; malformed or missing addresses render no label. Full email strings must not appear in `QuotaDisplaySnapshot.ToString()` or serialized state.

- [ ] **Step 5: Run Core tests and commit**

```powershell
dotnet test tests\CodexQuotaTaskbar.Core.Tests -c Release
git add src/CodexQuotaTaskbar.Core tests/CodexQuotaTaskbar.Core.Tests
git commit -m "feat: normalize Codex quota snapshots"
```

Expected: PASS.

### Task 2: Implement the bounded JSONL RPC connection

**Files:**
- Create: `src/CodexQuotaTaskbar.Host/CodexQuotaTaskbar.Host.csproj`
- Create: `src/CodexQuotaTaskbar.Host/Providers/Codex/JsonlRpcMessage.cs`
- Create: `src/CodexQuotaTaskbar.Host/Providers/Codex/JsonlRpcError.cs`
- Create: `src/CodexQuotaTaskbar.Host/Providers/Codex/IAppServerProcess.cs`
- Create: `src/CodexQuotaTaskbar.Host/Providers/Codex/AppServerProcess.cs`
- Create: `src/CodexQuotaTaskbar.Host/Providers/Codex/JsonlRpcConnection.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/Providers/JsonlRpcConnectionTests.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/Providers/AppServerProcessTests.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/CodexQuotaTaskbar.Host.Tests.csproj`
- Modify: `CodexQuotaTaskbar.slnx`

- [ ] **Step 1: Write failing JSONL protocol tests**

First create the Host and Host.Tests project files, add both to the solution, reference Core from Host, and reference Host from Host.Tests:

```powershell
dotnet new classlib -n CodexQuotaTaskbar.Host -o src\CodexQuotaTaskbar.Host
dotnet new xunit -n CodexQuotaTaskbar.Host.Tests -o tests\CodexQuotaTaskbar.Host.Tests
dotnet sln CodexQuotaTaskbar.slnx add `
  src\CodexQuotaTaskbar.Host\CodexQuotaTaskbar.Host.csproj `
  tests\CodexQuotaTaskbar.Host.Tests\CodexQuotaTaskbar.Host.Tests.csproj
dotnet add src\CodexQuotaTaskbar.Host reference src\CodexQuotaTaskbar.Core
dotnet add tests\CodexQuotaTaskbar.Host.Tests reference src\CodexQuotaTaskbar.Host
```

Host.Tests uses xUnit 2.9.3, `xunit.runner.visualstudio` 3.1.4, and `Microsoft.NET.Test.Sdk` 17.14.1.

Cover:

- wire requests omit `jsonrpc`;
- request IDs correlate out-of-order responses;
- one writer lock prevents interleaved lines;
- maximum 64 pending requests;
- 15-second timeout uses injected `TimeProvider`;
- stdout lines larger than 256 KiB are rejected before JSON parsing;
- notifications are queued without running subscribers on the reader loop;
- `-32001` is typed retryable overload;
- EOF fails pending requests once;
- stderr is drained but raw text is not persisted;
- dispose closes stdin, waits briefly, then kills the whole child tree if necessary.

Run:

```powershell
dotnet test tests\CodexQuotaTaskbar.Host.Tests -c Release --filter "JsonlRpc|AppServerProcess"
```

Expected: FAIL.

- [ ] **Step 2: Implement process ownership only**

`AppServerProcess` uses:

```csharp
var startInfo = new ProcessStartInfo(codexPath)
{
    UseShellExecute = false,
    CreateNoWindow = true,
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
};
startInfo.ArgumentList.Add("app-server");
startInfo.ArgumentList.Add("--stdio");
```

Never pass a token, auth path, or user prompt.

- [ ] **Step 3: Implement JSONL correlation and limits**

Use a `Channel<JsonlRpcMessage>` for notification delivery and `ConcurrentDictionary<long, TaskCompletionSource<JsonElement>>` for pending calls. Copy each response payload before disposing its source `JsonDocument`.

- [ ] **Step 4: Lock the exact initialization wire**

```json
{"method":"initialize","id":1,"params":{"clientInfo":{"name":"codex_quota_taskbar","title":"Codex Quota Taskbar","version":"0.1.0"}}}
{"method":"initialized"}
```

Do not send `params:{}` on `initialized` and do not set `experimentalApi:true`.

- [ ] **Step 5: Run focused and full tests, then commit**

```powershell
dotnet test tests\CodexQuotaTaskbar.Host.Tests -c Release --filter "JsonlRpc|AppServerProcess"
.\build.ps1 -Target Verify -Configuration Release
git add CodexQuotaTaskbar.slnx src/CodexQuotaTaskbar.Host/CodexQuotaTaskbar.Host.csproj src/CodexQuotaTaskbar.Host/Providers tests/CodexQuotaTaskbar.Host.Tests/CodexQuotaTaskbar.Host.Tests.csproj tests/CodexQuotaTaskbar.Host.Tests/Providers
git commit -m "feat: add bounded Codex JSONL connection"
```

Expected: PASS.

### Task 3: Locate Codex and implement bounded version/capability probing

**Files:**
- Create: `src/CodexQuotaTaskbar.Host/Providers/Codex/CodexExecutableLocator.cs`
- Create: `src/CodexQuotaTaskbar.Host/Providers/Codex/CodexVersion.cs`
- Create: `src/CodexQuotaTaskbar.Host/Providers/Codex/CodexCompatibility.cs`
- Create: `src/CodexQuotaTaskbar.Host/Providers/Codex/CodexCapabilityProbe.cs`
- Create: `src/CodexQuotaTaskbar.Host/Providers/Codex/AppServerContracts.cs`
- Create: `eng/verified-codex-versions.json`
- Create: `build/acquire-verified-codex.ps1`
- Create: `build/verify-codex-version-matrix.ps1`
- Create: `tests/fixtures/codex/0.142.0-required-contract.json`
- Create: `tests/fixtures/codex/0.146.0-required-contract.json`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/Providers/CodexExecutableLocatorTests.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/Providers/CodexCapabilityProbeTests.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/Providers/CodexVersionMatrixTests.cs`

- [ ] **Step 1: Write failing locator and capability tests**

Test explicit setting, PATH, installed Codex app candidate, missing executable, paths with spaces, five-second `--version` timeout, exact minimum `codex-cli 0.142.0`, exact packaging maximum `codex-cli 0.146.0`, older version, 0.146.1/newer entering `ProbeNewer`, compatible newer additive schema, missing method, and incompatible required field.

Run:

```powershell
dotnet test tests\CodexQuotaTaskbar.Host.Tests -c Release --filter "CodexExecutableLocator|CodexCapabilityProbe"
```

Expected: FAIL.

- [ ] **Step 2: Implement no-shell discovery and SemVer parsing**

Do not search or read `CODEX_HOME/auth.json`. Discovery may inspect executable paths only. Parse `codex-cli <major.minor.patch>` and return typed states: `Missing`, `UpdateCodexRequired`, `Verified`, `ProbeNewer`, `UpdateApplicationRequired`. The statically verified inclusive range is exactly 0.142.0–0.146.0; a newer version is never called verified until its live capability/schema probe passes, and release metadata must distinguish runtime-probed from packaging-verified.

- [ ] **Step 3: Implement semantic schema probing in a private temporary directory**

Run:

```text
codex app-server generate-json-schema --out <private-temp-directory>
```

Verify the required request/notification names and minimum account/rate-limit fields. Do not compare complete schema hashes; accept additive fields. Delete the temporary schema directory after evaluation.

- [ ] **Step 4: Pin and acquire both exact official Windows binaries**

Commit `eng/verified-codex-versions.json` with these immutable entries selected on 2026-08-01:

| Bound | Version/tag | Tag commit | Main npm integrity | Windows x64 npm integrity |
|---|---|---|---|---|
| minimum | `0.142.0` / `rust-v0.142.0` | `3a76f3ac68c8949d1cac6ea769b6ec7b8953a415` | `sha512-c7WftbRyE4zOLJV5p73mcGn4jVK3FmK84Q65hrZrXboZzLPDWRfbFedCbsIjU4PJS/kvgyMPhv7dH4/nhXzUyg==` | `sha512-LkARkXh3NM0XA8R8hFAgjx017fadfb9RgmqLzdhO1Qt/bDVSJnmDf4cwJ2h+FWZcm9/rRk3JC/IuqBvwg/TN+Q==` |
| maximum | `0.146.0` / `rust-v0.146.0` | `e363b08c9175ac1cbe5893615dd2cb9ddf95043b` | `sha512-yG3sPWNda/2YAIQIDq9MrrjoCTIQ7rxYM5IasrG3VBcuhCLTkgeg/JzqmJq1V98RE4MJ5jCxDXXQlOjrditFRw==` | `sha512-b3lxMYeR0+IhstNo4JjX1P9cPc1xwVcCVkPd1lD1wpWPJ0SBhpIkPczwbu3ZRkJcdyl342+rgyf4DUrbZLdrGA==` |

`acquire-verified-codex.ps1` uses `npm pack --json` for exact `@openai/codex@<version>` and `@openai/codex@<version>-win32-x64`, compares returned SRI values before extraction, rejects links/reparse entries and ambiguous executables, and stages one `codex.exe` per version under `artifacts/test-codex/`. It never runs an npm lifecycle script or changes the user's installed Codex.

- [ ] **Step 5: Exercise the exact version matrix after schema validation**

For both staged 0.142.0 and 0.146.0 binaries, assert `--version`, generate/validate schema, initialize, send `initialized`, call `account/read`, and only call `account/rateLimits/read` when the account is ChatGPT. First run against an isolated logged-out test home; then run a sanitized live smoke where the official child alone may use the user's normal login. Logged-out or API-key/Bedrock state is a valid protocol result, not incompatibility. Persist only pass/fail, version, tag/package hashes, method/field presence, and timing—never account payloads.

```powershell
.\build\acquire-verified-codex.ps1 -Manifest .\eng\verified-codex-versions.json
.\build\verify-codex-version-matrix.ps1 -Manifest .\eng\verified-codex-versions.json -RunLiveSmoke
```

Expected: both exact binaries pass the required schema and stdio matrix. The script also supports `-CheckLatestStable`; if the official npm stable version is no longer 0.146.0, it fails and requires a reviewed manifest/fixture update rather than silently widening the range.

- [ ] **Step 6: Run tests and commit**

```powershell
dotnet test tests\CodexQuotaTaskbar.Host.Tests -c Release --filter "CodexExecutableLocator|CodexCapabilityProbe|CodexVersionMatrix"
.\build\verify-codex-version-matrix.ps1 -Manifest .\eng\verified-codex-versions.json
git add eng build/acquire-verified-codex.ps1 build/verify-codex-version-matrix.ps1 src/CodexQuotaTaskbar.Host/Providers tests/CodexQuotaTaskbar.Host.Tests/Providers tests/fixtures/codex
git commit -m "feat: probe Codex app-server capabilities"
```

### Task 4: Implement the generation-safe provider state machine and reconnect loop

**Files:**
- Create: `src/CodexQuotaTaskbar.Host/Providers/Codex/ProviderGeneration.cs`
- Create: `src/CodexQuotaTaskbar.Host/Providers/Codex/ProviderEvent.cs`
- Create: `src/CodexQuotaTaskbar.Host/Providers/Codex/ProviderFailure.cs`
- Create: `src/CodexQuotaTaskbar.Host/Providers/Codex/CodexRateLimitProvider.cs`
- Create: `src/CodexQuotaTaskbar.Host/Providers/Codex/ProviderReconnectLoop.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/Providers/CodexRateLimitProviderTests.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/Providers/ProviderReconnectLoopTests.cs`

- [ ] **Step 1: Write failing account-generation and connection-epoch tests**

Required scenario:

```csharp
[Fact]
public async Task Late_old_account_response_cannot_repopulate_state_or_notify()
{
    var harness = ProviderHarness.Start(account: "A");
    var oldQuota = harness.HoldQuotaResponse();

    await harness.PublishAccountUpdatedAsync(account: "B");
    Assert.Null(harness.State.Identity);
    Assert.Empty(harness.State.Windows);

    oldQuota.Complete(Fixture.LowQuotaFor("A"));
    await harness.DrainAsync();

    Assert.DoesNotContain("A", harness.PublishedStateJson);
    Assert.Empty(harness.Notifications);
}
```

Also test two rapid account updates with reversed reads, old-process notifications after reconnect, cancellation followed by a late response, and a late old-account `<10%` response that cannot alter persisted deduplication.

Add timing tests for the initial full read, event-driven invalidation, five-minute fallback refresh, manual refresh debounced to one request per five seconds, and stale state after ten minutes without a successful read.

- [ ] **Step 2: Implement one serialized event consumer**

Network reads enqueue `ProviderEvent`; one `Channel<ProviderEvent>` consumer owns all mutable provider state. Increment `accountGeneration` synchronously on `account/updated` before awaiting anything; increment `connectionEpoch` before every new process connection. A response commits only when both captured values still match.

- [ ] **Step 3: Treat sparse updates as invalidation**

`account/rateLimits/updated` schedules one debounced, single-flight full `account/rateLimits/read`. Never publish its partial payload directly. `account/updated` clears identity, quota, and deduplication first, then reads the new account.

Schedule a full read every five minutes by default (using the configured 1–30 minute interval), mark data stale after ten minutes without success, and debounce manual refresh to one request per five seconds. All timers use injected `TimeProvider` so tests perform no real waiting.

- [ ] **Step 4: Implement reconnect timing with injected time and jitter**

Retry delays are 1, 2, 5, 10, 30, then 60 seconds with bounded random jitter. Reset after five minutes stable. Logged-out and non-ChatGPT modes wait for `account/updated`/fallback refresh rather than crash-looping.

- [ ] **Step 5: Run provider tests and commit**

```powershell
dotnet test tests\CodexQuotaTaskbar.Host.Tests -c Release --filter "CodexRateLimitProvider|ProviderReconnectLoop"
.\build.ps1 -Target Verify -Configuration Release
git add src/CodexQuotaTaskbar.Host/Providers tests/CodexQuotaTaskbar.Host.Tests/Providers
git commit -m "feat: add generation-safe Codex quota provider"
```

### Task 5: Create the hardened data root, atomic state snapshot, deduplication, and logs

**Files:**
- Create: `src/CodexQuotaTaskbar.Host/Storage/AppPaths.cs`
- Create: `src/CodexQuotaTaskbar.Host/Storage/DataRootAcl.cs`
- Create: `src/CodexQuotaTaskbar.Host/Storage/AtomicFile.cs`
- Create: `src/CodexQuotaTaskbar.Host/Storage/QuotaStateContract.cs`
- Create: `src/CodexQuotaTaskbar.Host/Storage/QuotaStateWriter.cs`
- Create: `src/CodexQuotaTaskbar.Host/Storage/NotificationStateStore.cs`
- Create: `src/CodexQuotaTaskbar.Host/Diagnostics/SanitizedRollingLog.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/Storage/DataRootAclTests.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/Storage/QuotaStateWriterTests.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/Storage/NotificationStateStoreTests.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/Diagnostics/SanitizedRollingLogTests.cs`

- [ ] **Step 1: Write failing storage-boundary tests**

Cover current-user + SYSTEM ACL, inheritance disabled, fixed directories, reparse rejection, state <=16 KiB, temp file in same directory, flush then atomic replace, old valid state retained after serialization/write failure, full email/token/raw JSON redaction, and 5 MiB rolling-log cap.

- [ ] **Step 2: Implement a fixed per-user root**

Use `%LOCALAPPDATA%\CodexQuotaTaskbar\Data` with `State`, `Commands`, `Logs`, `Safety`, and `Settings`. Never derive a path from app-server payloads. Open security-sensitive paths without following reparse points and audit ACLs at every startup.

- [ ] **Step 3: Implement the minimal state contract**

State contains schema, sequence, provider state, at most two windows, remaining percentage, duration, reset time, freshness, color state, and lease timestamp. It contains no account label or plan because the bridge does not need them.

- [ ] **Step 4: Persist low-quota notification keys only**

Key by normalized window identity plus `resetsAt`. Fixed critical threshold is 10%; clear/re-arm according to the spec. Persist no full account identifier.

- [ ] **Step 5: Run tests and commit**

```powershell
dotnet test tests\CodexQuotaTaskbar.Host.Tests -c Release --filter "Storage|NotificationStateStore|SanitizedRollingLog"
git add src/CodexQuotaTaskbar.Host/Storage src/CodexQuotaTaskbar.Host/Diagnostics tests/CodexQuotaTaskbar.Host.Tests
git commit -m "feat: harden local quota state storage"
```

### Task 6: Build the tray-only WPF shell and approved detail card

**Files:**
- Modify: `src/CodexQuotaTaskbar.Host/CodexQuotaTaskbar.Host.csproj`
- Create: `src/CodexQuotaTaskbar.Host/App.xaml`
- Create: `src/CodexQuotaTaskbar.Host/App.xaml.cs`
- Create: `src/CodexQuotaTaskbar.Host/Shell/SingleInstanceGuard.cs`
- Create: `src/CodexQuotaTaskbar.Host/Shell/HostControlProtocol.cs`
- Create: `src/CodexQuotaTaskbar.Host/Shell/HostControlServer.cs`
- Create: `src/CodexQuotaTaskbar.Host/Shell/HostControlClient.cs`
- Create: `src/CodexQuotaTaskbar.Host/Shell/IHostDetachCoordinator.cs`
- Create: `src/CodexQuotaTaskbar.Host/UI/TrayIconService.cs`
- Create: `src/CodexQuotaTaskbar.Host/UI/QuotaPopover.xaml`
- Create: `src/CodexQuotaTaskbar.Host/UI/QuotaPopover.xaml.cs`
- Create: `src/CodexQuotaTaskbar.Host/UI/QuotaPopoverViewModel.cs`
- Create: `src/CodexQuotaTaskbar.Host/UI/SettingsWindow.xaml`
- Create: `src/CodexQuotaTaskbar.Host/UI/SettingsWindow.xaml.cs`
- Create: `src/CodexQuotaTaskbar.Host/UI/GlassWindowBackdrop.cs`
- Create: `src/CodexQuotaTaskbar.Host/UI/QuotaColorPolicy.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/UI/QuotaPopoverViewModelTests.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/UI/QuotaColorPolicyTests.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/Shell/SingleInstanceGuardTests.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/Shell/HostControlTests.cs`

- [ ] **Step 1: Enable WPF and WinForms tray support**

```xml
<PropertyGroup>
  <OutputType>WinExe</OutputType>
  <UseWPF>true</UseWPF>
  <UseWindowsForms>true</UseWindowsForms>
  <ApplicationManifest>app.manifest</ApplicationManifest>
</PropertyGroup>
```

No Tauri, Rust, WebView2, or Node UI dependency is added.

- [ ] **Step 2: Write failing view-model, color-policy, and process-control tests**

Test loading, logged out, non-ChatGPT, stale, one-window, two-window, 30/29/10/9% boundaries, masked account + plan display, localized reset strings, and Refresh single-flight state.

Also test a mutex scoped to the current user session, a second launch forwarding `Show`, a bounded authenticated control message, `--detach --wait 10` acknowledgement, timeout/nonzero exit, and an absent primary instance returning success without starting WPF. Add the critical recovery case: launching `--safe-mode` while a normal primary already owns the mutex must command that primary into tray-only safe mode and must not merely open its existing injected UI.

- [ ] **Step 3: Implement single-instance and host-control behavior**

`SingleInstanceGuard` owns one per-user, per-session mutex. The primary instance serves a fixed command allowlist (`Show`, `Refresh`, `EnterSafeMode`, `DetachAndExit`) over a current-user + SYSTEM ACL named pipe with a 4 KiB limit; it never deserializes a type name or arbitrary path. A secondary normal launch forwards `Show` and exits.

Parsing `--detach --wait <seconds>` must happen before WPF/provider startup. If no primary exists, return 0. Otherwise send `DetachAndExit`; the primary stops accepting UI work, asks `IHostDetachCoordinator` to quiesce runtime work, shuts down provider/tray, acknowledges only after cleanup, and exits. Timeout or cleanup failure returns nonzero. Plan 3 will bind the same coordinator to verified bridge detach/unload before acknowledgement.

Parse `--safe-mode` before the normal second-launch path. With no primary, start this process directly in tray-only safe mode. With a primary, send `EnterSafeMode`; that primary persists safe-mode intent first, invokes `IHostDetachCoordinator.EnterSafeModeAsync`, suppresses future activation, and remains as the tray-only recovery host. The secondary exits only after acknowledgement. Plan 3 tests that acknowledgement follows capsule removal/module-unload (or lease-expiry recovery), so the shortcut cannot leave an injected normal instance running behind the mutex.

- [ ] **Step 4: Implement tray fallback first**

The tray menu contains `显示额度`, `立即刷新`, `打开 Codex`, `设置`, `开机启动`, and `退出`. Primary tray click toggles the popover. This is the guaranteed fallback when taskbar integration is unavailable.

- [ ] **Step 5: Implement the approved host-owned popover**

Use a 300 px glass card with two quota cards, masked account/plan, freshness, Refresh, and Open Codex. A user click activates it so `Esc` and keyboard navigation work. It closes on outside click or second toggle. Add UI Automation names, percentage values, and Invoke patterns; respect high contrast and reduced motion.

- [ ] **Step 6: Add a minimal settings window**

Settings: start with Windows, low-quota notifications, fallback refresh interval 1–30 minutes, all/primary taskbars (stored for Plan 3), and diagnostics/safe-mode status. Do not add account switching or automatic update.

- [ ] **Step 7: Run automated tests and a mock-data UI smoke**

```powershell
dotnet test tests\CodexQuotaTaskbar.Host.Tests -c Release --filter "QuotaPopover|QuotaColorPolicy|SingleInstanceGuard|HostControl"
dotnet run --project src\CodexQuotaTaskbar.Host -c Release -- --mock-data --safe-mode
```

Expected: tray icon appears; the detail card matches the approved A design; keyboard, `Esc`, outside click, high contrast, and 100–200% DPI are manually checked. Exit leaves no host process.

- [ ] **Step 8: Commit the shell**

```powershell
git add src/CodexQuotaTaskbar.Host tests/CodexQuotaTaskbar.Host.Tests/UI tests/CodexQuotaTaskbar.Host.Tests/Shell
git commit -m "feat: add quota tray and liquid detail card"
```

### Task 7: Add settings persistence, startup, notifications, and privacy disclosure

**Files:**
- Create: `src/CodexQuotaTaskbar.Host/Settings/AppSettings.cs`
- Create: `src/CodexQuotaTaskbar.Host/Settings/SettingsStore.cs`
- Create: `src/CodexQuotaTaskbar.Host/Shell/StartupRegistration.cs`
- Create: `src/CodexQuotaTaskbar.Host/Notifications/LowQuotaNotifier.cs`
- Create: `src/CodexQuotaTaskbar.Host/Shell/CodexLauncher.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/Settings/SettingsStoreTests.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/Shell/StartupRegistrationTests.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/Notifications/LowQuotaNotifierTests.cs`
- Create: `docs/privacy.md`

- [ ] **Step 1: Write failing persistence and notification tests**

Test atomic settings, unknown-field tolerance, corrupt-settings fallback, real HKCU state abstraction, one notification per reset window across host restart, no repeat on reconnect, and re-arm after reset timestamp change.

- [ ] **Step 2: Implement HKCU startup without elevation**

Register the published host under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\CodexQuotaTaskbar`. The UI reflects the actual registry value, not only cached settings. `--safe-mode` never enables startup by itself.

- [ ] **Step 3: Implement Windows notifications and Codex launch action**

Notifications contain only the window label and remaining percentage, never email/account. Prefer the installed Codex desktop app for Open Codex; otherwise open the documented Codex surface or show a clear unavailable action.

- [ ] **Step 4: Write privacy documentation**

State that the host launches the official app-server, which contacts OpenAI with the existing login; `clientInfo` may appear in OpenAI compliance logs; local product persistence is limited to settings, safety state, dedup keys, sanitized quota values, and bounded logs; the host never reads `auth.json`.

- [ ] **Step 5: Run tests and commit**

```powershell
dotnet test tests\CodexQuotaTaskbar.Host.Tests -c Release --filter "Settings|StartupRegistration|LowQuotaNotifier"
git add src/CodexQuotaTaskbar.Host tests/CodexQuotaTaskbar.Host.Tests docs/privacy.md
git commit -m "feat: add quota settings and notifications"
```

### Task 8: Add the fake app-server and provider integration matrix

**Files:**
- Create: `tests/CodexQuotaTaskbar.FakeAppServer/CodexQuotaTaskbar.FakeAppServer.csproj`
- Create: `tests/CodexQuotaTaskbar.FakeAppServer/Program.cs`
- Create: `tests/CodexQuotaTaskbar.FakeAppServer/ScenarioScript.cs`
- Create: `tests/CodexQuotaTaskbar.Provider.IntegrationTests/CodexQuotaTaskbar.Provider.IntegrationTests.csproj`
- Create: `tests/CodexQuotaTaskbar.Provider.IntegrationTests/AppServerIntegrationTests.cs`
- Create: `tests/CodexQuotaTaskbar.Provider.IntegrationTests/Scenarios/*.json`
- Create: `tools/CodexQuotaTaskbar.FileAccessAudit/CodexQuotaTaskbar.FileAccessAudit.csproj`
- Create: `tools/CodexQuotaTaskbar.FileAccessAudit/Program.cs`
- Modify: `CodexQuotaTaskbar.slnx`
- Modify: `build/verify.ps1`

- [ ] **Step 1: Write scenario scripts before the fake server**

Create all three project files and add them to `CodexQuotaTaskbar.slnx`. Provider.IntegrationTests references Host and has a build-order project reference to FakeAppServer (`ReferenceOutputAssembly=false`). Mark FileAccessAudit `IsPackable=false`; it is the one exact source path allowed by `tests/security/allowed-test-harness-paths.txt` and no publish/package target may copy it.

Required scenarios:

```text
handshake-order.json
responses-out-of-order.json
account-generation-race.json
rapid-account-updates.json
old-process-after-reconnect.json
stderr-noise.json
oversized-line.json
hung-child.json
retryable-overload.json
one-window.json
```

- [ ] **Step 2: Make integration tests fail against the missing fake server**

Run:

```powershell
dotnet test tests\CodexQuotaTaskbar.Provider.IntegrationTests -c Release
```

Expected: FAIL because the fake server executable does not exist.

- [ ] **Step 3: Implement a strict script-driven child process**

It validates initialize-before-initialized, exact `{"method":"initialized"}`, request IDs, omitted params for `account/rateLimits/read`, delayed/out-of-order responses, notifications, stderr, EOF, hang, and oversized lines. It contains no real credential or network access.

- [ ] **Step 4: Implement the file-access audit harness**

Run the host with the fake app-server under redirected test `LOCALAPPDATA`/`APPDATA` roots containing protected sentinels for:

- `.codex\auth.json`;
- Chromium `User Data\Default\Network\Cookies` and legacy `Cookies`;
- Edge and Brave equivalents;
- Firefox `Profiles\test.default\cookies.sqlite`.

Use process-level Windows file-I/O tracing plus deny/audit handles to prove the Host PID never opens, stats, maps, or enumerates any sentinel or cookie-store parent. Attribute the fake child separately so child activity cannot be mistaken for Host activity. Document that a real official app-server child owns its own credential access and is excluded from host-PID assertions; browser cookie paths are forbidden for every process owned by this product, including that child. The static scanner skips only this resolved harness source directory; it still scans every published artifact and must fail if the harness or sentinel names leak into a package.

- [ ] **Step 5: Run the integration and security matrix**

```powershell
dotnet test tests\CodexQuotaTaskbar.Provider.IntegrationTests -c Release
dotnet run --project tools\CodexQuotaTaskbar.FileAccessAudit -c Release -- --host .\src\CodexQuotaTaskbar.Host
.\build\verify-sensitive-boundary.ps1
.\build.ps1 -Target Verify -Configuration Release
```

Expected: PASS with zero Host file-I/O events for the credential and browser-cookie sentinels; no orphan fake server remains. `build/verify.ps1` explicitly runs Provider.IntegrationTests (in addition to solution membership), so the cross-plan Verify gate cannot silently omit this matrix.

- [ ] **Step 6: Commit integration coverage**

```powershell
git add CodexQuotaTaskbar.slnx build/verify.ps1 tests/CodexQuotaTaskbar.FakeAppServer tests/CodexQuotaTaskbar.Provider.IntegrationTests tools/CodexQuotaTaskbar.FileAccessAudit
git commit -m "test: cover Codex provider lifecycle and privacy"
```

### Task 9: Publish and accept the tray-only milestone

**Files:**
- Modify: `build.ps1`
- Create: `build/build-product.ps1`
- Create: `build/package-portable.ps1`
- Create: `docs/troubleshooting.md`
- Modify: `docs/superpowers/plans/2026-08-01-codex-quota-taskbar-provider-host.md`

- [ ] **Step 1: Add self-contained x64 publish**

```powershell
dotnet publish src\CodexQuotaTaskbar.Host\CodexQuotaTaskbar.Host.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o artifacts\CodexQuotaTaskbar
```

- [ ] **Step 2: Build the tray-only portable archive**

```powershell
.\build.ps1 -Target Build -Configuration Release
.\build\package-portable.ps1 -Input artifacts\CodexQuotaTaskbar -Output artifacts\CodexQuotaTaskbar-tray-milestone-x64.zip
```

Expected: ZIP plus `.sha256`; no native bridge is activated by this milestone.

- [ ] **Step 3: Run on the real current Codex login in safe mode**

```powershell
.\artifacts\CodexQuotaTaskbar\CodexQuotaTaskbar.exe --safe-mode
```

Expected: the tray detail card matches `codex /status` or `account/rateLimits/read` after used-to-remaining conversion, refresh works, reset times are local, and no full email appears in files/logs.

- [ ] **Step 4: Exercise failure states**

Temporarily point settings at a missing executable, fake old version, API-key fixture, logged-out fixture, one-window fixture, and hung child. Expected: each approved UX state appears without a crash loop or stale prior-account identity.

- [ ] **Step 5: Run the full gate and commit**

```powershell
.\build.ps1 -Target Verify -Configuration Release
.\build\verify-sensitive-boundary.ps1
git status --short
git add build.ps1 build docs/troubleshooting.md docs/superpowers/plans/2026-08-01-codex-quota-taskbar-provider-host.md
git commit -m "build: accept tray-only Codex quota milestone"
```

Expected: all tests PASS. Continue to [Taskbar UI and Release Plan](2026-08-01-codex-quota-taskbar-ui-release.md).
