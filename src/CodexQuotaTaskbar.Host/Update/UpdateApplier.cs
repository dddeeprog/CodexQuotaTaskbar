using System.Diagnostics;
using System.IO;

namespace CodexQuotaTaskbar.Host.Update;

internal static class UpdateApplier
{
    internal static Task ApplyAsync(UpdateApplyOptions options, CancellationToken token) =>
        new UpdateTransaction(new SystemUpdateRuntime()).ExecuteAsync(options, token);
    internal static Task ApplyAsync(UpdateApplyOptions options, IUpdateRuntime runtime, CancellationToken token) =>
        new UpdateTransaction(runtime).ExecuteAsync(options, token);
}

internal sealed class SystemUpdateRuntime : IUpdateRuntime
{
    public string Source => Environment.ProcessPath ?? throw new InvalidOperationException();
    public UpdatePackageIdentity Identity(string path) => UpdatePackageIdentity.Read(path);
    public async Task AttestAsync(string source, UpdatePackageIdentity identity, CancellationToken token)
    {
        using var service = new GitHubUpdateService();
        await service.VerifyInstallerSourceAsync(source, token);
        if (Identity(source) != identity) throw new InvalidDataException();
    }
    public async Task WaitForParentAsync(UpdateApplyOptions options, CancellationToken token)
    {
        Process parent;
        try { parent = Process.GetProcessById(options.ParentProcessId); }
        catch (ArgumentException) { return; }
        using (parent)
        {
            if (parent.HasExited) return;
            var start = parent.StartTime.ToUniversalTime().Ticks;
            using var self = Process.GetCurrentProcess();
            if (options.ParentProcessId == Environment.ProcessId || start > self.StartTime.ToUniversalTime().Ticks
                || options.ParentStartTicks is { } expected && start != expected
                || !Path.GetFullPath(parent.MainModule?.FileName ?? "").Equals(options.TargetExecutable, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException();
            await parent.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(45), token);
        }
    }
    private string? activeNonce;
    public UpdateProcess? Start(string target, string? nonce)
    {
        activeNonce = nonce;
        var info = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)! };
        if (nonce is not null) { info.ArgumentList.Add("--update-ready"); info.ArgumentList.Add(nonce); }
        using var process = Process.Start(info);
        return process is null ? null : new(process.Id, process.StartTime.ToUniversalTime().Ticks);
    }
    public UpdateExitState ExitState(UpdateProcess process)
    {
        try
        {
            using var current = Process.GetProcessById(process.Id);
            if (current.HasExited || current.StartTime.ToUniversalTime().Ticks != process.StartTicks) return UpdateExitState.Exited;
            return UpdateExitState.Alive;
        }
        catch (ArgumentException) { return UpdateExitState.Exited; }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { return UpdateExitState.Unknown; }
    }
    public UpdateExitState TargetExitState(string target)
    {
        var result = UpdateExitState.Exited;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(target)))
        using (process)
        {
            if (process.Id == Environment.ProcessId) continue;
            try
            {
                if (!process.HasExited && Path.GetFullPath(process.MainModule?.FileName ?? "").Equals(target, StringComparison.OrdinalIgnoreCase))
                    return UpdateExitState.Alive;
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
            { result = UpdateExitState.Unknown; }
        }
        return result;
    }
    public async Task<bool> WaitReadyAsync(UpdateReceipt receipt, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(45))
        {
            token.ThrowIfCancellationRequested();
            if (receipt.ProcessId is not { } id || receipt.ProcessStartTicks is not { } ticks || ExitState(new(id, ticks)) != UpdateExitState.Alive) return false;
            if (UpdateStartup.IsReady(receipt, ExitState) && Identity(receipt.Target).Version.ToString(3) == receipt.NewVersion) return true;
            await Task.Delay(100, token);
        }
        return false;
    }
    public async Task RequestExitAsync(UpdateProcess process)
    {
        // Only cooperative shutdown of this transaction's child; never force-kill.
        if (activeNonce is { } nonce && EventWaitHandle.TryOpenExisting(UpdateStartup.StopEventName(nonce), out var stop))
        { using (stop) stop.Set(); }
        var watch = Stopwatch.StartNew();
        while (ExitState(process) == UpdateExitState.Alive && watch.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(100);
    }
}
