using CodexQuotaTaskbar.Host.Update;

namespace CodexQuotaTaskbar.UpdateSandbox;

internal static class Scenario
{
    internal static object Plan(Boundary boundary, string name)
    {
        var root = boundary.InRoot(name);
        var target = Path.Combine(root, "fixture-target.exe");
        return new
        {
            Scenario = name, Root = root, Target = target,
            TargetKind = "fixed non-PE text payload; never Process.Start(target)",
            Files = new[] { "fixture-target.exe", "candidate.payload", "recovery/fixture-old.payload", "fixture-target.exe.update.json",
                "fixture-target.exe.update.bak", "fixture-target.exe.update.stage", "fixture-target.exe.update.ready", "fixture-target.exe.update.lock",
                "checkpoint-pending.json", "checkpoint-final.json", "process-events.json", "scenario-result.json",
                "child-1.ticket.json", "child-1.observed.json", "child-1.stop", "child-2.ticket.json", "child-2.observed.json", "child-2.stop" }
                .Concat(Package.Names.Append(Package.ManifestName).Select(file => "fixture-host/" + file))
                .Select(file => Path.Combine(root, file.Replace('/', '\\'))).ToArray(),
            AtomicWriteSidecars = "Each JSON write may use the same path plus .writing before atomic move; all paths are rechecked for links.",
            Processes = name == "recover"
                ? new[] { "1: fixture-host/" + Package.Name + ".exe --fixture-child (new payload, no-ready)", "2: same owned fixture executable (old restored payload, no nonce)" }
                : new[] { "1: fixture-host/" + Package.Name + ".exe --fixture-child (new payload, " + (name == "success" ? "ready" : "no-ready") + ")" },
            Expected = name switch { "success" => "Committed/new hash/no backup", "pending" => "Started/new hash/old backup, alive checkpoint then cooperative exit, materials retained",
                _ => "Started checkpoint; owned PID exit confirmed; same receipt re-entry produces RolledBack/old hash/no backup and old fixture restart" }
        };
    }
    internal static async Task<object> RunAsync(Boundary boundary, Package sourcePackage, string name)
    {
        var root = boundary.InRoot(name);
        if (Directory.Exists(root) || File.Exists(root)) throw new InvalidOperationException("Scenario already exists.");
        Directory.CreateDirectory(root);
        var package = sourcePackage.CopyTo(boundary, Path.Combine(root, "fixture-host"));
        var target = boundary.Check(Path.Combine(root, "fixture-target.exe"));
        var source = boundary.Check(Path.Combine(root, "candidate.payload"));
        var recovery = boundary.Check(Path.Combine(root, "recovery", "fixture-old.payload"));
        Directory.CreateDirectory(Path.GetDirectoryName(recovery)!);
        File.Copy(package.FilePath("fixture-old.payload"), recovery, false);
        File.Copy(recovery, target, false);
        File.Copy(package.FilePath("fixture-new.payload"), source, false);
        // Fresh execution never accepts pre-existing receipts/backups/stages/locks or ready markers.
        foreach (var path in new[] { UpdateTransaction.ReceiptPath(target), UpdateTransaction.BackupPath(target), UpdateTransaction.StagePath(target),
                     UpdateStartup.ReadyPath(target), target + ".update.lock" })
            if (File.Exists(boundary.Check(path))) throw new InvalidDataException("Stale transaction material.");
        var runtime = new FixtureRuntime(boundary, package, root, target, source, name != "success", name == "recover");
        var options = new UpdateApplyOptions(Environment.ProcessId, target) { Nonce = Guid.NewGuid().ToString("N") };
        var files = new SandboxFiles(boundary, target, source);
        UpdateApplyException? first = null;
        UpdateApplyException? recoveryResult = null;
        Exception? failure = null;
        Exception? stopFailure = null;
        try
        {
            runtime.VerifyMaterials();
            try { await new UpdateTransaction(runtime, files).ExecuteAsync(options, CancellationToken.None); }
            catch (UpdateApplyException error) { first = error; }
            if (name == "success")
            {
                Require(first is null, "Success transaction failed.");
                Require(Receipt(boundary, target).Phase == UpdateTransactionPhase.Committed, "Missing committed receipt.");
                Require(Files.Hash(target) == package.ExpectedHash("fixture-new.payload") && !File.Exists(UpdateTransaction.BackupPath(target)), "Commit hash/backup mismatch.");
                Require(runtime.Children.Count == 1, "Success must launch exactly one fixture.");
                await runtime.ConfirmObservationAsync(runtime.Children[0], package.ExpectedHash("fixture-new.payload"), "ready");
            }
            else
            {
                Require(first is { RecoveryOutcome: "待恢复" }, "Expected pending recovery failure.");
                var receipt = Receipt(boundary, target);
                Require(receipt.Phase == UpdateTransactionPhase.Started && receipt.ProcessId is > 0 && receipt.ProcessStartTicks is > 0, "Pending receipt is not process-bound.");
                Require(Files.Hash(target) == package.ExpectedHash("fixture-new.payload")
                    && Files.Hash(boundary.Check(UpdateTransaction.BackupPath(target))) == package.ExpectedHash("fixture-old.payload"), "Pending recovery material mismatch.");
                Require(runtime.ExitState(new(receipt.ProcessId!.Value, receipt.ProcessStartTicks!.Value)) == UpdateExitState.Alive, "Pending process was not actually alive.");
                Require(runtime.Children.Count == 1, "Pending checkpoint must have exactly one fixture.");
                await runtime.ConfirmObservationAsync(runtime.Children[0], package.ExpectedHash("fixture-new.payload"), "no-ready");
                Snapshot(boundary, runtime, root, target, "checkpoint-pending.json", first);
                // Recovery re-entry is reachable only after exact PID + start + image ownership and exit confirmation.
                await runtime.StopOwnedAsync(new(receipt.ProcessId.Value, receipt.ProcessStartTicks.Value));
                Require(runtime.TargetExitState(target) == UpdateExitState.Exited, "Cannot recover while ownership/exit is uncertain.");
                if (name == "recover")
                {
                    runtime.VerifyMaterials();
                    try { await new UpdateTransaction(runtime, files).ExecuteAsync(options, CancellationToken.None); }
                    catch (UpdateApplyException error) { recoveryResult = error; }
                    // Recovery re-entry restores the journal, but its local replaced flag stays false;
                    // the successful no-nonce restart therefore reports the production "retained" outcome.
                    Require(recoveryResult is not null && recoveryResult.RecoveryOutcome == "保留 v1.0.0，已尝试正常启动", "Missing restored-and-restarted outcome.");
                    Require(Receipt(boundary, target).Phase == UpdateTransactionPhase.RolledBack && Files.Hash(target) == package.ExpectedHash("fixture-old.payload")
                        && !File.Exists(UpdateTransaction.BackupPath(target)), "Recovery receipt/hash/backup mismatch.");
                    Require(runtime.Children.Count == 2, "Expected exactly one no-nonce old fixture restart.");
                    Require(runtime.Children[1].Nonce is null, "Restored old fixture restart must receive no nonce.");
                    await runtime.ConfirmObservationAsync(runtime.Children[1], package.ExpectedHash("fixture-old.payload"), "restored-old");
                }
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            try { await runtime.StopAllAsync(); }
            catch (Exception error) { stopFailure = error; }
            try
            {
                Require(runtime.Children.All(child => runtime.ExitState(new(child.Id, child.StartTicks)) == UpdateExitState.Exited), "An owned fixture exit remains unconfirmed.");
                if (failure is null && stopFailure is null)
                {
                    Require(runtime.Children.Count == (name == "recover" ? 2 : 1), "Unexpected total launch count.");
                    var receipt = Receipt(boundary, target);
                    Require(receipt.Target == target && receipt.Nonce == options.Nonce && receipt.OldHash == package.ExpectedHash("fixture-old.payload")
                        && receipt.NewHash == package.ExpectedHash("fixture-new.payload"), "Final receipt binding mismatch.");
                    Require(Files.Hash(recovery) == receipt.OldHash, "Separate original recovery payload changed.");
                    if (name == "pending")
                        Require(receipt.Phase == UpdateTransactionPhase.Started && Files.Hash(target) == receipt.NewHash
                            && Files.Hash(boundary.Check(UpdateTransaction.BackupPath(target))) == receipt.OldHash, "Pending material must survive cooperative process exit.");
                    else if (name == "success")
                        Require(receipt.Phase == UpdateTransactionPhase.Committed && Files.Hash(target) == receipt.NewHash
                            && !File.Exists(UpdateTransaction.BackupPath(target)), "Committed state changed after exit.");
                    else Require(receipt.Phase == UpdateTransactionPhase.RolledBack && Files.Hash(target) == receipt.OldHash
                        && !File.Exists(UpdateTransaction.BackupPath(target)), "Restored state changed after exit.");
                }
                Snapshot(boundary, runtime, root, target, "checkpoint-final.json", recoveryResult ?? first);
            }
            catch (Exception error) { failure ??= error; }
            Files.WriteJson(boundary, Path.Combine(root, "scenario-result.json"), new
            {
                Scenario = name, Passed = failure is null && stopFailure is null,
                Failure = failure?.Message, StopFailure = stopFailure?.Message,
                FirstTransaction = Detail(first), RecoveryReentry = Detail(recoveryResult), Processes = runtime.ProcessEvidence(),
                Coverage = "Production UpdateTransaction with real files and fixture subprocesses; test-only Identity/Attest/parent/ready semantics; not SystemUpdateRuntime or App"
            });
        }
        if (failure is not null || stopFailure is not null) throw new AggregateException(new[] { failure, stopFailure }.OfType<Exception>());
        return new { Scenario = name, Passed = true, Evidence = Path.Combine(root, "scenario-result.json") };
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static UpdateReceipt Receipt(Boundary boundary, string target) => Files.ReadJson<UpdateReceipt>(boundary.Check(UpdateTransaction.ReceiptPath(target)));
    private static object? Detail(UpdateApplyException? error) => error is null ? null : new { error.Failure, error.RecoveryOutcome, error.RecoveryFailure, error.CleanupFailure };
    private static void Snapshot(Boundary boundary, FixtureRuntime runtime, string root, string target, string name, UpdateApplyException? error)
    {
        var paths = new[] { target, UpdateTransaction.ReceiptPath(target), UpdateTransaction.BackupPath(target), UpdateTransaction.StagePath(target), UpdateStartup.ReadyPath(target),
            Path.Combine(root, "candidate.payload"), Path.Combine(root, "recovery", "fixture-old.payload") };
        Files.WriteJson(boundary, Path.Combine(root, name), new
        {
            Utc = DateTime.UtcNow, Receipt = Receipt(boundary, target), Error = Detail(error), Processes = runtime.ProcessEvidence(),
            FileEvidence = paths.Select(path => new { Path = boundary.Check(path), Exists = File.Exists(path), Sha256 = File.Exists(path) ? Files.Hash(path) : null }).ToArray()
        });
    }
}
