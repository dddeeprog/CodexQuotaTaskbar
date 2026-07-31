# Taskbar Probe Authorization Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every cleanup and diagnostics callback query begin with a linearizable, retained production authorization lease so stale XamlRoot/session state cannot be queried or mutated.

**Architecture:** Add small shared safety helpers in `probe_safety`: a revocable RAII lease gate, an opaque one-shot cleanup capability/workflow, and a diagnostics session gate retaining the exact query adapter. `XamlTaskbarProbe` supplies primitive COM/XAML identity and ancestry evidence; `ProbeCapsuleManager` routes timer, explicit, and shutdown cleanup through the same workflow. Tests fake only primitive evidence/query adapters and call these production helpers.

**Tech Stack:** C++20, C++/WinRT, Win32/XAML diagnostics, CMake/MSBuild Release `/W4 /WX`, native executable tests.

---

### Task 1: Revocable diagnostics session lease

**Files:**
- Modify: `src/native/taskbar-bridge/probe_safety.h`
- Modify: `src/native/taskbar-bridge/probe_safety.cpp`
- Test: `src/native/tests/probe_safety_tests.cpp`

- [x] **Step 1: Write invalidation-before-Add and race RED tests**

Add a primitive `FakeDiagnosticsQueryAdapter` counting object resolution, reverse-handle resolution, and XAML inspection. Tests use the desired `DiagnosticsSessionGate` API to prove:

```cpp
gate.install({epoch, site_identity, diagnostics_identity}, adapter);
EXPECT(gate.close() == SessionCloseResult::drained);
EXPECT(!gate.try_acquire().has_value());
EXPECT(adapter->total_queries() == 0);
```

Add barrier cases for acquisition-wins, close-wins, and reentrant close. Acquisition-wins must return `in_flight`, retain the adapter, reject new leases, and become drained only after the first lease releases. Reentrant close must return without blocking.

- [x] **Step 2: Run RED**

Run:

```powershell
$cmake = & .\build\resolve-cmake.ps1
& $cmake --build artifacts\native-verify --target probe_safety_tests --config Release
```

Expected: compile failure because `DiagnosticsSessionGate`, its lease, adapter, and typed close result do not exist.

- [x] **Step 3: Implement the minimal shared gate**

Add:

```cpp
struct DiagnosticsSessionIdentity { uint64_t epoch; uintptr_t site; uintptr_t diagnostics; };
class DiagnosticsQueryAdapter { /* opaque get-object/get-handle methods */ };
enum class SessionCloseResult { drained, in_flight };
class DiagnosticsSessionGate { class Lease; /* install/acquire/close/reset-if-drained */ };
```

The gate owns identity plus `shared_ptr<DiagnosticsQueryAdapter>` under one mutex. `try_acquire` compares installed nonzero identity without querying. Lease destruction decrements the in-flight count. `close` only closes admission and reports state; it never waits.

- [x] **Step 4: Run GREEN**

Build and run `probe_safety_tests.exe`; all tests must pass.

### Task 2: Opaque cleanup authorization and workflow

**Files:**
- Modify: `src/native/taskbar-bridge/probe_safety.h`
- Modify: `src/native/taskbar-bridge/probe_safety.cpp`
- Test: `src/native/tests/probe_safety_tests.cpp`

- [x] **Step 1: Write trigger, ancestry, replay, and mint/consume-race RED tests**

Define wished-for APIs for:

```cpp
enum class CapsuleCleanupTrigger { timer, explicit_removal, shutdown, rollback };
struct CleanupAuthorizationBinding;   // immutable record/host/root/session/path
struct CleanupAuthorizationEvidence;  // freshly resolved primitive evidence/path
class CleanupAuthorizationEvidenceSource;
class CleanupAuthorization;           // opaque, move-only, no public constructor
class CapsuleCleanupWorkflow;          // owns issuer/gate + transaction
class CapsuleCleanupRecord;            // production trigger entry seam
```

For each required trigger, feed evidence where anchor/Grid identities and local parent relationship are unchanged but their XamlRoot/Content/path differ. Expect retryable failure, incomplete workflow, zero cleanup parent/child queries, and zero mutations. Add exact-evidence success; wrong record/trigger/host/root/session/path failure; one-shot replay failure; and barriers where invalidation wins before mint (zero cleanup calls) or acquisition wins after mint (close reports `in_flight` until the final cleanup access returns).

- [x] **Step 2: Run RED**

Expected: compile failure for the missing production workflow/capability APIs.

- [x] **Step 3: Implement minimal capability and workflow**

Use fixed-size bounded identity paths (maximum 512) with explicit complete/overflow/cycle failure. The workflow owns a unique issuer identity and revocable authority gate. Issuance requires exact equality of record key, trigger, owner thread, host/root/session generations, anchor/Grid/XamlRoot/Content identities, and the complete recorded path. A valid move-only capability owns the authority lease and attempt nonce. `CapsuleCleanupTransaction` consumes it before calling `CapsuleCleanupAccess`; invalid, moved-from, wrong-trigger, wrong-workflow, or replayed capabilities return retryable failure with zero port access.

- [x] **Step 4: Run GREEN**

Build and run the focused native test. Preserve all prior reparented-capsule, missing-child, detached/null, and every-mutating-step retry tests.

### Task 3: Wire all production cleanup triggers

**Files:**
- Modify: `src/native/taskbar-bridge/probe_capsule.h`
- Modify: `src/native/taskbar-bridge/probe_capsule.cpp`
- Modify: `src/native/taskbar-bridge/xaml_taskbar_probe.cpp`
- Test: `src/native/tests/probe_safety_tests.cpp`

- [x] **Step 1: Add production entry-point and lifecycle RED assertions**

Define the desired `CapsuleCleanupRecord` seam used by production records, with `on_timer`, `on_explicit_removal`, `on_shutdown`, and `on_rollback` methods. For every method, retain the same anchor/Grid objects and recorded ancestry, feed stale-root evidence, and assert authorization evidence runs first while the cleanup port remains untouched. Add lifecycle RED coverage showing each retryable entry result retains a nonzero remaining count and prevents quiesced signaling, handle close, and unload.

- [x] **Step 2: Run RED**

Expected: compile failure because `CapsuleCleanupRecord` entry methods do not exist; existing lifecycle fake cannot represent authorization-retained cleanup.

- [x] **Step 3: Replace the weak validator**

At insertion, retain the exact anchor and Grid objects plus a `CleanupAuthorizationBinding` containing the current `HostToken` and complete anchor-to-Content identity path. Store a production evidence-provider callback, not a boolean validator. Before any cleanup transaction, the provider runs on the owner thread, acquires the bound root authority lease, re-resolves anchor/Grid XamlRoot, Content, exact identities, and bounded ancestry, then returns primitive evidence. Make each manager record own the same production `CapsuleCleanupRecord`; route timer, `remove_anchor_on_current_thread`, `cleanup_current_thread`, and insertion-failure rollback through its corresponding entry method. Close each root authority gate whenever its binding/session/host is invalidated. Keep failed records and timers retained.

Retain strong references to the XamlRoot, Content, capsule, and every intermediate ancestry node for the record lifetime. Acquire a dedicated mutation lease before final binding validation and keep it through XAML mutation plus record/timer registration, propagating acquisition-winning insertion as `in_flight` to root invalidation.

- [x] **Step 4: Run focused GREEN**

Build and run `probe_safety_tests` plus `bridge_lifecycle_tests`.

### Task 4: Wire diagnostics lease before every callback query

**Files:**
- Modify: `src/native/taskbar-bridge/xaml_taskbar_probe.cpp`
- Test: `src/native/tests/probe_safety_tests.cpp`
- Test: `src/native/tests/bridge_lifecycle_tests.cpp`

- [x] **Step 1: Add production pipeline/order and lifecycle RED**

Define the desired `DiagnosticsCallbackPipeline` seam used directly by the private callback. Its query adapter owns the complete Add-path diagnostics and XAML projection/inspection work. With a primitive counting adapter, invalidate immediately before Add and assert object-resolution, reverse-handle, and XAML query counts remain zero. Use barriers to prove close-wins and acquisition-wins ordering and that replacement callback/state update occurs only after drained. Add lifecycle RED coverage showing an in-flight diagnostics lease prevents quiesced signaling, handle close, and unload.

Add deterministic production transition REDs before changing orchestration:

- idle capsule cleanup failure keeps the old site/session/epoch/root registry/bound roots and prepared target, performs zero target advice, and publishes only after an exact-target retry reaches zero capsule and host counts;
- target advice fails once, leaves `watcher_needed`, then an exact retry makes a second advice call and admits the current watcher;
- a forced post-advice publication failure retains `advised_not_published`, unadvises that target exactly once on retry, and never double-advises;
- a blocked target advice excludes `release_for_unload` before any gate reset, while synchronous Advise/Unadvise reentry returns busy without mutation;
- final site/service/watcher release observes the diagnostics mutex unlocked, and failed replacement `GetSite` still returns the retained old site;
- cleanup authority in `cleanup_only` rejects mutation and normal cleanup, accepts only an opaque exact gate/token transition permit, and cannot revive a permanently revoked root.

- [x] **Step 2: Run RED**

Expected: compile failure because the production `DiagnosticsCallbackPipeline` does not exist and callback/lifecycle code still has no lease-aware result.

- [x] **Step 3: Add the production adapter and unified session transition**

Wrap the exact `IXamlDiagnostics` pointer and all existing Add-path XAML inspection in the production `DiagnosticsCallbackPipeline` adapter. Install it with the exact epoch/site/diagnostics identities. The private callback delegates before any diagnostics or XAML projection; the pipeline acquires `DiagnosticsSessionGate::Lease` and keeps it alive through the adapter's final Add-path query. `SetSite` and unadvice close all relevant admissions first. On `in_flight`, retain old identities, roots, pointers, and adapters and return retryable/busy; invalidate, reset, or replace only after a later `drained` result. Do not synchronously wait, including reentrant calls.

Replace pointer/boolean lifecycle inference with explicit states: `empty`, `initial_site`, `current_unwatched`, `current`, `closing`, `prepared`, `watcher_needed`, `advise_in_progress`, `advised_not_published`, `quiesced`, `unloading`, and `unsafe_retained`. Store complete old and target `SessionResources` in a tokenized resumable transition. Every mutator claims that transition before closing a gate. Move resource owners under the mutex, but make all COM calls, host dispatch, cleanup-port work, and final owner destruction outside it. Make `GetSite` snapshot the stable resource owner and query outside the lock; during a failed/busy replacement it returns `transition.old`.

Split root authority into forward, cleanup-only, closed, and permanently revoked modes. Transition start closes both cleanup and mutation admission. After all existing leases drain, an opaque exact gate/token permit authorizes only the production old-epoch cleanup path; mutation and normal cleanup remain closed. Clean every capsule record to zero, revoke the cleanup lane, retire roots on their owner threads, and clear the root registry, `bound_roots`, and `hosts` before target advice or publication. Failure persists `unsafe_retained` with the complete old epoch and prepared target for exact retry.

Bind each watcher to the exact installed session identity. Automatically advise a replacement watcher while callback/session admission remains closed. Advise failure persists `watcher_needed`; exact retry must advise again. If advice succeeded but installation/publication fails, persist `advised_not_published` and unadvise it before retry. Publish and reopen only a complete target; never return replacement success without its watcher. Unadvise transitions to `quiesced` with cleanup-only permits, and unload claims `unloading` before any gate change. Add a detach operation that moves the diagnostics adapter out of its gate so its final destruction occurs after both mutexes are unlocked.

- [x] **Step 4: Run focused GREEN**

Build and run all three native test executables.

### Task 5: Documentation and final evidence

**Files:**
- Modify: `docs/修改日志.md`
- Verify: all modified Task 4 files

- [x] **Step 1: Run full verification**

Run `build/Verify.ps1`; require sensitive-boundary PASS, managed 86/86, native 3/3, and Release `/W4 /WX`.

- [x] **Step 2: Run repeat and PE evidence**

Run all three native executables for 30 rounds (90 executions). Assert x64, High Entropy VA, ASLR, NX, CFG, exactly four expected exports, and exactly fourteen allowlisted imports.

- [x] **Step 3: Regenerate the changelog entry**

Use `notion-change-log` extraction over every file modified in this review, including `docs/修改日志.md` itself. Sort/deduplicate ranges and update the latest single entry accurately. State explicitly that no Explorer injection or live probe ran.

- [x] **Step 4: Review, amend, and prove clean**

Request final code review, fix all Critical/Important issues, rerun evidence after the last edit, amend `feat: add minimal taskbar structure probe`, and require empty `git status --porcelain`.
