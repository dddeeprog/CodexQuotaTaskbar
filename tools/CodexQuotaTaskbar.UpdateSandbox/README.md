# Update transaction fixture sandbox

**Preparation only on 2026-10-30. No driver invocation (including dry-run), fixture process, new test host, official helper or product App has been executed for this tool. Build success is not behavior acceptance.**

This independent tool exists because CompatibilityProbe checks Shell/module compatibility and DotBridgeProbe checks a browser bridge. Neither is an update transaction harness. This tool is outside the solution and default `build.ps1`/`build/verify.ps1`; no product source or production trust rule changes. It links the existing production `UpdateTransaction`, `UpdateStartup`, options/diagnostics and identity/parser sources directly, without copying the update algorithm.

Its `fixture-target.exe` is **non-PE text**, never an executable launch target. A test-only runtime maps the two pinned payload SHA256 values to versions 1.0.0 and 2.0.0 and performs a local package/recovery-material check. Only a sandbox copy of this tool's own apphost runs as `--fixture-child`; that child reads the payload and can publish the existing nonce/receipt-bound ready file. That ready signal means **fixture readiness**, not a production App mutex or usable UI.

The fixture package manifest is a local integrity pin, **not official package attestation**. Production `UpdatePackageIdentity.Read`, official network self-attestation, `SystemUpdateRuntime`, the real helper/target, App mutex/settings/UI-ready and physical environment are not behavior-tested here. The production identity source is linked unchanged for its record type and review; the test-only runtime explicitly uses its own non-PE mapping. Official package offline validation is separate existing root evidence. This tool does not open or launch that official ZIP's EXE.

## Build preparation

From PowerShell 7, run `build/update-sandbox.ps1` in the repository when building is desired. It restores only against the local tool directory, builds with `--no-restore`, then writes `sandbox-package.json` next to the apphost. There are no NuGet dependencies, new runtime installs, network calls, driver execution or test launches. The project can also be built directly:

```powershell
dotnet restore .\tools\CodexQuotaTaskbar.UpdateSandbox\CodexQuotaTaskbar.UpdateSandbox.csproj --source .\tools\CodexQuotaTaskbar.UpdateSandbox --ignore-failed-sources
dotnet build .\tools\CodexQuotaTaskbar.UpdateSandbox\CodexQuotaTaskbar.UpdateSandbox.csproj -c Release --no-restore
```

The build script prepares the manifest; direct build alone does not. Rebuilds can change package hashes, so obtain the manifest SHA256 after the final build. Invoke the `.exe` apphost directly; invocation via `dotnet tool.dll` is refused because it would not establish the fixture image identity.

## Future dry-run and execution

**The following commands are for a later explicitly authorized run. None was executed during preparation.** These variable assignments create no directory; a driver dry-run prints the plan to stdout and never calls the transaction, writes a file or starts a child.

```powershell
$package = 'C:\Users\TomatoK\.codex\worktrees\reliability\CodexQuotaTaskbar\tools\CodexQuotaTaskbar.UpdateSandbox\bin\Release\net10.0'
$manifestSha256 = (Get-FileHash -LiteralPath (Join-Path $package 'sandbox-package.json') -Algorithm SHA256).Hash
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('CodexQuotaTaskbar.UpdateSandbox\runs\' + [Guid]::NewGuid().ToString('N'))
& (Join-Path $package 'CodexQuotaTaskbar.UpdateSandbox.exe') --sandbox-root $sandbox --package-root $package --package-sha256 $manifestSha256 --scenario all
```

Only a later explicit `--execute` permits sandbox writes and fixture starts:

```powershell
& (Join-Path $package 'CodexQuotaTaskbar.UpdateSandbox.exe') --sandbox-root $sandbox --package-root $package --package-sha256 $manifestSha256 --scenario all --execute
```

`--scenario success|pending|recover` narrows the run. Every driver invocation requires an absent 32-hex GUID root immediately below `%TEMP%\CodexQuotaTaskbar.UpdateSandbox\runs`. A prior dry-run does not create it. A prior execution must never be reused as a fresh run; choose a new GUID. There is deliberately **no general external recovery command**: `recover` first creates its own pending state in a fresh run, confirms that run's exact owned child exited, and only then re-enters the original transaction with its existing receipt and backup. Do not manually point this tool at an old run or a production install.

The default plan lists absolute paths, the maximum child roles and expected outcomes. `plan.json` is saved before the first child on execution; `process-events.json` records exact apphost/argv/hash before each launch and actual PID/start/image after. Child tickets, observations and stop files are distinct per launch. All paths reject UNC/device/ADS/traversal, ambiguous suffixes and existing reparse points on every access. Explicit exclusions cover the main repository, worktrees, historic project subtree, visualization subtree, production settings and `%LOCALAPPDATA%\Programs\CodexQuotaTaskbar`, Program Files and Windows. The only executable child is an exact hash match for the running tool's apphost and assembly with matching runtime metadata, copied into that run.

These are path-based local safeguards against accidental escape; they are not a security boundary against a hostile concurrent process changing the filesystem between checks and operations. The tool does not alter ACLs, permissions, protection, accounts, product settings, official installation, network state, releases or existing perf/fixtures. The test assumes the user's local .NET 10 installation already exists.

## Prepared scenarios and receipts

| Scenario | Prepared behavior | Evidence expected on a future run |
| --- | --- | --- |
| `success` | Real `File.Replace`; real fixture child publishes nonce-bound ready; original transaction commits; child is cooperatively stopped | `Committed`, new target SHA, removed transaction backup, original recovery payload retained, bound child PID/start/image, confirmed exit |
| `pending` | Real child intentionally never publishes ready; test fault withholds the first stop request so original transaction sees it alive and refuses rollback | `checkpoint-pending.json`: `Started`, new target SHA, old backup SHA, actual live PID/start/image; then owned cooperative exit. The pending receipt and old backup remain untouched |
| `recover` | Same pending checkpoint, then exact owned PID exit confirmation, then original transaction re-entry; original transaction restores old payload and tries its no-nonce restart through the same fenced fixture runtime | `RolledBack`, old target SHA, original recovery payload, backup consumed by restore, second owned fixture PID/start/image, confirmed exits |

All writes and `File.Replace` calls go through sandbox file gates, including lock, receipt, ready, backup, stage and atomic `.writing` sidecars. Every candidate launch and no-nonce old restart uses the same runtime gate. Separate original recovery bytes and candidate/package hashes are checked before attestation, launch and recovery re-entry. A mismatch refuses progress and leaves materials for inspection.

`checkpoint-pending.json` and `checkpoint-final.json` capture receipt objects plus actual file SHA256 and process observations; `scenario-result.json` and root `summary.json` distinguish expected transaction failure from a harness failure. A pending checkpoint is captured while the child is actually alive. Final pending evidence deliberately has an exited child with the same unrecovered receipt and backup. Exit code 0 means selected scenario assertions and owned child exits passed; 1 means a scenario failed; 2 means argument/preflight/outer failure. A harness failure never warrants claiming a scenario passed.

The tool never stops by process name and never force-kills. It signals only its recorded child with matching PID, creation ticks and exact image path/hash using a per-child stop file. Uncertain ownership refuses signaling/recovery. Each fixture child has a maximum 30-second lease and can exit on its own if the driver dies. An unconfirmed exit is an error; preserve the run and inspect its evidence. Processes with unrelated names or identities are never touched.

## Retention and current limits

The driver never recursively deletes any run. Successful, pending and restored trees, original payloads, package manifests and evidence remain for review. After a future run, remove only its explicitly identified GUID directory after all recorded child exits are confirmed and no recovery or review use remains. Build outputs are reproducible but retained with the implementation for review. No old perf/fixtures or previous evidence are cleaned here.

No behavioral/negative/RED/GREEN test was run during preparation. Future reviewers should exercise argument/path/reparse/hash/stale-state rejection and the three scenarios only after authorization. Static inspection and successful compilation alone cannot establish the runtime gates, Windows file replacement, child lifecycle or recovery behavior.
