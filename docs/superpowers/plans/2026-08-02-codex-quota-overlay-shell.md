# Codex Quota Overlay Shell Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Incrementally add a complete, runnable Codex quota Host to the existing solution that renders the approved iOS-style capsule beside each selected taskbar, reads real quota through Codex App Server, and ships settings, notifications, autostart, and a production package without loading code into Explorer.

**Architecture:** Extend the existing Core project with pure quota-presentation, App Server parsing, and taskbar-placement models, then add one WPF Host project that owns the provider process, topology discovery, per-monitor overlay windows, popover, tray menu, settings, notifications, autostart, and lifetime. Deterministic demo data remains available only as a visual harness; the normal product path uses Codex App Server.

**Tech Stack:** .NET 10, WPF, WinForms `NotifyIcon`, Win32 window/monitor APIs through focused P/Invoke seams, xUnit 2.9.3, PowerShell build and artifact-policy scripts.

---

## Scope

This plan executes the approved overlay design in `docs/superpowers/specs/2026-08-02-codex-quota-taskbar-overlay-design.md` as one complete delivery. It includes the independently testable overlay shell, official Codex App Server stdio provider, account/schema handling, stale-state recovery, settings persistence, notifications, autostart, and portable production release. Internal tasks are verification boundaries, not user-facing phases.

Do not modify or delete the existing compatibility probe, BridgeControl, or native bridge in phase 1. They remain separately verifiable diagnostics and must not become Host dependencies.

## File map

### Existing files to modify

- `CodexQuotaTaskbar.slnx`: add Host and Host.Tests under the existing solution folders.
- `src/CodexQuotaTaskbar.Core/CodexQuotaTaskbar.Core.csproj`: expose internals to the new Core tests only if needed; do not add WPF dependencies.
- `build.ps1`: add a `Host` target without changing `Probe` behavior.
- `build/verify.ps1`: run Host artifact-boundary self-tests in addition to the existing managed/native verification.
- `docs/修改日志.md`: record the completed phase with exact files and line references at handoff.

### Core files to create

- `src/CodexQuotaTaskbar.Core/Quota/QuotaWindowSnapshot.cs`: one validated quota window.
- `src/CodexQuotaTaskbar.Core/Quota/QuotaSnapshot.cs`: immutable normalized snapshot and freshness.
- `src/CodexQuotaTaskbar.Core/Quota/QuotaCapsuleProjection.cs`: deterministic two-row compact projection.
- `src/CodexQuotaTaskbar.Core/Overlay/ScreenRect.cs`: overflow-safe physical-pixel rectangle.
- `src/CodexQuotaTaskbar.Core/Overlay/TaskbarAnchor.cs`: monitor/work-area/taskbar geometry and DPI.
- `src/CodexQuotaTaskbar.Core/Overlay/OverlayPlacementCalculator.cs`: pure edge-relative placement.
- `src/CodexQuotaTaskbar.Core/Overlay/FullscreenClassifier.cs`: pure true-fullscreen classification.

### Host files to create

- `src/CodexQuotaTaskbar.Host/CodexQuotaTaskbar.Host.csproj`: WPF WinExe, no BridgeControl or probe reference.
- `src/CodexQuotaTaskbar.Host/app.manifest`: Per-Monitor-V2 declaration.
- `src/CodexQuotaTaskbar.Host/App.xaml` and `App.xaml.cs`: composition root and clean shutdown.
- `src/CodexQuotaTaskbar.Host/Properties/AssemblyInfo.cs`: test visibility only.
- `src/CodexQuotaTaskbar.Host/Platform/NativeMethods.cs`: minimum required user32/shell32/dwmapi declarations.
- `src/CodexQuotaTaskbar.Host/Platform/ITaskbarWindowApi.cs`: read-only native seam.
- `src/CodexQuotaTaskbar.Host/Platform/Win32TaskbarWindowApi.cs`: production native adapter.
- `src/CodexQuotaTaskbar.Host/Platform/TaskbarTopologySource.cs`: window snapshots to anchors.
- `src/CodexQuotaTaskbar.Host/Platform/FullscreenMonitor.cs`: foreground/fullscreen observation.
- `src/CodexQuotaTaskbar.Host/Platform/IEnvironmentEventSource.cs`: display, work-area, taskbar and session event seam.
- `src/CodexQuotaTaskbar.Host/Platform/SystemEnvironmentEventSource.cs`: production event adapter with bounded polling fallback.
- `src/CodexQuotaTaskbar.Host/Platform/IWindowPositionApi.cs`: physical-pixel HWND style, position, visibility and Z-order seam.
- `src/CodexQuotaTaskbar.Host/Platform/Win32WindowPositionApi.cs`: `SetWindowPos`/`ShowWindow` production adapter.
- `src/CodexQuotaTaskbar.Host/Platform/OverlayWindowInterop.cs`: testable HWND policy used by the WPF capsule.
- `src/CodexQuotaTaskbar.Host/Platform/ISystemPreferenceSource.cs`: high-contrast, advanced-effects and animation seam.
- `src/CodexQuotaTaskbar.Host/Platform/SystemPreferenceSource.cs`: production system-preference adapter.
- `src/CodexQuotaTaskbar.Host/Overlay/IOverlayWindow.cs`: coordinator-facing window contract.
- `src/CodexQuotaTaskbar.Host/Overlay/IOverlayWindowFactory.cs`: factory contract used before concrete WPF windows exist.
- `src/CodexQuotaTaskbar.Host/Overlay/OverlayCoordinator.cs`: reconcile one capsule per selected monitor.
- `src/CodexQuotaTaskbar.Host/UI/QuotaCapsuleWindow.xaml` and `.xaml.cs`: no-activate capsule window.
- `src/CodexQuotaTaskbar.Host/UI/QuotaCapsuleViewModel.cs`: bindable compact state and commands.
- `src/CodexQuotaTaskbar.Host/UI/QuotaPopoverWindow.xaml` and `.xaml.cs`: 300-DIP detail card.
- `src/CodexQuotaTaskbar.Host/UI/QuotaPopoverViewModel.cs`: detail content and demo actions.
- `src/CodexQuotaTaskbar.Host/UI/OverlayWindowFactory.cs`: create capsule/popover instances.
- `src/CodexQuotaTaskbar.Host/Tray/TrayController.cs`: notification-area icon and menu.
- `src/CodexQuotaTaskbar.Host/Demo/DemoQuotaSource.cs`: fixed 72%/41% snapshot only.
- `src/CodexQuotaTaskbar.Host/Lifetime/HostOptions.cs`: `--demo` and bounded smoke-test options.
- `src/CodexQuotaTaskbar.Host/Lifetime/HostLifetime.cs`: single shutdown authority.

### Tests and build files to create

- `tests/CodexQuotaTaskbar.Core.Tests/Quota/QuotaCapsuleProjectionTests.cs`.
- `tests/CodexQuotaTaskbar.Core.Tests/Overlay/OverlayPlacementCalculatorTests.cs`.
- `tests/CodexQuotaTaskbar.Core.Tests/Overlay/FullscreenClassifierTests.cs`.
- `tests/CodexQuotaTaskbar.Host.Tests/CodexQuotaTaskbar.Host.Tests.csproj`.
- `tests/CodexQuotaTaskbar.Host.Tests/TestSupport/RepositoryPaths.cs`.
- `tests/CodexQuotaTaskbar.Host.Tests/ProjectBoundaryTests.cs`.
- `tests/CodexQuotaTaskbar.Host.Tests/Platform/TaskbarTopologySourceTests.cs`.
- `tests/CodexQuotaTaskbar.Host.Tests/Platform/FullscreenMonitorTests.cs`.
- `tests/CodexQuotaTaskbar.Host.Tests/Platform/SystemEnvironmentEventSourceTests.cs`.
- `tests/CodexQuotaTaskbar.Host.Tests/Platform/OverlayWindowInteropTests.cs`.
- `tests/CodexQuotaTaskbar.Host.Tests/Overlay/OverlayCoordinatorTests.cs`.
- `tests/CodexQuotaTaskbar.Host.Tests/UI/QuotaCapsuleViewModelTests.cs`.
- `tests/CodexQuotaTaskbar.Host.Tests/UI/CapsuleThemePolicyTests.cs`.
- `tests/CodexQuotaTaskbar.Host.Tests/Lifetime/HostOptionsTests.cs`.
- `tests/CodexQuotaTaskbar.Host.Tests/Lifetime/HostLifetimeTests.cs`.
- `tests/CodexQuotaTaskbar.Host.Tests/Demo/DemoQuotaSourceTests.cs`.
- `tests/security/host-boundary-policy.ps1`.
- `tests/security/host-boundary-policy.selftest.ps1`.
- `build/package-host.ps1`.
- `docs/compatibility/overlay-shell-visual-checklist.md`.

## Dirty-worktree protocol for execution

Before Task 1, capture `git status --short`, `git diff --name-only`, and `git diff --cached --name-only`. Treat every already modified or untracked path as user-owned unless this plan created it during execution.

- Never stage or revert an unrelated path.
- If the initial cached diff is non-empty, disable every commit step for the entire execution. Never call `git commit` against a pre-populated index.
- Never use whole-file staging for a file that was already dirty before the task.
- For a pre-dirty file that receives necessary task changes, either stage only the exact task hunks with a reviewed patch or skip that task's commit.
- If the initial cached diff was empty and commits are explicitly authorized, require the index to be empty immediately before staging each task; after staging, verify the cached name list is an exact subset of that task's owned clean/new paths, then run `git diff --cached --check` and inspect `git diff --cached --stat` plus the full cached diff.
- If exact separation cannot be proven, skip the commit. Do not clean or reset the user's working tree.
- Every “Commit” step below is a conditional gate, not an unconditional command list. When any precondition fails, mark it skipped and run none of the commands in its block.

## Task 1: Add the Host project without creating an injection dependency

**Files:**
- Create: `src/CodexQuotaTaskbar.Host/CodexQuotaTaskbar.Host.csproj`
- Create: `src/CodexQuotaTaskbar.Host/app.manifest`
- Create: `src/CodexQuotaTaskbar.Host/App.xaml`
- Create: `src/CodexQuotaTaskbar.Host/App.xaml.cs`
- Create: `src/CodexQuotaTaskbar.Host/Properties/AssemblyInfo.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/CodexQuotaTaskbar.Host.Tests.csproj`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/TestSupport/RepositoryPaths.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/ProjectBoundaryTests.cs`
- Modify: `CodexQuotaTaskbar.slnx`

- [ ] **Step 1: Write the project-boundary test before the Host project exists**

Create Host.Tests first with the normal xUnit packages but no Host project reference yet. `RepositoryPaths` walks parents from `AppContext.BaseDirectory` until it finds `CodexQuotaTaskbar.slnx`, then resolves paths below that root without accepting caller-supplied traversal. The first test reads the future Host project XML and asserts that its only project reference is `CodexQuotaTaskbar.Core`:

```csharp
[Fact]
public void Host_has_no_bridge_or_probe_project_reference()
{
    var project = XDocument.Load(RepositoryPaths.HostProject);
    var references = project.Descendants("ProjectReference")
        .Select(element => Path.GetFileNameWithoutExtension((string)element.Attribute("Include")!))
        .ToArray();

    Assert.Equal(["CodexQuotaTaskbar.Core"], references);
    Assert.DoesNotContain(references, value => value.Contains("Bridge", StringComparison.Ordinal));
    Assert.DoesNotContain(references, value => value.Contains("Probe", StringComparison.Ordinal));
}
```

- [ ] **Step 2: Run the boundary test and verify the intended RED failure**

Run: `dotnet test tests/CodexQuotaTaskbar.Host.Tests/CodexQuotaTaskbar.Host.Tests.csproj -c Release --filter Host_has_no_bridge_or_probe_project_reference`

Expected: FAIL because the Host project does not exist.

- [ ] **Step 3: Create the minimal WPF Host and manifest**

Use this project shape:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <AssemblyName>CodexQuotaTaskbar</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\CodexQuotaTaskbar.Core\CodexQuotaTaskbar.Core.csproj" />
  </ItemGroup>
</Project>
```

The manifest must declare `PerMonitorV2, PerMonitor` and Windows 10/11 compatibility. `App.xaml` must omit `StartupUri`; `App.xaml.cs` owns startup and shutdown composition.

After the Host project exists, add explicit Host and Core project references to Host.Tests. No BridgeControl or probe reference is permitted.

- [ ] **Step 4: Add Host and Host.Tests to the existing solution**

Place Host under `/src/` and Host.Tests under `/tests/`; do not reorder or remove existing projects.

- [ ] **Step 5: Run the focused test and solution build**

Run: `dotnet test tests/CodexQuotaTaskbar.Host.Tests/CodexQuotaTaskbar.Host.Tests.csproj -c Release --filter Host_has_no_bridge_or_probe_project_reference`

Expected: PASS.

Run: `dotnet build CodexQuotaTaskbar.slnx -c Release`

Expected: PASS with zero warnings.

- [ ] **Step 6: Commit only this task if commits are authorized**

```powershell
git add CodexQuotaTaskbar.slnx src/CodexQuotaTaskbar.Host tests/CodexQuotaTaskbar.Host.Tests
git diff --cached --check
git diff --cached
git commit -m "feat: add standalone quota overlay host"
```

If commits are not explicitly authorized, skip this step and preserve the working tree.

## Task 2: Add validated mock quota projection in Core

**Files:**
- Create: `src/CodexQuotaTaskbar.Core/Quota/QuotaWindowSnapshot.cs`
- Create: `src/CodexQuotaTaskbar.Core/Quota/QuotaSnapshot.cs`
- Create: `src/CodexQuotaTaskbar.Core/Quota/QuotaCapsuleProjection.cs`
- Create: `tests/CodexQuotaTaskbar.Core.Tests/Quota/QuotaCapsuleProjectionTests.cs`

- [ ] **Step 1: Write failing tests for two-row selection and validation**

Cover these cases:

```csharp
[Theory]
[InlineData(72, "Cool")]
[InlineData(29, "Amber")]
[InlineData(9, "Critical")]
public void Projects_remaining_percentage_to_accessible_color_token(double remaining, string token)
```

```csharp
[Fact]
public void Orders_shorter_window_first_and_limits_capsule_to_two_rows()
```

```csharp
[Theory]
[InlineData(double.NaN)]
[InlineData(-1)]
[InlineData(101)]
public void Rejects_invalid_remaining_percentage(double remaining)
```

Also test an empty snapshot, a one-window snapshot with a neutral second row, equal-duration primary-before-secondary tie-breaking, and fresh/stale/unavailable snapshot projection.

- [ ] **Step 2: Run the quota tests and verify RED**

Run: `dotnet test tests/CodexQuotaTaskbar.Core.Tests/CodexQuotaTaskbar.Core.Tests.csproj -c Release --filter QuotaCapsuleProjection`

Expected: FAIL because the quota types do not exist.

- [ ] **Step 3: Implement immutable quota types and projection**

Use private constructors plus explicit factory validation instead of public positional constructors or mutable DTOs:

```csharp
public sealed record QuotaWindowSnapshot
{
    private QuotaWindowSnapshot(
        string label,
        QuotaWindowKind kind,
        double remainingPercent,
        TimeSpan duration,
        DateTimeOffset resetsAt) { /* assign validated values */ }

    public static bool TryCreate(
        string? label,
        QuotaWindowKind kind,
        double remainingPercent,
        TimeSpan duration,
        DateTimeOffset resetsAt,
        out QuotaWindowSnapshot? snapshot) { /* reject invalid input */ }
}

public enum QuotaWindowKind { Primary, Secondary }

public sealed record QuotaSnapshot
{
    private QuotaSnapshot(
        IReadOnlyList<QuotaWindowSnapshot> windows,
        QuotaFreshness freshness,
        DateTimeOffset observedAt) { /* copy to immutable storage */ }

    public static QuotaSnapshot Create(
        IEnumerable<QuotaWindowSnapshot> windows,
        QuotaFreshness freshness,
        DateTimeOffset observedAt) { /* validate and copy */ }
}

public enum QuotaColorToken { Cool, Amber, Critical, Unavailable }

public sealed record QuotaCapsuleRow(
    string Label,
    int RoundedRemainingPercent,
    QuotaColorToken Color,
    bool Available);
```

`TryCreate` rejects undefined `QuotaWindowKind` values. `QuotaCapsuleProjection.Create(QuotaSnapshot)` sorts by duration, uses `QuotaWindowKind.Primary` before `Secondary` as the equal-duration tie-breaker, keeps two rows, fills missing rows with neutral placeholders, propagates fresh/stale/unavailable presentation state, and never clamps invalid input. The projection must consume `QuotaSnapshot`; do not leave the snapshot type unused.

- [ ] **Step 4: Run focused and full Core tests**

Run: `dotnet test tests/CodexQuotaTaskbar.Core.Tests/CodexQuotaTaskbar.Core.Tests.csproj -c Release --filter QuotaCapsuleProjection`

Expected: PASS.

Run: `dotnet test tests/CodexQuotaTaskbar.Core.Tests/CodexQuotaTaskbar.Core.Tests.csproj -c Release`

Expected: all existing and new Core tests PASS.

- [ ] **Step 5: Commit only this task if commits are authorized**

```powershell
git add src/CodexQuotaTaskbar.Core/Quota tests/CodexQuotaTaskbar.Core.Tests/Quota
git diff --cached --check
git diff --cached
git commit -m "feat: add quota capsule projection"
```

## Task 3: Implement pure overlay placement and true-fullscreen classification

**Files:**
- Create: `src/CodexQuotaTaskbar.Core/Overlay/ScreenRect.cs`
- Create: `src/CodexQuotaTaskbar.Core/Overlay/TaskbarAnchor.cs`
- Create: `src/CodexQuotaTaskbar.Core/Overlay/OverlayPlacementCalculator.cs`
- Create: `src/CodexQuotaTaskbar.Core/Overlay/FullscreenClassifier.cs`
- Create: `tests/CodexQuotaTaskbar.Core.Tests/Overlay/OverlayPlacementCalculatorTests.cs`
- Create: `tests/CodexQuotaTaskbar.Core.Tests/Overlay/FullscreenClassifierTests.cs`

- [ ] **Step 1: Write table-driven failing placement tests**

Cover bottom/top/left/right taskbars, 96/144/192 DPI, negative monitor coordinates, undersized work areas, and overflow rejection. Lock the bottom-taskbar example:

```csharp
var anchor = TestAnchor.Bottom(
    monitor: new ScreenRect(0, 0, 2560, 1440),
    workArea: new ScreenRect(0, 0, 2560, 1392),
    taskbar: new ScreenRect(0, 1392, 2560, 1440),
    dpi: 144);

Assert.Equal(
    new ScreenRect(2257, 1326, 2542, 1380),
    OverlayPlacementCalculator.Place(anchor));
```

At 150% scaling this represents 190×36 DIP, 12 DIP right margin, and 8 DIP taskbar gap.

- [ ] **Step 2: Write failing fullscreen tests**

Assert that a window covering the full monitor is fullscreen, a normal maximized window covering only the work area is not, and cloaked/Desktop/Explorer/Host windows are excluded.

- [ ] **Step 3: Run the overlay Core tests and verify RED**

Run: `dotnet test tests/CodexQuotaTaskbar.Core.Tests/CodexQuotaTaskbar.Core.Tests.csproj -c Release --filter "OverlayPlacementCalculator|FullscreenClassifier"`

Expected: FAIL because the overlay types do not exist.

- [ ] **Step 4: Implement overflow-safe physical-pixel geometry**

Use checked arithmetic, reject non-positive DPI and inverted rectangles, and convert DIP with `Math.Round(value * dpi / 96d, MidpointRounding.AwayFromZero)`. Placement must never silently move a negative-coordinate monitor onto the primary monitor.

- [ ] **Step 5: Implement true-fullscreen classification**

The classifier accepts a pure snapshot:

```csharp
public sealed record ForegroundWindowSnapshot(
    ScreenRect ExtendedFrame,
    ScreenRect MonitorBounds,
    bool IsCloaked,
    ForegroundWindowKind Kind);
```

Return true only when the extended frame covers full monitor bounds within a two-pixel tolerance and `Kind` is `Application`.

- [ ] **Step 6: Run focused and full Core tests**

Run: `dotnet test tests/CodexQuotaTaskbar.Core.Tests/CodexQuotaTaskbar.Core.Tests.csproj -c Release --filter "OverlayPlacementCalculator|FullscreenClassifier"`

Expected: PASS.

Run: `dotnet test tests/CodexQuotaTaskbar.Core.Tests/CodexQuotaTaskbar.Core.Tests.csproj -c Release`

Expected: PASS.

- [ ] **Step 7: Commit only this task if commits are authorized**

```powershell
git add src/CodexQuotaTaskbar.Core/Overlay tests/CodexQuotaTaskbar.Core.Tests/Overlay
git diff --cached --check
git diff --cached
git commit -m "feat: calculate taskbar overlay placement"
```

## Task 4: Discover taskbars through read-only Win32 APIs

**Files:**
- Create: `src/CodexQuotaTaskbar.Host/Platform/NativeMethods.cs`
- Create: `src/CodexQuotaTaskbar.Host/Platform/ITaskbarWindowApi.cs`
- Create: `src/CodexQuotaTaskbar.Host/Platform/Win32TaskbarWindowApi.cs`
- Create: `src/CodexQuotaTaskbar.Host/Platform/TaskbarTopologySource.cs`
- Create: `src/CodexQuotaTaskbar.Host/Platform/FullscreenMonitor.cs`
- Create: `src/CodexQuotaTaskbar.Host/Platform/IEnvironmentEventSource.cs`
- Create: `src/CodexQuotaTaskbar.Host/Platform/SystemEnvironmentEventSource.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/Platform/TaskbarTopologySourceTests.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/Platform/FullscreenMonitorTests.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/Platform/SystemEnvironmentEventSourceTests.cs`

- [ ] **Step 1: Write failing topology tests against a fake native API**

Test one `Shell_TrayWnd`, multiple `Shell_SecondaryTrayWnd` windows, duplicate monitor rejection, visible expanded auto-hide bars, collapsed/off-screen auto-hide bars on each edge, unknown classes, invalid DPI, a disappearing HWND between calls, taskbar recreation, and movement. Assert the fake exposes no process-open or memory APIs.

Add separate `FullscreenMonitor` tests for DWM-cloaked foreground windows, normal maximized windows, true full-monitor windows, Explorer/Desktop/Host exclusions, and foreground-window disappearance. Add environment-event tests for display change, work-area change, session lock/unlock, and a bounded polling tick that detects taskbar recreation when no shell event arrives.

- [ ] **Step 2: Run the focused Host tests and verify RED**

Run: `dotnet test tests/CodexQuotaTaskbar.Host.Tests/CodexQuotaTaskbar.Host.Tests.csproj -c Release --filter "TaskbarTopologySource|FullscreenMonitor|SystemEnvironmentEventSource"`

Expected: FAIL because the platform types do not exist.

- [ ] **Step 3: Implement the minimum native seam**

`ITaskbarWindowApi` may expose only:

```csharp
IReadOnlyList<nint> EnumerateTopLevelWindows();
string? GetClassName(nint window);
bool IsVisible(nint window);
ScreenRect? GetWindowRect(nint window);
MonitorSnapshot? GetMonitor(nint window);
uint? GetDpi(nint window);
ForegroundWindowSnapshot? GetForegroundWindowSnapshot();
bool IsPerMonitorAutoHideBar(nint window, TaskbarEdge edge, ScreenRect monitorBounds);
```

Production P/Invoke may use `EnumWindows`, `GetClassNameW`, `IsWindowVisible`, `GetWindowRect`, `MonitorFromWindow`, `GetMonitorInfoW`, `GetDpiForWindow`, `GetForegroundWindow`, `DwmGetWindowAttribute`, and `SHAppBarMessage(ABM_GETAUTOHIDEBAREX)`. Do not declare `OpenProcess`, `VirtualAllocEx`, `WriteProcessMemory`, `CreateRemoteThread`, or module-enumeration APIs in Host.

- [ ] **Step 4: Implement deterministic topology snapshots**

Classify an edge from taskbar and monitor rectangles, retain the monitor device name as the per-run key, and reject ambiguous or invalid geometry. For a per-monitor auto-hide bar, treat it as expanded only when its current rectangle intersects the visible monitor edge by more than the tested collapsed-strip tolerance; a rejected or collapsed taskbar yields no visible overlay instance and must not guess.

- [ ] **Step 5: Implement fullscreen and environment observers**

`FullscreenMonitor` maps the read-only foreground snapshot into the Core classifier. `SystemEnvironmentEventSource` forwards display/work-area and session lock/unlock events onto the UI dispatcher and owns one bounded 500 ms observation timer. Every tick resamples taskbar geometry/auto-hide state and foreground/fullscreen state, then requests reconciliation even when topology identity is unchanged; display/work-area and lock events request an immediate debounced sample. Disposal unsubscribes every handler and timer. Session lock is an unconditional hide signal; unlock triggers fresh topology and foreground reads before showing.

- [ ] **Step 6: Run focused Host tests**

Run: `dotnet test tests/CodexQuotaTaskbar.Host.Tests/CodexQuotaTaskbar.Host.Tests.csproj -c Release --filter "TaskbarTopologySource|FullscreenMonitor|SystemEnvironmentEventSource"`

Expected: PASS.

- [ ] **Step 7: Commit only this task if commits are authorized**

```powershell
git add src/CodexQuotaTaskbar.Host/Platform tests/CodexQuotaTaskbar.Host.Tests/Platform
git diff --cached --check
git diff --cached
git commit -m "feat: discover taskbar overlay anchors"
```

## Task 5: Reconcile one non-activating overlay per monitor

**Files:**
- Create: `src/CodexQuotaTaskbar.Host/Overlay/IOverlayWindow.cs`
- Create: `src/CodexQuotaTaskbar.Host/Overlay/IOverlayWindowFactory.cs`
- Create: `src/CodexQuotaTaskbar.Host/Overlay/OverlayCoordinator.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/Overlay/OverlayCoordinatorTests.cs`

- [ ] **Step 1: Write failing coordinator tests**

Cover create, physical-pixel move, update, hide, explicit non-topmost demotion, re-show/topmost promotion, remove, all-taskbars/main-only, no-op identical snapshot, DPI change, taskbar recreation/movement, per-monitor auto-hide collapse/expand, fullscreen transition, session lock/unlock, native-operation failure, and disposal. Use a fake `IOverlayWindowFactory` and fake windows; assert every created instance is eventually closed exactly once.

- [ ] **Step 2: Run the coordinator tests and verify RED**

Run: `dotnet test tests/CodexQuotaTaskbar.Host.Tests/CodexQuotaTaskbar.Host.Tests.csproj -c Release --filter OverlayCoordinator`

Expected: FAIL because the coordinator does not exist.

- [ ] **Step 3: Implement a single-threaded dispatcher reconciliation loop**

The coordinator accepts immutable topology, fullscreen and session snapshots, computes physical-pixel placements through Core, and mutates window contracts only on the UI dispatcher. It consumes every sample from the single environment-event source created in Task 4, including foreground/fullscreen-only changes; do not create another polling timer or a thread per monitor. Task 5 defines only the factory interface and fakes—the concrete WPF factory is created after the window types exist in Task 6.

- [ ] **Step 4: Implement deterministic disposal**

Stop timers, unsubscribe system events, close popovers, close every overlay, clear dictionaries, and reject future reconciliation after disposal.

- [ ] **Step 5: Run focused tests**

Run: `dotnet test tests/CodexQuotaTaskbar.Host.Tests/CodexQuotaTaskbar.Host.Tests.csproj -c Release --filter OverlayCoordinator`

Expected: PASS.

- [ ] **Step 6: Commit only this task if commits are authorized**

```powershell
git add src/CodexQuotaTaskbar.Host/Overlay tests/CodexQuotaTaskbar.Host.Tests/Overlay
git diff --cached --check
git diff --cached
git commit -m "feat: coordinate per-monitor quota overlays"
```

## Task 6: Render the approved Liquid Capsule without stealing focus

**Files:**
- Create: `src/CodexQuotaTaskbar.Host/Platform/IWindowPositionApi.cs`
- Create: `src/CodexQuotaTaskbar.Host/Platform/Win32WindowPositionApi.cs`
- Create: `src/CodexQuotaTaskbar.Host/Platform/OverlayWindowInterop.cs`
- Create: `src/CodexQuotaTaskbar.Host/Platform/ISystemPreferenceSource.cs`
- Create: `src/CodexQuotaTaskbar.Host/Platform/SystemPreferenceSource.cs`
- Create: `src/CodexQuotaTaskbar.Host/UI/QuotaCapsuleWindow.xaml`
- Create: `src/CodexQuotaTaskbar.Host/UI/QuotaCapsuleWindow.xaml.cs`
- Create: `src/CodexQuotaTaskbar.Host/UI/QuotaCapsuleViewModel.cs`
- Create: `src/CodexQuotaTaskbar.Host/UI/CapsuleThemePolicy.cs`
- Create: `src/CodexQuotaTaskbar.Host/UI/QuotaPopoverWindow.xaml`
- Create: `src/CodexQuotaTaskbar.Host/UI/QuotaPopoverWindow.xaml.cs`
- Create: `src/CodexQuotaTaskbar.Host/UI/QuotaPopoverViewModel.cs`
- Create: `src/CodexQuotaTaskbar.Host/UI/OverlayWindowFactory.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/Platform/OverlayWindowInteropTests.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/UI/QuotaCapsuleViewModelTests.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/UI/CapsuleThemePolicyTests.cs`
- Create: `docs/compatibility/overlay-shell-visual-checklist.md`

- [ ] **Step 1: Write failing view-model tests**

Test accessible name, 72%/41% text, cool/amber/critical token mapping, stale/unavailable text, tooltip contents, primary-click toggle, right-click command list, and second-click close. In `OverlayWindowInteropTests`, use a fake position API to test physical rectangles, `SWP_NOACTIVATE`, topmost promotion, non-topmost demotion, hide, `WM_DPICHANGED` suggested rectangles, `WM_MOUSEACTIVATE -> MA_NOACTIVATE`, and fail-closed behavior when style or position calls fail. In `CapsuleThemePolicyTests`, cover high contrast, advanced effects disabled, reduced motion, normal preferences, preference change, and adapter read failure.

- [ ] **Step 2: Run the UI logic tests and verify RED**

Run: `dotnet test tests/CodexQuotaTaskbar.Host.Tests/CodexQuotaTaskbar.Host.Tests.csproj -c Release --filter "QuotaCapsuleViewModel|OverlayWindowInterop|CapsuleThemePolicy"`

Expected: FAIL because the view models do not exist.

- [ ] **Step 3: Implement the capsule XAML**

Lock `Width="190"`, `Height="36"`, `CornerRadius="18"`, a 25-DIP orb, and two 4-DIP clipped tracks. Use named theme resources for glass, border, text, cool, amber, critical, stale, and unavailable states. Do not embed HTML, WebView, images from URLs, or script content.

- [ ] **Step 4: Apply non-activation and PMv2 behavior after HWND creation**

Set `ShowInTaskbar="False"`, `ShowActivated="False"`, `WindowStyle="None"`, `ResizeMode="NoResize"`, and transparent background in XAML. On `SourceInitialized`, add `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`; return `MA_NOACTIVATE` from `WM_MOUSEACTIVATE`. Do not position with WPF `Window.Left`/`Top`. `IWindowPositionApi` applies Core's physical-pixel rectangle through `SetWindowPos` with `SWP_NOACTIVATE`, uses `HWND_TOPMOST` only while eligible, explicitly demotes to `HWND_NOTOPMOST` before hide/close, and handles `WM_DPICHANGED` using the system-suggested physical rectangle before requesting a coordinator refresh. Any style, position, Z-order, or visibility failure closes that instance and reports a rejected overlay result.

- [ ] **Step 5: Implement the concrete WPF window factory**

`OverlayWindowFactory` is the first concrete implementation of Task 5's interface. It creates the capsule only after all HWND services are available and returns failure without publishing a partially initialized window. `QuotaCapsuleWindow` delegates style/message/position policy to the non-WPF `OverlayWindowInterop`, so xUnit can test the physical-pixel and fail-closed behavior without constructing a WPF window on a non-STA test thread.

- [ ] **Step 6: Implement the 300-DIP popover and menu behavior**

The popover is the only window that activates after explicit primary click. It closes on outside click, `Esc`, second click, topology change, or Host shutdown. Right-click commands in phase 1 are Refresh Demo, Open Codex placeholder, Settings placeholder, Start with Windows placeholder, and Exit; unavailable actions are visibly disabled rather than silently doing nothing.

- [ ] **Step 7: Add high-contrast and reduced-motion fallbacks**

`ISystemPreferenceSource` exposes `IsHighContrast`, `AreAdvancedEffectsEnabled`, `AreAnimationsEnabled`, and `PreferencesChanged`. The production adapter reads `SystemParameters.HighContrast`, `SystemParameters.ClientAreaAnimation`, and `Windows.UI.ViewManagement.UISettings.AdvancedEffectsEnabled`; read failure falls back to opaque surfaces with animation disabled. `CapsuleThemePolicy` maps the adapter snapshot to normal-glass, opaque-high-contrast/advanced-effects-disabled, and reduced-motion modes. Disposal unsubscribes preference events.

- [ ] **Step 8: Run focused and full managed tests**

Run: `dotnet test tests/CodexQuotaTaskbar.Host.Tests/CodexQuotaTaskbar.Host.Tests.csproj -c Release --filter "QuotaCapsuleViewModel|OverlayWindowInterop|CapsuleThemePolicy"`

Expected: PASS.

Run: `dotnet test CodexQuotaTaskbar.slnx -c Release`

Expected: all managed tests PASS.

- [ ] **Step 9: Create the visual-checklist template**

Create unchecked entries for normal 72%/41%, amber, critical, stale, unavailable, high contrast, transparency disabled, 100%, 150%, and 200% DPI states. Do not claim visual success until the runnable Host exists in Task 7 and the packaged smoke runs in Task 9.

- [ ] **Step 10: Commit only this task if commits are authorized**

```powershell
git add src/CodexQuotaTaskbar.Host/Platform/IWindowPositionApi.cs src/CodexQuotaTaskbar.Host/Platform/Win32WindowPositionApi.cs src/CodexQuotaTaskbar.Host/Platform/OverlayWindowInterop.cs src/CodexQuotaTaskbar.Host/Platform/ISystemPreferenceSource.cs src/CodexQuotaTaskbar.Host/Platform/SystemPreferenceSource.cs src/CodexQuotaTaskbar.Host/UI tests/CodexQuotaTaskbar.Host.Tests/Platform/OverlayWindowInteropTests.cs tests/CodexQuotaTaskbar.Host.Tests/UI docs/compatibility/overlay-shell-visual-checklist.md
git diff --cached --check
git diff --cached
git commit -m "feat: render liquid quota overlay"
```

## Task 7: Wire demo lifetime, tray control, and clean exit

**Files:**
- Create: `src/CodexQuotaTaskbar.Host/Demo/DemoQuotaSource.cs`
- Create: `src/CodexQuotaTaskbar.Host/Tray/TrayController.cs`
- Create: `src/CodexQuotaTaskbar.Host/Lifetime/HostOptions.cs`
- Create: `src/CodexQuotaTaskbar.Host/Lifetime/HostLifetime.cs`
- Modify: `src/CodexQuotaTaskbar.Host/App.xaml.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/Lifetime/HostOptionsTests.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/Lifetime/HostLifetimeTests.cs`
- Create: `tests/CodexQuotaTaskbar.Host.Tests/Demo/DemoQuotaSourceTests.cs`

- [ ] **Step 1: Write failing option and lifetime tests**

Accept only `--demo`, `--demo-state normal|amber|critical|stale|unavailable`, and the test-only bounded `--exit-after-seconds 1..30`. `--demo-state` and `--exit-after-seconds` are valid only with `--demo`; reject unknown, duplicate, zero, negative, oversized, or unrecognized-state arguments. Verify each demo selector produces the intended projection without I/O. Verify one shutdown request disposes tray, coordinator, popover, and timers exactly once.

- [ ] **Step 2: Run lifetime tests and verify RED**

Run: `dotnet test tests/CodexQuotaTaskbar.Host.Tests/CodexQuotaTaskbar.Host.Tests.csproj -c Release --filter "HostOptions|HostLifetime|DemoQuotaSource"`

Expected: FAIL because the lifetime types do not exist.

- [ ] **Step 3: Implement deterministic demo state**

`DemoQuotaSource` takes the validated closed `DemoState` enum. `normal` returns exactly 72% for a five-hour window and 41% for a weekly window; `amber`, `critical`, `stale`, and `unavailable` return deterministic snapshots that exercise only those visual states. It performs no network, file, account, or Codex process access.

- [ ] **Step 4: Implement tray and composition root**

Create one `NotifyIcon`, attach the approved menu, start the coordinator on the WPF dispatcher, and make Exit the single shutdown authority. `Application.ShutdownMode` must be `OnExplicitShutdown` because no normal main window exists.

- [ ] **Step 5: Implement bounded smoke-test exit**

`--exit-after-seconds` schedules dispatcher shutdown and is accepted only with `--demo`. It exists for process-level test automation and must not persist in settings.

- [ ] **Step 6: Run lifetime tests and a no-window build smoke**

Run: `dotnet test tests/CodexQuotaTaskbar.Host.Tests/CodexQuotaTaskbar.Host.Tests.csproj -c Release --filter "HostOptions|HostLifetime|DemoQuotaSource"`

Expected: PASS.

Run: `dotnet build src/CodexQuotaTaskbar.Host/CodexQuotaTaskbar.Host.csproj -c Release`

Expected: PASS with zero warnings.

- [ ] **Step 7: Commit only this task if commits are authorized**

```powershell
git add src/CodexQuotaTaskbar.Host/Demo src/CodexQuotaTaskbar.Host/Tray src/CodexQuotaTaskbar.Host/Lifetime src/CodexQuotaTaskbar.Host/App.xaml.cs tests/CodexQuotaTaskbar.Host.Tests/Demo tests/CodexQuotaTaskbar.Host.Tests/Lifetime
git diff --cached --check
git diff --cached
git commit -m "feat: run quota overlay demo"
```

## Task 8: Prove the formal Host artifact contains no injection path

**Files:**
- Create: `tests/security/host-boundary-policy.ps1`
- Create: `tests/security/host-boundary-policy.selftest.ps1`
- Create: `build/package-host.ps1`
- Modify: `build.ps1`
- Modify: `build/verify.ps1`

- [ ] **Step 1: Write the Host-scoped boundary-policy self-test first**

Construct bounded temporary source, project, publish and ZIP fixtures. Prove the policy rejects:

- `CodexQuotaTaskbar.Bridge.dll`;
- `CodexQuotaTaskbar.CompatibilityProbe.exe`;
- `CodexQuotaTaskbar.BridgeControl.dll`;
- payload names or binary strings for `WriteProcessMemory`, `CreateRemoteThread`, `VirtualAllocEx`, and `CQTB_StartProbe`;
- a Host project reference or `.deps.json` dependency on BridgeControl/probe;
- reparse points, duplicate or case-colliding ZIP entries, absolute/path-traversal entries, nested archives, and unexpected executable files.

It must accept a minimal framework-dependent Host fixture containing only `CodexQuotaTaskbar.exe`, `CodexQuotaTaskbar.dll`, `CodexQuotaTaskbar.deps.json`, `CodexQuotaTaskbar.runtimeconfig.json`, `CodexQuotaTaskbar.Core.dll`, and `release-manifest.json`. Do not add injection symbols to the repository-wide `forbidden-production-patterns.txt`; retained diagnostic source necessarily contains them.

- [ ] **Step 2: Run the scanner self-test and verify RED**

Run: `pwsh -NoProfile -NonInteractive -File tests/security/host-boundary-policy.selftest.ps1`

Expected: FAIL because the Host boundary policy does not exist.

- [ ] **Step 3: Implement the bounded Host source/dependency/artifact policy**

Scan only the Host project/source, its project-reference closure, its published `.deps.json`, and the Host artifact. Resolve every root, reject paths outside it, enumerate without following reparse points, inspect PE names/import strings and bounded text files, and return nonzero on the first violation. Do not reuse the probe package allowlist because the Host boundary is intentionally stricter.

- [ ] **Step 4: Implement Host packaging**

Publish exactly:

```powershell
dotnet publish src/CodexQuotaTaskbar.Host/CodexQuotaTaskbar.Host.csproj `
  -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false
```

The phase-1 contract is framework-dependent `win-x64`; self-contained packaging belongs to phase 3. The staging allowlist is exactly `CodexQuotaTaskbar.exe`, `CodexQuotaTaskbar.dll`, `CodexQuotaTaskbar.deps.json`, `CodexQuotaTaskbar.runtimeconfig.json`, `CodexQuotaTaskbar.Core.dll`, and the generated `release-manifest.json`. Reject any unexpected executable, DLL, PDB, archive, or subdirectory.

`build/package-host.ps1` publishes only Host, validates `.deps.json` contains the Host/Core projects and framework references but no BridgeControl/probe/native bridge, and scans source and staging through the Host policy. `release-manifest.json` has a fixed schema version and a canonical ordinally sorted list of every payload file except the manifest itself, with lowercase SHA-256 and byte length; it never claims to hash itself. Validate the manifest separately for exact allowlisted name, schema, canonical ordering, unique names, digest format, sizes, and exact payload coverage. The raw ZIP sidecar protects the manifest together with all archive bytes.

The script creates `artifacts/CodexQuotaTaskbar-overlay-demo-x64.zip` plus its ZIP sidecar. For verification: hash the raw ZIP and compare its sidecar first; inspect every central-directory entry for absolute paths, traversal, duplicates/case collisions, reparse metadata, nested archives and allowlist violations before extraction; extract into a fresh validated descendant of `artifacts`; reject post-extraction reparse points; validate the manifest schema and compare every listed payload file; then rerun the Host policy. All cleanup targets must be resolved and proven descendants of `artifacts` before removal.

- [ ] **Step 5: Add build targets without changing Probe semantics**

Extend `build.ps1 -Target` to `Verify|Build|Probe|Host`. `Host` invokes `build/package-host.ps1`; `Probe` stays unchanged. Add the Host boundary-policy self-test to `build/verify.ps1` before managed tests.

- [ ] **Step 6: Run security self-tests and formal verification**

Run: `pwsh -NoProfile -NonInteractive -File tests/security/host-boundary-policy.selftest.ps1`

Expected: PASS.

Run: `.\build.ps1 -Target Verify -Configuration Release`

Expected: sensitive boundary PASS, managed tests PASS, native diagnostics tests PASS, Host boundary-policy self-test PASS.

- [ ] **Step 7: Build and inspect the Host package**

Run: `.\build.ps1 -Target Host -Configuration Release`

Expected: framework-dependent `win-x64` package created and verified; exact allowlist and `.deps.json` closure pass; no Bridge, Probe, BridgeControl, native bridge, or injection symbol is present.

- [ ] **Step 8: Commit only this task if commits are authorized**

```powershell
# build.ps1 and verify scripts were already dirty before this plan.
# Skip this commit unless exact task hunks can be staged and reviewed separately.
git diff --cached --check
git diff --cached
git commit -m "build: package zero-injection overlay host"
```

## Task 9: Run a safe visible smoke and record phase-1 evidence

**Files:**
- Modify: `docs/compatibility/overlay-shell-visual-checklist.md`
- Modify: `docs/修改日志.md`

- [ ] **Step 1: Verify the packaged executable hash before launch**

Verify `artifacts/CodexQuotaTaskbar-overlay-demo-x64.zip` against its SHA-256 sidecar before extraction. Inspect ZIP entries using the Task 8 validation order, extract into a new validated directory under `artifacts`, compare `release-manifest.json`, and rerun `host-boundary-policy.ps1` on the extracted tree.

Expected: all hashes and boundary checks PASS.

- [ ] **Step 2: Capture a read-only Explorer baseline**

Record Explorer PID, creation time, and the basenames of loaded `CodexQuotaTaskbar*` modules. Do not open Explorer for write access.

Expected: no Host bridge module is loaded.

- [ ] **Step 3: Launch one bounded demo smoke**

Run the packaged Host with `--demo --exit-after-seconds 10`. Verify the 190×36 capsule appears above the taskbar, stays out of Alt+Tab, does not steal focus, opens the popover on click if manually exercised, and exits by itself.

Expected: process exit code 0; no Explorer restart, prompt, injection journal change, or Windhawk interruption.

Repeat bounded visual launches with `--demo-state amber`, `critical`, `stale`, and `unavailable`; use the normal state at 100%, 150%, and 200% Windows scaling, and exercise high-contrast/transparency/reduced-motion states only through reversible user-visible system settings or the non-production visual harness. Do not mutate system accessibility settings programmatically.

- [ ] **Step 4: Compare the Explorer baseline after exit**

Expected: same Explorer identity and no newly loaded `CodexQuotaTaskbar*` module.

- [ ] **Step 5: Run final verification from the current tree**

Run: `.\build.ps1 -Target Verify -Configuration Release`

Expected: all verification stages PASS.

Run: `git diff --check`

Expected: no whitespace errors.

- [ ] **Step 6: Record evidence in the visual checklist and change log**

Complete the previously unchecked visual entries for normal 72%/41%, amber, critical, stale, unavailable, high contrast, transparency disabled, 100%, 150%, and 200% DPI states. Record exact commands, exit codes, test counts, package hash, inspected Explorer identity category (not private paths), and any visual limitations. Follow the `notion-change-log` skill when updating `docs/修改日志.md`.

- [ ] **Step 7: Commit only this task if commits are authorized**

```powershell
# docs/修改日志.md was already dirty before this plan.
# Skip this commit unless the task hunks can be staged and reviewed separately.
git diff --cached --check
git diff --cached
git commit -m "docs: verify quota overlay shell"
```

## Completion gate

Phase 1 is complete only when all of the following are true:

- the Host runs from the existing solution and displays mock 72%/41% data;
- placement tests cover four edges, negative coordinates, and 96/144/192 DPI;
- normal maximized windows do not hide the capsule, true fullscreen windows do;
- the capsule does not activate or enter Alt+Tab;
- explicit click opens the 300-DIP popover;
- shutdown closes every Host-owned window and tray resource;
- the formal Host package contains no BridgeControl, compatibility probe, native bridge, or injection symbols;
- one bounded visible smoke exits cleanly while Windhawk remains enabled;
- Explorer identity and loaded-module baseline are unchanged by the Host smoke;
- full repository verification and `git diff --check` pass.
