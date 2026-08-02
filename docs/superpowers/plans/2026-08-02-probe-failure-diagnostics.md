# Probe failure diagnostics implementation plan

> **For Codex:** Direct execution selected because the user explicitly requested: “修改，直到能用”.

**Goal:** Make a failed guarded live probe emit a safe, machine-readable reason instead of the unhelpful `activation: Rejected`, then use that evidence to repair the actual compatibility failure without disabling any safety gate.

**Architecture:** Preserve the existing injector failure metadata at the package-runtime boundary, expose only allow-listed enum names and numeric error codes in an optional backward-compatible lifecycle diagnostic, and keep all path, PID, SID, window and command-line data out of the report. If that first typed result is a native Ready/Quiesced failure, add an activation-bound, fixed-enum native phase diagnostic rather than guessing at XAML internals. Re-run a short explicit-retry probe only after offline validation and a confirmed clean recovery of the prior post-reboot journal state.

**Tech Stack:** .NET 10 / C#, xUnit, PowerShell build verification, native CMake bridge package.

---

### Task 1: Define the diagnostic contract and its red regression test

**Files:**
- Modify: `tests/CodexQuotaTaskbar.Core.Tests/Compatibility/ProbeModeRunnerTests.cs`
- Modify: `tools/CodexQuotaTaskbar.CompatibilityProbe/ProbeModeRunner.cs`
- Modify: `tools/CodexQuotaTaskbar.CompatibilityProbe/Reporting/CompatibilityReport.cs`

1. Add a failing test whose fake rejected runtime result contains an activation diagnostic and assert JSON has only `stage`, `code`, `nativeError`, and `remoteHResult` (including the defined null behavior).
2. Run the focused test and observe the expected red failure before production edits.
3. Introduce an immutable, allow-listed `ProbeActivationDiagnostic`; require `Enum.IsDefined`, reject a diagnostic for `Ready`, and avoid strings drawn from system state.
4. Thread it through `ProbeActivationResult`, `GuardedProbeModeRunner`, and `CompatibilityReportLifecycle`.
5. Add compatibility assertions that old lifecycle shapes remain readable because the optional field is absent for successful, unsupported, and generic boundary failures; prove exceptions/paths never serialize.
6. Re-run the focused test and verify it passes.

### Task 2: Preserve injector failure metadata at the package boundary

**Files:**
- Modify: `tools/CodexQuotaTaskbar.CompatibilityProbe/PackagedProbeRuntime.cs`
- Modify: `tests/CodexQuotaTaskbar.Core.Tests/Compatibility/ProbeModeRunnerTests.cs`

1. Add a failing test for the mapping helper using a simulated `StartProbeRejected` failure with a known HRESULT, plus a null-code case.
2. Run the focused test and observe the expected red failure.
3. Map only `InjectorFailure.Stage`, `.Code`, `.NativeError`, and `.RemoteHResult` into the new probe diagnostic.
4. Re-run the focused tests and verify report output and mapping.

### Task 3: Verify the change and obtain one actionable live result

**Files:**
- Modify if the observed typed failure identifies a code defect; add a regression test first.
- Update: `docs/compatibility/probe-runbook.md` only if the report schema or rerun procedure changes.

1. Run the full offline verification suite, security boundary checks, and package build.
2. Verify the ZIP/EXE hash and use only its fresh extracted formal package, with a fresh non-reparse JSON destination.
3. Recover the old unsafe journal after reboot using the existing detach flow; require both a successful detach result and a journal `Clean`/`Allowed` read before any retry. If that gate fails, stop rather than overriding it.
4. Run one short explicit-retry live probe, inspect the typed result, and never use Explorer restart.
5. If it reports an injector boundary defect, write the precise red regression, make the smallest safe change, then repeat offline validation and one controlled probe.
6. The previous 20-second trace already strongly indicates a Ready/Quiesced native-lifecycle failure, so add the fixed-enum phase diagnostic before the next live run: native bridge holds best-effort manual-reset phase events bound to the activation ID; the injector performs only zero-wait event reads before emergency detach. Do not use a new remote export/query, because a timeout can race bridge self-unload.
7. Cover the phase names, lifecycle ordering, Ready-timeout propagation, absent-event behavior, and report serialization with regression tests; the phase is diagnostic-only and cannot influence authorization, safety, journal state, or retry.
8. If typed evidence specifically points to third-party taskbar hooks, offer a user-controlled comparison with those hooks disabled/restored; do not treat their presence as proof or change the user's configuration automatically.
9. Stop only once the probe reaches a clean `Compatible` outcome or a user-controlled third-party compatibility constraint is proven with the typed evidence.

### Task 4: Final verification and handoff

**Files:**
- Modify: `docs/修改日志.md`

1. Run targeted and full tests plus package verification.
2. Inspect the final diff for scope and report-field privacy.
3. Update the Chinese change log with changed files and line references.
4. Report the exact result, remaining constraints (if any), and local artifacts.
