using System.Diagnostics;
using System.Text.Json;
using CodexQuotaTaskbar.Host.Update;

namespace CodexQuotaTaskbar.UpdateSandbox;

internal sealed record ChildTicket(string Target, string Nonce, string Mode, string StopFile, string ObservationFile, string ImagePath, string ImageHash, string PayloadHash, long ExpiresUtcTicks);
internal sealed record OwnedChild(int Id, long StartTicks, string ImagePath, string Target, string? Nonce, string StopFile, string ObservationFile);
internal sealed record ChildObservation(string Kind, int Id, long StartTicks, string ImagePath, string ImageHash, string TargetPayload, string PayloadHash, string Nonce, string Mode, DateTime Utc);

// This runtime is test-only. Identity is an explicit non-PE mapping and Attest never calls production trust code.
internal sealed class FixtureRuntime(Boundary boundary, Package package, string scenarioRoot, string target, string source, bool withholdFirstStop, bool allowOldRestart) : IUpdateRuntime
{
    private readonly List<OwnedChild> owned = [];
    private readonly List<object> events = [];
    internal IReadOnlyList<OwnedChild> Children => owned;
    public string Source => source;
    public UpdatePackageIdentity Identity(string path)
    {
        boundary.Check(path);
        if (path != target && path != source && path != UpdateTransaction.StagePath(target) && path != UpdateTransaction.BackupPath(target))
            throw new InvalidOperationException("Identity path not owned by this scenario.");
        return package.FixtureIdentity(path);
    }
    public Task AttestAsync(string candidate, UpdatePackageIdentity identity, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        VerifyMaterials();
        if (candidate != source || identity != package.FixtureIdentity(source) || identity.Version != new Version(2, 0, 0)) throw new InvalidDataException();
        Record("fixture-local-attest-only", new { identity.Sha256, Version = identity.Version.ToString() });
        return Task.CompletedTask;
    }
    internal void VerifyMaterials()
    {
        boundary.CheckRoot(requireFresh: false);
        _ = Package.Load(package.Root, package.ManifestHash);
        foreach (var file in new[] { target, source, UpdateTransaction.StagePath(target), UpdateTransaction.BackupPath(target),
                     UpdateTransaction.ReceiptPath(target), target + ".update.lock", UpdateStartup.ReadyPath(target) }) boundary.Check(file);
        var recovery = boundary.Check(Path.Combine(scenarioRoot, "recovery", "fixture-old.payload"));
        if (Files.Hash(recovery) != package.ExpectedHash("fixture-old.payload") || Files.Hash(source) != package.ExpectedHash("fixture-new.payload"))
            throw new InvalidDataException("Pinned candidate or separate recovery material changed.");
    }
    public Task WaitForParentAsync(UpdateApplyOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (options.TargetExecutable != target || options.ParentProcessId != Environment.ProcessId) throw new InvalidOperationException();
        // No production parent exists: the driver never opens the non-PE target as an executable.
        Record("fixture-parent-absent", new { options.ParentProcessId });
        return Task.CompletedTask;
    }
    public UpdateProcess? Start(string path, string? nonce)
    {
        // Both candidate Start and the transaction's no-nonce recovery Start use this one gate.
        VerifyMaterials();
        if (path != target || TargetExitState(target) != UpdateExitState.Exited || owned.Count >= (allowOldRestart ? 2 : 1)
            || (owned.Count == 0 && nonce is null) || (owned.Count == 1 && nonce is not null)) throw new InvalidOperationException("Unowned or extra fixture launch.");
        var identity = Identity(path);
        if ((nonce is null && identity.Version != new Version(1, 0, 0))
            || (nonce is not null && (identity.Version != new Version(2, 0, 0) || !Guid.TryParseExact(nonce, "N", out _)))) throw new InvalidDataException();
        var index = owned.Count + 1;
        var image = boundary.Check(package.FilePath(Package.Name + ".exe"));
        var stop = boundary.Check(Path.Combine(scenarioRoot, $"child-{index}.stop"));
        var observation = boundary.Check(Path.Combine(scenarioRoot, $"child-{index}.observed.json"));
        var ticketPath = boundary.Check(Path.Combine(scenarioRoot, $"child-{index}.ticket.json"));
        if (File.Exists(stop) || File.Exists(observation) || File.Exists(ticketPath)) throw new InvalidDataException("Stale child material.");
        var ticket = new ChildTicket(target, nonce ?? "", nonce is null ? "restored-old" : withholdFirstStop ? "no-ready" : "ready",
            stop, observation, image, package.ExpectedHash(Package.Name + ".exe"), identity.Sha256, DateTime.UtcNow.AddSeconds(30).Ticks);
        Files.WriteJson(boundary, ticketPath, ticket);
        // Persist exact launch argv and image hash before creating any process.
        var info = new ProcessStartInfo(image) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = scenarioRoot };
        foreach (var arg in new[] { "--fixture-child", "--execute", "--sandbox-root", boundary.Root, "--package-root", package.Root,
                     "--package-sha256", package.ManifestHash, "--ticket", ticketPath }) info.ArgumentList.Add(arg);
        Record("planned-start", new { image, ticket.ImageHash, Arguments = info.ArgumentList.ToArray(), ticket.Mode });
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Fixture Process.Start returned null.");
        var child = new OwnedChild(process.Id, process.StartTime.ToUniversalTime().Ticks, image, target, nonce, stop, observation);
        owned.Add(child);
        Record("actual-start", child);
        if (Observe(child) != UpdateExitState.Alive) throw new InvalidOperationException("Cannot establish fixture process ownership.");
        return new(child.Id, child.StartTicks);
    }
    public async Task<bool> WaitReadyAsync(UpdateReceipt receipt, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(3))
        {
            token.ThrowIfCancellationRequested();
            VerifyMaterials();
            if (receipt.ProcessId is not { } pid || receipt.ProcessStartTicks is not { } ticks || ExitState(new(pid, ticks)) != UpdateExitState.Alive) return false;
            boundary.Check(UpdateStartup.ReadyPath(target));
            if (UpdateStartup.IsReady(receipt, ExitState))
            {
                Record("fixture-ready-bound-to-receipt-not-app-ui", new { pid, ticks, receipt.Nonce });
                return true;
            }
            await Task.Delay(50, token);
        }
        Record("fixture-ready-timeout", new { receipt.Nonce });
        return false;
    }
    public UpdateExitState ExitState(UpdateProcess process)
    {
        var child = owned.SingleOrDefault(item => item.Id == process.Id && item.StartTicks == process.StartTicks);
        return child is null ? UpdateExitState.Unknown : Observe(child);
    }
    public UpdateExitState TargetExitState(string path)
    {
        if (path != target) return UpdateExitState.Unknown;
        var states = owned.Where(item => item.Target == path).Select(Observe).ToArray();
        return states.Contains(UpdateExitState.Alive) ? UpdateExitState.Alive : states.Contains(UpdateExitState.Unknown) ? UpdateExitState.Unknown : UpdateExitState.Exited;
    }
    private UpdateExitState Observe(OwnedChild child)
    {
        boundary.Check(child.ImagePath);
        try
        {
            using var process = Process.GetProcessById(child.Id);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != child.StartTicks) return UpdateExitState.Exited;
            var image = process.MainModule?.FileName;
            return image is not null && Boundary.Absolute(image).Equals(child.ImagePath, StringComparison.OrdinalIgnoreCase)
                && Files.Hash(child.ImagePath) == package.ExpectedHash(Package.Name + ".exe") ? UpdateExitState.Alive : UpdateExitState.Unknown;
        }
        catch (ArgumentException) { return UpdateExitState.Exited; }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        { return UpdateExitState.Unknown; }
    }
    public Task RequestExitAsync(UpdateProcess process)
    {
        if (withholdFirstStop && owned.Count == 1)
        {
            // Explicit test fault: the ready timeout leaves the real owned process alive.
            Record("test-fault-stop-withheld-for-pending-checkpoint", process);
            return Task.CompletedTask;
        }
        return StopOwnedAsync(process);
    }
    internal async Task StopOwnedAsync(UpdateProcess process)
    {
        var child = owned.SingleOrDefault(item => item.Id == process.Id && item.StartTicks == process.StartTicks)
            ?? throw new InvalidOperationException("Never signal an unowned PID.");
        var state = Observe(child);
        if (state == UpdateExitState.Exited) { Record("already-exited", child); return; }
        if (state != UpdateExitState.Alive) throw new InvalidOperationException("Ownership uncertain; no stop signal sent.");
        boundary.Check(child.StopFile);
        using (var output = new FileStream(child.StopFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) output.Flush(true);
        Record("owned-cooperative-stop", child);
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (Observe(child) == UpdateExitState.Exited) { Record("confirmed-exited", child); return; }
            await Task.Delay(50);
        }
        throw new InvalidOperationException("Fixture exit not confirmed. No force kill; preserve evidence and let the 30-second lease expire.");
    }
    internal async Task StopAllAsync()
    {
        var failures = new List<Exception>();
        foreach (var child in owned)
        {
            try { await StopOwnedAsync(new(child.Id, child.StartTicks)); }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count > 0) throw new AggregateException("Some fixture exits remain unconfirmed.", failures);
    }
    internal async Task ConfirmObservationAsync(OwnedChild child, string expectedPayloadHash, string mode)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(boundary.Check(child.ObservationFile)) && timer.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(50);
        var observation = Files.ReadJson<ChildObservation>(boundary.Check(child.ObservationFile));
        if (observation.Kind != "real-fixture-process-not-production-app" || observation.Id != child.Id || observation.StartTicks != child.StartTicks
            || observation.ImagePath != child.ImagePath || observation.ImageHash != package.ExpectedHash(Package.Name + ".exe")
            || observation.TargetPayload != target || observation.PayloadHash != expectedPayloadHash || observation.Mode != mode
            || observation.Nonce != (child.Nonce ?? "")) throw new InvalidDataException("Fixture child observation binding mismatch.");
    }
    internal object ProcessEvidence() => owned.Select(child => new { Owned = child, State = Observe(child).ToString(),
        Observation = File.Exists(boundary.Check(child.ObservationFile)) ? Files.ReadJson<JsonElement>(child.ObservationFile) : (JsonElement?)null }).ToArray();
    private void Record(string name, object detail)
    {
        events.Add(new { Event = name, Utc = DateTime.UtcNow, Detail = detail });
        Files.WriteJson(boundary, Path.Combine(scenarioRoot, "process-events.json"), events);
    }
}

internal static class FixtureChild
{
    internal static async Task RunAsync(Options options, Boundary boundary, Package package)
    {
        if (!options.Execute || options.Ticket is null) throw new InvalidOperationException("Explicit child execution required.");
        var ticketPath = boundary.Check(options.Ticket);
        var ticket = Files.ReadJson<ChildTicket>(ticketPath);
        var target = boundary.Check(ticket.Target);
        var stop = boundary.Check(ticket.StopFile);
        var observation = boundary.Check(ticket.ObservationFile);
        var image = boundary.Check(ticket.ImagePath);
        var scenarioRoot = Path.GetDirectoryName(ticketPath)!;
        if (Path.GetDirectoryName(target) != scenarioRoot || Path.GetDirectoryName(stop) != scenarioRoot || Path.GetDirectoryName(observation) != scenarioRoot
            || image != package.FilePath(Package.Name + ".exe") || !image.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)
            || ticket.ImageHash != package.ExpectedHash(Package.Name + ".exe") || Files.Hash(image) != ticket.ImageHash
            || ticket.PayloadHash != package.FixtureIdentity(target).Sha256 || ticket.Mode is not ("ready" or "no-ready" or "restored-old")
            || ticket.ExpiresUtcTicks <= DateTime.UtcNow.Ticks || ticket.ExpiresUtcTicks > DateTime.UtcNow.AddSeconds(30).Ticks
            || File.Exists(stop) || File.Exists(observation)) throw new InvalidDataException("Fixture child ticket is stale or not bound to this package and root.");
        using var self = Process.GetCurrentProcess();
        var process = new UpdateProcess(self.Id, self.StartTime.ToUniversalTime().Ticks);
        Files.WriteJson(boundary, observation, new ChildObservation("real-fixture-process-not-production-app", process.Id, process.StartTicks,
            image, ticket.ImageHash, target, ticket.PayloadHash, ticket.Nonce, ticket.Mode, DateTime.UtcNow));
        if (ticket.Mode == "ready")
        {
            boundary.Check(UpdateTransaction.ReceiptPath(target)); boundary.Check(UpdateStartup.ReadyPath(target)); boundary.Check(UpdateStartup.ReadyPath(target) + ".writing");
            UpdateStartup.PublishReady(target, ticket.Nonce, package.FixtureIdentity(target), process);
        }
        var lease = Stopwatch.StartNew();
        while (lease.Elapsed < TimeSpan.FromSeconds(30) && DateTime.UtcNow.Ticks < ticket.ExpiresUtcTicks)
        {
            boundary.Check(stop);
            if (File.Exists(stop)) return;
            await Task.Delay(50);
        }
        // A crash of the driver cannot leave a permanent fixture process behind.
    }
}
