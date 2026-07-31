# Codex Quota Taskbar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build and package an independent Windows 11 x64 application that reads official Codex quota data and renders the approved Liquid Capsule inside the taskbar on the user’s Windows build 26200.

**Architecture:** Execute three independently testable plans in order. First prove that a minimal, removable taskbar element can attach safely on build 26200 and place its injector/journal logic in a BCL-only shared BridgeControl library. Then build a complete tray-only host with the official Codex stdio provider. Finally connect the proven native bridge, Liquid Capsule, host-owned popover, safety breaker, and installer. A failed compatibility probe is a hard stop before the later plans.

**Tech Stack:** .NET 10 (`net10.0-windows`, WPF + WinForms tray), C# and BCL-only provider, C++23/CMake/MSVC native bridge, Windows XAML Diagnostics/TAP, xUnit 2.9.3, PowerShell build orchestration, NSIS 3 packaging.

---

## Source Baselines

Pin every external reference; do not plan against a floating branch.

| Source | Pinned revision | Use |
|---|---|---|
| `pfcdev/TaskbarWidgets` | `517d655d54fae3974690b3f9461bbf6539c13a30` (`v0.4.2`) | MIT-licensed taskbar probe/injection, detach lifecycle, file channel, build and NSIS reference |
| `openai/codex` | `3a76f3ac68c8949d1cac6ea769b6ec7b8953a415` (`rust-v0.142.0`) | Minimum app-server wire contract and generated-schema fixtures |
| `openai/codex` | `e363b08c9175ac1cbe5893615dd2cb9ddf95043b` (`rust-v0.146.0`) | Packaging-verified maximum app-server binary/schema/stdio fixture |
| `openai/codex` | `775fb21d2af9b9936618fe22dd62e6f0cb3ba4a3` | Packaging-time current app-server documentation reference |
| `QuotaTray` | `be39c92432324307fe412a1611cc9000dcddfbda` | MIT-licensed stdio process and JSONL reference only; copy substantive code only with notice |

Create `THIRD_PARTY_NOTICES.md` and preserve the TaskbarWidgets/QuotaTray MIT notices for any imported or adapted code.

The release's exact packaging-verified Codex range is 0.142.0–0.146.0. Pin both official Windows x64 npm artifacts and their SRI values in `eng/verified-codex-versions.json`, exercise both real binaries against schema and stdio tests, and fail the release if the official stable version has moved beyond 0.146.0 until the manifest/fixtures are reviewed and rerun. Runtime capability probing may accept a newer additive-compatible version, but it must be labeled runtime-probed rather than packaging-verified.

## Deliberate Departures from TaskbarWidgets

- Do not import `AccountManager.cs`, `Migration/LegacyMigration.cs`, `Data/Accounts`, `Data/IdeProfiles`, active-account markers, account settings, or any `auth.json` path.
- Do not ship Tauri/Rust settings, community widgets/SDK, media helper, updater, `twdev`, or generic widget host.
- Replace the general loader with a focused .NET 10 WPF host that owns the tray icon, settings, detail card, app-server, safety journal, and bridge supervision.
- Extract the minimal TAP/taskbar attach and cleanup logic from the upstream 7,480-line hook into focused native files; do not copy the whole hook.
- Keep all provider, auth-state, popover, settings, notifications, and network work outside Explorer.

## Plan Set and Gates

1. [Compatibility Probe Plan](2026-08-01-codex-quota-taskbar-compatibility-probe.md)
   - Produces a signed-by-hash diagnostic ZIP and a sanitized compatibility report.
   - Hard gate: the report must say `Compatible` on Windows `10.0.26200` and prove attach, visible layout, detach, Explorer restart, and every selected monitor.
   - If the report is not `Compatible`, stop and return to user design review. Do not claim tray fallback completes the task.
2. [Provider and Tray Host Plan](2026-08-01-codex-quota-taskbar-provider-host.md)
   - Produces a working tray-only Codex quota app using official stdio, with the host-owned popover and no Explorer injection.
   - May begin only after the compatibility gate passes.
3. [Taskbar UI and Release Plan](2026-08-01-codex-quota-taskbar-ui-release.md)
   - Reuses the probe’s proven native boundary, inserts the Liquid Capsule, wires commands and safety controls, then produces installer and portable artifacts.

Each plan is separately reviewable, testable, and commit-oriented. Do not collapse their gates into one large implementation batch.

## Intended Repository Layout

```text
CodexQuotaTaskbar.slnx
Directory.Build.props
global.json
VERSION
THIRD_PARTY_NOTICES.md
build.ps1
build/
  build-native.ps1
  build-product.ps1
  build-nsis.ps1
  verify.ps1
  verify-sensitive-boundary.ps1
  create-release.ps1
docs/
  compatibility/
  privacy.md
  troubleshooting.md
installer/nsis/CodexQuotaTaskbar.nsi
src/
  CodexQuotaTaskbar.BridgeControl/
  CodexQuotaTaskbar.Core/
  CodexQuotaTaskbar.Host/
  native/
    CMakeLists.txt
    common/
    taskbar-bridge/
    tests/
tests/
  CodexQuotaTaskbar.BridgeControl.Tests/
  CodexQuotaTaskbar.Core.Tests/
  CodexQuotaTaskbar.Host.Tests/
  CodexQuotaTaskbar.Provider.IntegrationTests/
  CodexQuotaTaskbar.FakeAppServer/
tools/
  CodexQuotaTaskbar.CompatibilityProbe/
  CodexQuotaTaskbar.FileAccessAudit/
```

`CodexQuotaTaskbar.FileAccessAudit` is a non-packable security-test harness and the only exact source-path exemption to the forbidden-string scan. The scanner still covers all published artifacts and fails if that harness or its credential sentinel is ever packaged.

## Local Toolchain Contract

The current machine has Windows 11 `10.0.26200` (25H2), Codex CLI `0.142.0`, .NET SDK `10.0.302`, Visual Studio 2022 Enterprise `17.14.21`, MSVC `14.44.35207`, Windows SDK `10.0.26100.0`, VS-bundled CMake `3.31.6`, and Node `24.11.1`. It does not currently have NSIS in PATH; the release plan pins the current winget package `NSIS.NSIS` version `3.12` (installer SHA-256 `3bc2b06253a7e4957111be152ac6a536e0c7478a706e19da814038db5d706495`).

- Pin .NET with `global.json` to `10.0.302` and `rollForward: latestPatch`.
- Resolve CMake from PATH first, then the Visual Studio installation; do not require global PATH mutation.
- Install pinned NSIS 3.12 only in the release-plan packaging task.
- Rust, Cargo, WebView2, npm build dependencies, and a .NET workload are not required by the selected architecture. Existing Node/npm is used only to fetch the two exact, SRI-pinned official Codex compatibility-test tarballs; those packages are never product/runtime dependencies or shipped artifacts.

## Cross-Plan Verification Command

Every commit that changes production code must leave this command green:

```powershell
.\build.ps1 -Target Verify -Configuration Release
```

Expected final result:

```text
Sensitive boundary: PASS
.NET unit tests: PASS
Provider integration tests: PASS
Native tests: PASS
Build: PASS
```

Live Explorer tests, accessibility checks, installer smoke tests, and the 30-minute soak remain explicit gated tasks; they are not hidden inside the fast verification command.

## Final Artifact Contract

The final release task must produce:

```text
artifacts/CodexQuotaTaskbarSetup-x64.exe
artifacts/CodexQuotaTaskbar-portable-x64.zip
artifacts/CodexQuotaTaskbar-compatibility-26200.json
artifacts/CodexQuotaTaskbarSetup-x64.exe.sha256
artifacts/CodexQuotaTaskbar-portable-x64.zip.sha256
artifacts/release-manifest.json
```

No artifact may contain `AccountManager`, `Data/Accounts`, `Data/IdeProfiles`, `active-codex-account.txt`, TaskbarStats migration code, or a private ChatGPT/WHAM endpoint. The literal `auth.json` is also forbidden everywhere except the exact normalized packaged Markdown entry `docs/privacy.md`, where only that one disclosure token is permitted by `allowed-documentation-paths.txt`; binaries and every other entry remain unconditionally forbidden.
