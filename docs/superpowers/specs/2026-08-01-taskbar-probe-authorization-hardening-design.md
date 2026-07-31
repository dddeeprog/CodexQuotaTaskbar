# Taskbar Probe Authorization Hardening Design

## Scope

This addendum closes two Task 4 authorization-order gaps without changing the probe surface or starting Task 5. It preserves the existing XamlRoot/Content identity checks, cleanup retry semantics, `UnsafeRetained` behavior, and zero-live-injection rule.

## Cleanup authorization

Insertion records an immutable cleanup binding containing a unique record key, the owner thread, exact host/XamlRoot/Content identities and generations, exact anchor and Grid COM identities, and the complete anchor-to-content ancestry identity chain observed before mutation. The record also strongly retains the XamlRoot, Content, anchor, Grid, capsule, and every bounded intermediate `DependencyObject` in that chain, preventing COM-address reuse from turning a changed ancestry into an apparent identity match.

Before final binding validation or any XAML insertion mutation, insertion acquires a dedicated lease from the same root authority and holds it through record and timer registration. A close-winning invalidation therefore performs zero insertion mutation; an acquisition-winning insertion is reported as `in_flight` and finishes as an operation linearized before invalidation.

Every cleanup trigger (`timer`, `explicit_removal`, and `shutdown`) plus the existing insertion-failure `rollback` path enters the same production `CapsuleCleanupRecord` orchestration seam and its `CapsuleCleanupWorkflow`. Before the workflow calls `CapsuleCleanupTransaction` or its cleanup object-access port, a separate authorization-evidence provider re-resolves on the owner thread:

- the current host, diagnostics epoch, root generation, XamlRoot, and Content;
- the exact retained anchor and Grid identities;
- both objects' live XamlRoot and Content;
- the complete, bounded anchor-to-Content ancestry chain, including the exact recorded Grid in its recorded position.

The evidence provider first acquires the record's revocable authority lease and retains it while resolving evidence. The shared issuer compares current evidence with the immutable binding. Missing, changed, zero, cyclic, truncated, or ambiguous ancestry produces no authorization. The valid result is an opaque, move-only `CleanupAuthorization` bound to one authority gate, workflow, record, trigger, host generation, root generation, diagnostics epoch, and attempt nonce. It owns that lease until the transaction's final object-port access returns, has no public constructor or serialization surface, and is consumed once. Session/root invalidation closes the authority gate: close wins prevent minting, while acquisition wins keep invalidation in `in_flight` and retain the old authority until the operation finishes. This removes the mint-to-consume race and prevents replay across records, triggers, sessions, hosts, roots, or attempts.

Authorization evidence may query XAML solely to prove authority. The cleanup object-access port is not touched until a valid capability exists. Therefore moving the complete Grid/anchor/capsule subtree to another XamlRoot fails before any cleanup parent/child query or mutation. The record, timer, and truthful remaining count stay retained.

## Diagnostics callback session lease

A synchronized `DiagnosticsSessionGate` owns the installed session identity and a reference-counted diagnostics query adapter. `try_acquire` compares the identities captured during installation without performing any diagnostics or XAML query. It returns an RAII lease only while admission is open and the epoch, site identity, diagnostics identity, and adapter are all valid. The lease retains the exact adapter and session identity for the complete handle-resolution operation.

Callback Add delegates to a shared production `DiagnosticsCallbackPipeline`. The pipeline acquires this lease before invoking its query adapter, and the adapter performs the complete Add-path `GetIInspectableFromHandle`, `GetHandleFromIInspectable`, and XAML projection/inspection while the lease remains alive. The private callback performs none of those queries outside the pipeline. An invalidated session therefore performs zero diagnostics or XAML queries.

Every watcher captures its immutable diagnostics session identity, and callback acquisition must match the expected epoch, site, and diagnostics identities exactly. A stale watcher therefore cannot borrow a replacement adapter.

### Unified diagnostics-session transition

One state machine owns the site, diagnostics adapter, watcher, root epoch, and every operation that can retire or replace them. Its explicit states are `empty`, `initial_site`, `current_unwatched`, `current`, `closing`, `prepared`, `watcher_needed`, `advise_in_progress`, `advised_not_published`, `quiesced`, `unloading`, and `unsafe_retained`. Pointer nullness and historical booleans never imply lifecycle state.

All COM resources for one session live in a shared `SessionResources` owner. A `SessionTransition` contains a monotonic transition token, operation kind and resumable stage, the complete old resources, an optional prepared target, and any target watcher already advised but not published. The old resources remain retained until target watcher advice, session installation, and publication all succeed. During a replacement that returns busy or fails, `GetSite` snapshots `transition.old` and performs `QueryInterface` after unlocking; it never exposes a partially prepared target.

Every mutating entry point first claims the state and publishes its transition token under the diagnostics mutex. It then closes callback, pending-work, diagnostics-query, cleanup, and mutation admission. Competing or synchronously reentrant `SetSite`, watcher, or unload operations return a typed busy result without changing gates or resources. The transition never waits for a lease owned by the calling stack.

Root authority has separate `forward`, `cleanup_only`, `closed`, and permanently `revoked` modes. Closing a root creates an opaque cleanup permit bound to that exact authority and transition token. Once existing cleanup and mutation leases drain, only the production cleanup workflow may use that permit in `cleanup_only`; normal cleanup and every mutation acquisition remain rejected. A permanently revoked authority can never be reopened. Active replacement uses these permits to clean every idle or active old-epoch capsule record to zero before root invalidation. It then closes the cleanup lane, removes exact host properties on the owner threads, clears `bound_roots`, `hosts`, and the root registry, and only afterward prepares the new epoch. Any cleanup, host dispatch, or root retirement failure persists `unsafe_retained` with the old resources, old epoch, old root registry, bound roots, and prepared target intact; the target is not advised or published, and an exact-target retry resumes the recorded stage.

External work is a three-phase protocol. Phase 1, under the mutex, claims the transition, closes admission, and moves owners into the transition without destroying them. Phase 2, unlocked, drains leases, unadvises the old watcher, performs transition-authorized capsule/root cleanup, allocates and advises the target watcher, or releases unload resources. Phase 3, under the mutex, either publishes a complete target and reopens admission, or records an exact retry stage. A failed allocation or `AdviseVisualTreeChange` enters `watcher_needed`, so the next exact-target retry must advise again and can never return success without a watcher. If advice succeeds but session installation or final publication cannot complete, `advised_not_published` retains the advised target; retry first unadvises it before returning to `watcher_needed`, preventing duplicate advice.

Initial diagnostics bootstrap is explicit: initial `SetSite` publishes `initial_site`, activation produces `current_unwatched`, and the normal watcher call uses the same advise transition before reaching `current`. Watcher unadvice uses the same claim/close/drain/external-call protocol and finishes in `quiesced`, retaining old session resources and token-bound cleanup permits while forward mutation stays closed. Shutdown cleanup consumes those permits; root uninitialization permanently revokes them. `release_for_unload` may claim `unloading` only before it closes or resets any gate, and only after capsule/root counts are zero. Therefore it cannot race an unlocked advise into a dead session.

No external COM call or final release occurs under the diagnostics mutex. The state stores shared resource owners so mutex-protected snapshots only change C++ reference counts; `QueryInterface`, `Advise`, `Unadvise`, host dispatch, cleanup-port calls, and destruction of the last `SessionResources` owner all occur after unlocking. `DiagnosticsSessionGate` similarly detaches its adapter by moving it to a caller-owned result before its internal mutex is released, so final adapter and diagnostics destruction happens outside both locks.

## Tests

Tests exercise the shared production helpers with primitive evidence/query adapters only:

- each production `CapsuleCleanupRecord` entry point (timer, explicit removal, shutdown, and insertion rollback) rejects an otherwise locally identical Grid/anchor/capsule subtree under a different XamlRoot before cleanup-port access and keeps the workflow incomplete;
- valid exact evidence mints a one-shot capability, while wrong record, trigger, host/root/session generation, ancestry, and replay fail;
- a barrier invalidating authority after mint but before consumption proves that an acquisition-winning capability keeps invalidation `in_flight` through the final cleanup-port access, while a close-winning attempt performs zero cleanup-port calls;
- invalidating a diagnostics session immediately before the production `DiagnosticsCallbackPipeline` Add path yields zero handle/XAML queries;
- production `SetSite` tests prove diagnostics and cleanup-port acquisition winners return `ERROR_BUSY` with the old site/session retained, then install and activate the replacement after drain; a retained stale watcher performs zero replacement-adapter queries while the automatically advised replacement watcher is admitted;
- an idle old-epoch capsule cleanup failure retains the old site, epoch, root registry, roots, hosts, and prepared target, performs zero target advice, and an exact-target retry publishes only after capsule and host counts reach zero;
- a fail-once target `Advise` enters `watcher_needed`; the exact retry makes a second advice call and admits the new watcher, while a forced post-advice publication failure exercises `advised_not_published` cleanup before retry;
- blocking target advice races `release_for_unload` deterministically and proves unload returns without closing the prepared session; synchronous Advise/Unadvise reentry sees the transition and cannot mutate it;
- offline host-retirement coverage composes the production `HandoffRequest` claimed/in-flight timeout tests with a one-shot failure at the production root-retirement mutation boundary. A fully validated owner-thread dispatch requires an Explorer-owned taskbar HWND and XamlRoot, which cannot be constructed faithfully offline and is intentionally not exercised by live injection;
- a COM release observer proves the diagnostics mutex is available during every final site/service/watcher release, and `GetSite` continues to return the retained old site after busy or retryable replacement failure;
- cleanup-authority mode tests prove transition cleanup accepts only the exact gate/token permit, rejects mutation and normal cleanup in `cleanup_only`, and never revives a permanently revoked root;
- barrier tests cover diagnostics acquisition-wins and close-wins ordering: invalidation closes new admission while an existing lease retains the old adapter, replacement occurs only after that lease drains, and reentrant replacement returns `in_flight` without waiting;
- lifecycle remaining-count tests continue to prove no quiesced signal, handle close, or unload while cleanup is retained.

All verification remains offline: Release `/W4 /WX`, managed and native suites, 30 native repeat rounds, sensitive-boundary validation, and PE/import/export checks. No Explorer injection or live compatibility probe is allowed.
