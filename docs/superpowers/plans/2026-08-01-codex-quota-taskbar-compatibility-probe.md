# Codex Quota Taskbar Compatibility Probe Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prove, before product implementation, that a minimal 190 × 36 taskbar element can attach, lay out, detach, and recover safely on the user’s Windows 11 build 26200.

**Architecture:** Build a small .NET 10 console probe and a minimal C++23 Explorer bridge extracted from the pinned TaskbarWidgets TAP/injection lifecycle. The probe records a pre-activation journal, injects only after runtime structure validation, shows a noninteractive diagnostic capsule for five seconds, requests a soft detach, and writes a sanitized JSON report. No Codex provider, settings, account code, network access, or product autostart is present.

**Tech Stack:** .NET 10 console + xUnit, C++23/CMake/MSVC, Windows XAML Diagnostics/TAP, PowerShell build scripts.

---

**Prerequisite:** Read [the approved design spec](../specs/2026-08-01-codex-quota-taskbar-design.md) and the [implementation index](2026-08-01-codex-quota-taskbar-implementation.md). Stop this plan immediately if a live probe cannot detach cleanly on build 26200.

### Task 1: Pin provenance and create the minimal build skeleton

**Files:**
- Create: `global.json`
- Create: `Directory.Build.props`
- Create: `CodexQuotaTaskbar.slnx`
- Create: `VERSION`
- Create: `THIRD_PARTY_NOTICES.md`
- Create: `src/CodexQuotaTaskbar.Core/CodexQuotaTaskbar.Core.csproj`
- Create: `tools/CodexQuotaTaskbar.CompatibilityProbe/CodexQuotaTaskbar.CompatibilityProbe.csproj`
- Create: `tests/CodexQuotaTaskbar.Core.Tests/CodexQuotaTaskbar.Core.Tests.csproj`
- Create: `src/native/CMakeLists.txt`
- Create: `build.ps1`
- Create: `build/resolve-cmake.ps1`
- Create: `build/verify.ps1`
- Create: `.gitignore`

- [ ] **Step 1: Re-verify pinned upstream revisions**

Run:

```powershell
git ls-remote https://github.com/pfcdev/TaskbarWidgets.git refs/heads/main
git ls-remote https://github.com/openai/codex.git "refs/tags/rust-v0.142.0*"
git ls-remote https://github.com/ukr8b3g-cmyk/QuotaTray.git refs/heads/main
```

Expected: the implementation notes retain TaskbarWidgets `517d655d54fae3974690b3f9461bbf6539c13a30`, Codex peeled tag `3a76f3ac68c8949d1cac6ea769b6ec7b8953a415`, and QuotaTray `be39c92432324307fe412a1611cc9000dcddfbda`. If a remote moved, keep the pinned revision rather than silently changing the plan.

- [ ] **Step 2: Run the future verification entry point and observe the expected failure**

Run:

```powershell
.\build.ps1 -Target Verify -Configuration Release
```

Expected: FAIL because `build.ps1` does not exist yet.

- [ ] **Step 3: Add the pinned .NET and project configuration**

Create the projects and solution with deterministic commands, then replace generated policy files with the contents specified below:

```powershell
dotnet new sln --name CodexQuotaTaskbar --format slnx
dotnet new classlib -n CodexQuotaTaskbar.Core -o src\CodexQuotaTaskbar.Core
dotnet new console -n CodexQuotaTaskbar.CompatibilityProbe -o tools\CodexQuotaTaskbar.CompatibilityProbe
dotnet new xunit -n CodexQuotaTaskbar.Core.Tests -o tests\CodexQuotaTaskbar.Core.Tests
dotnet sln CodexQuotaTaskbar.slnx add `
  src\CodexQuotaTaskbar.Core\CodexQuotaTaskbar.Core.csproj `
  tools\CodexQuotaTaskbar.CompatibilityProbe\CodexQuotaTaskbar.CompatibilityProbe.csproj `
  tests\CodexQuotaTaskbar.Core.Tests\CodexQuotaTaskbar.Core.Tests.csproj
dotnet add tools\CodexQuotaTaskbar.CompatibilityProbe reference src\CodexQuotaTaskbar.Core
dotnet add tests\CodexQuotaTaskbar.Core.Tests reference src\CodexQuotaTaskbar.Core
```

Use this `global.json`:

```json
{
  "sdk": {
    "version": "10.0.302",
    "rollForward": "latestPatch",
    "allowPrerelease": false
  }
}
```

Use this shared project policy:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
    <PlatformTarget>x64</PlatformTarget>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
</Project>
```

`CodexQuotaTaskbar.Core` has no Windows UI dependency. The probe references Core and embeds the native bridge only after Task 6.

- [ ] **Step 4: Add the solution and build scripts**

`build.ps1` accepts `Verify`, `Build`, and `Probe` only during this phase. `build/resolve-cmake.ps1` resolves `cmake.exe` from PATH or Visual Studio’s `Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin` without editing PATH. `Verify` runs:

```powershell
dotnet test .\CodexQuotaTaskbar.slnx -c Release
& $cmake -S src\native -B artifacts\native-verify -A x64 -DBUILD_TESTING=ON
& $cmake --build artifacts\native-verify --config Release --parallel
& $cmake --build artifacts\native-verify --target RUN_TESTS --config Release
```

- [ ] **Step 5: Run the clean skeleton verification**

Run:

```powershell
.\build.ps1 -Target Verify -Configuration Release
```

Expected: PASS with an empty Core test assembly and a minimal no-op native test target.

- [ ] **Step 6: Commit the skeleton**

```powershell
git add global.json Directory.Build.props CodexQuotaTaskbar.slnx VERSION THIRD_PARTY_NOTICES.md .gitignore src tools tests build.ps1 build
git commit -m "build: scaffold Codex quota taskbar"
```

### Task 2: Enforce the credential and upstream import boundary

**Files:**
- Create: `build/verify-sensitive-boundary.ps1`
- Create: `tests/security/forbidden-production-patterns.txt`
- Create: `tests/security/allowed-documentation-paths.txt`
- Create: `tests/security/allowed-test-harness-paths.txt`
- Modify: `build/verify.ps1`
- Modify: `THIRD_PARTY_NOTICES.md`

- [ ] **Step 1: Write the guard’s failing self-test**

The script accepts `-SelfTest` and creates a temporary production-like fixture containing `AccountManager`, `auth.json`, and `Data\Accounts`. It must return nonzero and list all three matches.

Forbidden patterns:

```text
AccountManager
auth.json
Data[\\/]Accounts
Data[\\/]IdeProfiles
active-codex-account.txt
chatgpt.com/backend-api/wham
TaskbarStats
```

Run:

```powershell
.\build\verify-sensitive-boundary.ps1 -SelfTest
```

Expected: FAIL until detection logic exists.

- [ ] **Step 2: Implement bounded source and artifact scanning**

Scan only production roots (`src`, `tools`, `installer`, `artifacts`), never `.git`, `docs`, ordinary test fixtures, `obj`, or `bin`. Resolve each path, reject symlink/reparse escapes, and return nonzero on a match. The self-test succeeds only when the inner fixture scan fails for the expected reasons.

The sole source-tree exemption is the exact, resolved `tools/CodexQuotaTaskbar.FileAccessAudit/` test-harness directory recorded in `allowed-test-harness-paths.txt`; that harness intentionally constructs a sentinel named `auth.json`. The scanner must reject wildcards, parent traversal, additional exemptions, and any occurrence of the same patterns under `src`, `installer`, or `artifacts`. Packaging must prove that this `IsPackable=false` harness is never published or copied into an artifact.

`allowed-documentation-paths.txt` is a separate path-and-pattern allowlist, not a directory bypass. It permits only the literal `auth.json` in the exact normalized entry `docs/privacy.md` (both source and unpacked package root); every other forbidden pattern in that file still fails. Artifact verification unpacks/stages ZIP and installer contents, normalizes each entry path, applies this one Markdown disclosure exception, and scans all executables/DLLs and every other entry without it. A self-test proves that renaming the file, adding a second forbidden token, or placing the same text anywhere else fails.

- [ ] **Step 3: Verify the real tree is clean**

Run:

```powershell
.\build\verify-sensitive-boundary.ps1
```

Expected: `Sensitive boundary: PASS`.

- [ ] **Step 4: Add provenance notices without importing forbidden code**

Record TaskbarWidgets and QuotaTray repository URLs, pinned SHAs, MIT copyright notices, and a statement that only described lifecycle/provider patterns were adapted. Do not add upstream `AccountManager.cs`, migration, generic widgets, Tauri, media helper, updater, or community SDK.

- [ ] **Step 5: Add the guard to every Verify run**

Run:

```powershell
.\build.ps1 -Target Verify -Configuration Release
```

Expected: sensitive boundary runs first and PASS appears before compilation.

- [ ] **Step 6: Commit the boundary**

```powershell
git add build tests/security THIRD_PARTY_NOTICES.md
git commit -m "test: forbid credential-handling code"
```

### Task 3: Implement Windows identity and the fail-closed compatibility policy

**Files:**
- Create: `src/CodexQuotaTaskbar.Core/Compatibility/WindowsBuildIdentity.cs`
- Create: `src/CodexQuotaTaskbar.Core/Compatibility/TaskbarSignature.cs`
- Create: `src/CodexQuotaTaskbar.Core/Compatibility/CompatibilityDecision.cs`
- Create: `src/CodexQuotaTaskbar.Core/Compatibility/CompatibilityPolicy.cs`
- Create: `tests/CodexQuotaTaskbar.Core.Tests/Compatibility/CompatibilityPolicyTests.cs`
- Create: `tools/CodexQuotaTaskbar.CompatibilityProbe/Windows/WindowsIdentityReader.cs`
- Create: `tools/CodexQuotaTaskbar.CompatibilityProbe/Windows/ModuleSignatureReader.cs`

- [ ] **Step 1: Write failing pure-policy tests**

In addition to the policy cases below, test the Windows reader with registry-boundary fakes for a valid UBR, a missing value, a wrong registry view/type, and access failure; every incomplete identity must fail closed as `ProbeRequired`.

```csharp
[Fact]
public void Allows_exact_26200_identity_only_after_runtime_structure_probe_passes()
{
    var identity = new WindowsBuildIdentity(10, 0, 26200, 1234, Architecture.X64);
    var signature = TaskbarSignature.Verified("explorer-version", "taskbar-hash", "SystemTray.SystemTrayFrame>Grid");

    var result = CompatibilityPolicy.Evaluate(identity, signature, structureProbePassed: true);

    Assert.Equal(CompatibilityDecision.Compatible, result.Decision);
}

[Theory]
[InlineData(26100)]
[InlineData(26201)]
public void Fails_closed_for_unrecorded_build(int build)
{
    var result = CompatibilityPolicy.Evaluate(
        new WindowsBuildIdentity(10, 0, build, 0, Architecture.X64),
        TaskbarSignature.Unknown,
        structureProbePassed: false);

    Assert.Equal(CompatibilityDecision.ProbeRequired, result.Decision);
}
```

Run:

```powershell
dotnet test tests\CodexQuotaTaskbar.Core.Tests -c Release --filter CompatibilityPolicyTests
```

Expected: FAIL because the policy types do not exist.

- [ ] **Step 2: Implement immutable identity and decision types**

Decisions are `ProbeRequired`, `Compatible`, `Unsupported`, and `UnsafePreviousActivation`. Never infer compatibility from the numeric build alone; `26200` plus a verified runtime type/parent signature is required.

- [ ] **Step 3: Implement OS and module evidence collection**

Use `RtlGetVersion` for major/minor/build and `RuntimeInformation.OSArchitecture` for x64. Read UBR separately from the 64-bit registry view at `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion`, value `UBR`; validate it as a nonnegative 32-bit integer. A missing, wrong-type, or unreadable UBR makes the identity incomplete and returns `ProbeRequired` rather than substituting zero. Record, without exposing user paths:

- `explorer.exe` file version and SHA-256;
- relevant loaded taskbar/XAML module base filenames, file versions, and SHA-256;
- the discovered XAML type/parent signature;
- monitor count and DPI values.

Do not turn an unknown module list into a compatibility pass.

- [ ] **Step 4: Run the policy tests**

Run:

```powershell
dotnet test tests\CodexQuotaTaskbar.Core.Tests -c Release --filter CompatibilityPolicyTests
```

Expected: PASS.

- [ ] **Step 5: Commit compatibility identity**

```powershell
git add src/CodexQuotaTaskbar.Core/Compatibility tools/CodexQuotaTaskbar.CompatibilityProbe/Windows tests/CodexQuotaTaskbar.Core.Tests/Compatibility
git commit -m "feat: add fail-closed Windows compatibility policy"
```

### Task 4: Extract a minimal native TAP structure probe and deterministic cleanup

**Files:**
- Create: `src/native/common/com_ptr.h`
- Create: `src/native/common/win32_error.h`
- Create: `src/native/taskbar-bridge/bridge_exports.h`
- Create: `src/native/taskbar-bridge/dllmain.cpp`
- Create: `src/native/taskbar-bridge/xaml_taskbar_probe.h`
- Create: `src/native/taskbar-bridge/xaml_taskbar_probe.cpp`
- Create: `src/native/taskbar-bridge/probe_capsule.h`
- Create: `src/native/taskbar-bridge/probe_capsule.cpp`
- Create: `src/native/taskbar-bridge/bridge_runtime.h`
- Create: `src/native/taskbar-bridge/bridge_runtime.cpp`
- Create: `src/native/tests/xaml_taskbar_probe_tests.cpp`
- Create: `src/native/tests/bridge_lifecycle_tests.cpp`
- Modify: `src/native/CMakeLists.txt`
- Reference only: TaskbarWidgets `src/native/taskbar-hook/taskbar_widgets_hook.cpp:6297-6358,6419-6918,7056-7142,7297-7480`

- [ ] **Step 1: Write failing structure-gate tests against fakes**

Define a small boundary:

```cpp
struct VisualNodeFacts {
  std::wstring type_name;
  bool parent_is_grid;
  bool belongs_to_validated_taskbar_window;
};

ProbeDecision evaluate_visual_node(const VisualNodeFacts& facts) noexcept;
```

Tests require exact `SystemTray.SystemTrayFrame`, a `Grid` parent, and a previously validated taskbar host. Near matches must return `ignore` without calling a mutator.

Run:

```powershell
.\build.ps1 -Target Verify -Configuration Release
```

Expected: native test FAIL because the functions do not exist.

- [ ] **Step 2: Implement the pure gate before COM/TAP work**

Keep node classification free of WinRT/COM so malformed and near-match inputs are exhaustively unit tested.

- [ ] **Step 3: Extract the minimum TAP adapter**

Adapt only these behaviors from the pinned upstream revision:

- `InitializeXamlDiagnosticsEx` setup;
- `IVisualTreeServiceCallback2` watcher;
- per-XAML-thread `WH_CALLWNDPROC` handoff;
- exact type/parent validation;
- enumeration of all relevant XAML host windows;
- watcher unadvise and per-thread cleanup.

Do not copy upstream account popup, widget catalog, providers, weather/media code, dormant `CreateWindow*` wrappers, or generic JavaScript renderer.

- [ ] **Step 4: Add a five-second noninteractive diagnostic capsule**

The capsule is 190 × 36 DIP, reads `CQ · PROBE`, has no click handler, no files beyond the probe report, no provider, and no network. Insertion must save the original Grid column state; cleanup removes the element, stops timers, and restores the Grid exactly once.

- [ ] **Step 5: Add idempotent lifecycle tests**

Test `initialize → insert → detach → detach`, initialization failure, watcher failure, wrong parent, shutdown during delayed retry, callback drain, and `detach → quiesced → self-unload`. Every path must end with zero tracked inserted elements and zero live callbacks.

`DllMain` only caches its `HMODULE` and disables thread notifications. The bridge owns one bootstrap worker. On shutdown it blocks new callbacks, removes elements/hooks/watchers, waits for the callback count to reach zero, signals a `quiesced` event, closes owned handles, and calls `FreeLibraryAndExitThread` from that worker as its final instruction. It never unloads from `DllMain`, a TAP callback, or a remote `FreeLibrary` thread.

- [ ] **Step 6: Build and run native tests**

Run:

```powershell
.\build.ps1 -Target Verify -Configuration Release
```

Expected: native tests PASS; no DLL is injected by Verify.

- [ ] **Step 7: Commit the native probe boundary**

```powershell
git add src/native
git commit -m "feat: add minimal taskbar structure probe"
```

### Task 5: Implement the probe injector, activation journal, timeout, and emergency detach

**Files:**
- Create: `src/CodexQuotaTaskbar.BridgeControl/CodexQuotaTaskbar.BridgeControl.csproj`
- Create: `src/CodexQuotaTaskbar.BridgeControl/Injection/IExplorerProcessApi.cs`
- Create: `src/CodexQuotaTaskbar.BridgeControl/Injection/ExplorerInjector.cs`
- Create: `src/CodexQuotaTaskbar.BridgeControl/Injection/BridgeEventNames.cs`
- Create: `src/CodexQuotaTaskbar.BridgeControl/Safety/ActivationJournal.cs`
- Create: `tools/CodexQuotaTaskbar.CompatibilityProbe/ProbeOptions.cs`
- Create: `tools/CodexQuotaTaskbar.CompatibilityProbe/Program.cs`
- Create: `tools/CodexQuotaTaskbar.CompatibilityProbe/Safety/ExplorerResponsivenessProbe.cs`
- Create: `tools/CodexQuotaTaskbar.CompatibilityProbe/Reporting/CompatibilityReport.cs`
- Create: `tools/CodexQuotaTaskbar.CompatibilityProbe/Reporting/CompatibilityReportWriter.cs`
- Create: `tools/CodexQuotaTaskbar.CompatibilityProbe/recover-probe.ps1`
- Create: `tests/CodexQuotaTaskbar.BridgeControl.Tests/CodexQuotaTaskbar.BridgeControl.Tests.csproj`
- Create: `tests/CodexQuotaTaskbar.BridgeControl.Tests/ExplorerInjectorTests.cs`
- Create: `tests/CodexQuotaTaskbar.BridgeControl.Tests/ActivationJournalTests.cs`
- Modify: `tools/CodexQuotaTaskbar.CompatibilityProbe/CodexQuotaTaskbar.CompatibilityProbe.csproj`
- Modify: `CodexQuotaTaskbar.slnx`

- [ ] **Step 1: Write failing injection-state tests with a fake Win32 boundary**

Create the BCL-only BridgeControl project and its test project, add both to `CodexQuotaTaskbar.slnx`, reference Core from BridgeControl, reference BridgeControl from the probe, and reference both Core and BridgeControl from BridgeControl.Tests. Neither BridgeControl nor its tests may reference WPF, Host, a provider, or account code.

Cover:

- journal is `Pending` before any remote thread is created;
- `OpenProcess`, allocation, write, thread creation, ABI mismatch, or ready timeout returns a typed failure;
- a ten-second ready timeout signals shutdown and records `Unsafe`;
- detach is idempotent;
- an existing unclean journal blocks `--live` unless `--explicit-retry` is passed;
- collect-only never opens Explorer with mutation rights.

Run:

```powershell
dotnet test tests\CodexQuotaTaskbar.BridgeControl.Tests -c Release --filter "ActivationJournal|ExplorerInjector"
```

Expected: FAIL.

- [ ] **Step 2: Implement the journal and safe CLI modes**

Supported commands:

```text
--collect-only
--live --display-seconds 5
--detach
--explicit-retry
--output <json-path>
--restart-explorer-test --output <json-path>
```

Resolve a relative output path against the caller's current directory and normalize it once; accept an absolute path too. Reject a directory, reparse escape, or non-`.json` target.

Store the shared journal under `%LOCALAPPDATA%\CodexQuotaTaskbar\Probe\activation.json`. It contains app version, build, sanitized signatures, Explorer PID, start time, and `Pending|Stable|Clean|Unsafe`; no username or full path. The probe is a thin CLI over BridgeControl services from its first implementation, so the later product never needs to reference the WPF Host or duplicate injection code.

- [ ] **Step 3: Implement the injector from the upstream loader pattern**

Adapt pinned `Program.cs:296-327,437-634` into focused classes. Before creating the remote thread:

- verify Explorer belongs to the current session and expected user;
- verify x64 architecture;
- extract the bridge to a SHA-256 versioned probe directory;
- verify exported probe ABI;
- write `Pending` journal.

Wait for a per-Explorer ready event for at most ten seconds. Request detach through a named shutdown event, require the bridge's `quiesced` event, then poll the Explorer module list until the versioned DLL is absent. Do not remote-`FreeLibrary` the TAP bridge. A ten-second detach/unload timeout records `Unsafe`, leaves the mapped file untouched, and blocks reinjection until explicit recovery.

- [ ] **Step 4: Implement responsiveness and emergency recovery**

Use `SendMessageTimeout(WM_NULL, 2 seconds)` only as a responsiveness signal. Three consecutive failures mark the report unsafe and prevent reinjection. `recover-probe.ps1` signals all known probe shutdown events and starts Explorer only if Explorer has already exited; it never force-kills a responsive Explorer.

- [ ] **Step 5: Run all non-live tests**

Run:

```powershell
.\build.ps1 -Target Verify -Configuration Release
```

Expected: PASS; no Explorer injection.

- [ ] **Step 6: Commit the probe controller**

```powershell
git add CodexQuotaTaskbar.slnx src/CodexQuotaTaskbar.BridgeControl tools tests/CodexQuotaTaskbar.BridgeControl.Tests
git commit -m "feat: add guarded compatibility probe controller"
```

### Task 6: Produce and validate a collect-only probe package

**Files:**
- Modify: `tools/CodexQuotaTaskbar.CompatibilityProbe/CodexQuotaTaskbar.CompatibilityProbe.csproj`
- Modify: `build.ps1`
- Create: `build/build-native.ps1`
- Create: `build/package-probe.ps1`
- Create: `docs/compatibility/probe-runbook.md`
- Create: `tests/security/probe-artifact-scan.ps1`

- [ ] **Step 1: Wire the bridge build and embedded resource**

`build-native.ps1` produces `artifacts/native/Release/CodexQuotaTaskbar.Bridge.dll`. The probe project embeds that exact DLL and verifies its SHA-256 before extraction.

- [ ] **Step 2: Build the self-contained probe**

Run:

```powershell
.\build.ps1 -Target Probe -Configuration Release
```

Expected:

```text
artifacts/probe/CodexQuotaTaskbar.CompatibilityProbe.exe
artifacts/probe/recover-probe.ps1
artifacts/CodexQuotaTaskbar-compatibility-probe-x64.zip
artifacts/CodexQuotaTaskbar-compatibility-probe-x64.zip.sha256
```

- [ ] **Step 3: Run collect-only mode**

Run:

```powershell
.\artifacts\probe\CodexQuotaTaskbar.CompatibilityProbe.exe --collect-only --output .\artifacts\probe\collect-only.json
```

Expected: exit 0, build `26200`, x64, sanitized module/type evidence, `result: "ProbeRequired"`, and no Explorer mutation.

- [ ] **Step 4: Scan the package boundary**

Run:

```powershell
.\build\verify-sensitive-boundary.ps1
.\tests\security\probe-artifact-scan.ps1 .\artifacts\CodexQuotaTaskbar-compatibility-probe-x64.zip
```

Expected: PASS; no banned strings, account code, network import, or autostart behavior.

- [ ] **Step 5: Commit packaging and runbook**

```powershell
git add tools build.ps1 build docs/compatibility tests/security
git commit -m "build: package compatibility probe"
```

### Task 7: Execute the live build-26200 compatibility gate

**Files:**
- Create: `docs/compatibility/26200-report.json`
- Create: `docs/compatibility/26200-notes.md`

- [ ] **Step 1: Verify the preflight and recovery path**

Before live injection:

```powershell
.\artifacts\probe\CodexQuotaTaskbar.CompatibilityProbe.exe --detach
.\artifacts\probe\CodexQuotaTaskbar.CompatibilityProbe.exe --collect-only --output .\artifacts\probe\preflight.json
Get-FileHash .\artifacts\CodexQuotaTaskbar-compatibility-probe-x64.zip -Algorithm SHA256
```

Expected: detach is harmless, preflight is `ProbeRequired`, and ZIP hash matches its `.sha256` file. Tell the user the five-second probe is about to appear and where the recovery script is located.

- [ ] **Step 2: Run the live probe once**

Run:

```powershell
.\artifacts\probe\CodexQuotaTaskbar.CompatibilityProbe.exe --live --display-seconds 5 --output .\artifacts\probe\live-26200.json
```

Expected:

- one `CQ · PROBE` capsule per discovered supported taskbar;
- ready within ten seconds;
- visible for five seconds;
- capsule removed and Grid layout restored;
- Explorer remains responsive;
- report result `Compatible`.

If any expectation fails, run `recover-probe.ps1`, preserve diagnostics, set result `Unsafe` or `Unsupported`, and **stop the entire implementation**.

- [ ] **Step 3: Exercise Explorer restart recovery without autostart**

Run:

```powershell
.\artifacts\probe\CodexQuotaTaskbar.CompatibilityProbe.exe --restart-explorer-test --output .\artifacts\probe\restart-26200.json
.\artifacts\probe\CodexQuotaTaskbar.CompatibilityProbe.exe --live --display-seconds 5 --output .\artifacts\probe\live-after-restart-26200.json
```

The restart command must detach first, ask Explorer to exit gracefully, wait for the shell to return, and never register startup. Expected: the second cycle attaches/detaches cleanly and no stale capsule remains.

- [ ] **Step 4: Exercise current display topology**

Record monitor count, taskbar host count, DPI, taskbar bounds, and which hosts received a capsule. If more than one taskbar exists, every selected taskbar must pass. If the current machine has only one, record that limitation; mixed-DPI and secondary-taskbar release testing remains mandatory in Plan 3.

- [ ] **Step 5: Sanitize and commit the compatibility evidence**

Copy only approved fields from the runtime report to `docs/compatibility/26200-report.json`: versions, hashes, type signatures, counts, timings, decisions, and cleanup results. Remove usernames, absolute user paths, window titles, and process command lines.

```powershell
git add docs/compatibility/26200-report.json docs/compatibility/26200-notes.md
git commit -m "test: verify taskbar compatibility on Windows 26200"
```

### Task 8: Close the probe plan gate

**Files:**
- Modify: `docs/compatibility/26200-notes.md`
- Modify: `docs/superpowers/plans/2026-08-01-codex-quota-taskbar-compatibility-probe.md`

- [ ] **Step 1: Run the complete non-live verification once more**

Run:

```powershell
.\build.ps1 -Target Verify -Configuration Release
git status --short
```

Expected: all automated tests PASS and the worktree contains only the intentional plan checkbox/report update.

- [ ] **Step 2: Record the gate decision**

Mark this plan’s tasks complete only when `docs/compatibility/26200-report.json` says `Compatible` and cleanup/restart assertions pass.

- [ ] **Step 3: Commit the gate result**

```powershell
git add docs/compatibility docs/superpowers/plans/2026-08-01-codex-quota-taskbar-compatibility-probe.md
git commit -m "docs: close Windows 26200 compatibility gate"
```

- [ ] **Step 4: Handoff**

If compatible, begin [Provider and Tray Host Plan](2026-08-01-codex-quota-taskbar-provider-host.md). If not compatible, stop and report the exact failed evidence to the user; do not execute later plans.
