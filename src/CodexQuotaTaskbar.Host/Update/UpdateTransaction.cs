using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace CodexQuotaTaskbar.Host.Update;

internal enum UpdateTransactionPhase { Prepared, Replacing, Replaced, Launching, Started, Committed, RolledBack }
internal sealed record UpdateReceipt(string Nonce, string Target, string OldHash, string NewHash,
    string OldVersion, string NewVersion, UpdateTransactionPhase Phase, int? ProcessId = null, long? ProcessStartTicks = null,
    int? ParentId = null, long? ParentStartTicks = null);
internal sealed record UpdateProcess(int Id, long StartTicks);
internal enum UpdateExitState { Exited, Alive, Unknown }
internal interface IUpdateRuntime
{
    string Source { get; }
    UpdatePackageIdentity Identity(string path);
    Task AttestAsync(string source, UpdatePackageIdentity identity, CancellationToken token);
    Task WaitForParentAsync(UpdateApplyOptions options, CancellationToken token);
    UpdateProcess? Start(string target, string? nonce);
    Task<bool> WaitReadyAsync(UpdateReceipt receipt, CancellationToken token);
    UpdateExitState ExitState(UpdateProcess process);
    UpdateExitState TargetExitState(string target);
    Task RequestExitAsync(UpdateProcess process);
}
internal interface IUpdateFiles
{
    bool Exists(string path);
    byte[] Read(string path);
    string Hash(string path);
    void Write(string path, byte[] bytes);
    void Copy(string source, string target);
    void Replace(string source, string target, string? backup);
    void Delete(string path);
    IDisposable Lock(string path);
}
internal sealed class SystemUpdateFiles : IUpdateFiles
{
    public bool Exists(string path) => File.Exists(path);
    public byte[] Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > 8192) throw new InvalidDataException();
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); return bytes;
    }
    public string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    public void Write(string path, byte[] bytes)
    {
        var temporary = path + ".writing";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(true); }
        File.Move(temporary, path, true);
    }
    public void Copy(string source, string target) => File.Copy(source, target, false);
    public void Replace(string source, string target, string? backup) => File.Replace(source, target, backup);
    public void Delete(string path) => File.Delete(path);
    public IDisposable Lock(string path) => new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
}
internal sealed class UpdateTransaction(IUpdateRuntime runtime, IUpdateFiles? fileOperations = null)
{
    private readonly IUpdateFiles files = fileOperations ?? new SystemUpdateFiles();
    internal async Task ExecuteAsync(UpdateApplyOptions options, CancellationToken token)
    {
        var target = Path.GetFullPath(options.TargetExecutable);
        var stage = UpdateApplyStage.Validate;
        UpdateReceipt? receipt = null;
        UpdateProcess? child = null;
        var parentExited = false;
        var launchUnknown = false;
        var replaced = false;
        var stageOwned = false;
        var outcome = "未替换";
        UpdatePackageIdentity? originalIdentity = null;
        IDisposable? held = null;
        try
        {
            held = files.Lock(target + ".update.lock");
            receipt = ReadReceipt(target);
            if (receipt is not null)
            {
                // Recovery is based on both hashes, never on a phase alone.
                var current = Hash(target);
                if (receipt.Phase is UpdateTransactionPhase.Started or UpdateTransactionPhase.Launching or UpdateTransactionPhase.Replacing or UpdateTransactionPhase.Replaced
                    && current == receipt.OldHash && !files.Exists(BackupPath(target)))
                {
                    // Restore may have committed its single-file replacement before
                    // the following journal publication was interrupted.
                    stage = UpdateApplyStage.WaitParent; await runtime.WaitForParentAsync(options, token); parentExited = true;
                    if (runtime.TargetExitState(target) != UpdateExitState.Exited) throw new InvalidOperationException();
                    receipt = receipt with { Phase = UpdateTransactionPhase.RolledBack }; Save(receipt);
                }
                if (receipt.Phase == UpdateTransactionPhase.Committed)
                {
                    if (current != receipt.NewHash) throw new InvalidDataException();
                    CleanupBackup(target, receipt);
                    if (runtime.Identity(runtime.Source).Sha256 == receipt.NewHash) return;
                }
                else if (receipt.Phase == UpdateTransactionPhase.RolledBack)
                {
                    if (current != receipt.OldHash) throw new InvalidDataException();
                    if (runtime.Identity(runtime.Source).Sha256 == receipt.NewHash)
                    {
                        outcome = $"已恢复 v{receipt.OldVersion}；该失败更新已阻止重复安装";
                        if (options.Nonce is null && !parentExited)
                        { stage = UpdateApplyStage.WaitParent; await runtime.WaitForParentAsync(options, token); parentExited = true; }
                        throw new InvalidOperationException();
                    }
                }
                else if (receipt.Phase is UpdateTransactionPhase.Launching or UpdateTransactionPhase.Started)
                {
                    if (current != receipt.NewHash) throw new InvalidDataException();
                    if (receipt.ProcessId is not { } pid || receipt.ProcessStartTicks is not { } ticks
                        || runtime.ExitState(new(pid, ticks)) != UpdateExitState.Exited)
                    { outcome = "待恢复"; throw new InvalidOperationException(); }
                    stage = UpdateApplyStage.WaitParent; await runtime.WaitForParentAsync(options, token); parentExited = true;
                    stage = UpdateApplyStage.Validate;
                    Restore(target, receipt); receipt = receipt with { Phase = UpdateTransactionPhase.RolledBack };
                    Save(receipt);
                    outcome = $"已恢复 v{receipt.OldVersion}；该失败更新已阻止重复安装";
                    throw new InvalidOperationException();
                }
                else if (current == receipt.NewHash)
                {
                    stage = UpdateApplyStage.WaitParent; await runtime.WaitForParentAsync(options, token); parentExited = true;
                    stage = UpdateApplyStage.Validate;
                    Restore(target, receipt); receipt = receipt with { Phase = UpdateTransactionPhase.RolledBack }; Save(receipt);
                    outcome = $"已恢复 v{receipt.OldVersion}；该失败更新已阻止重复安装";
                    throw new InvalidOperationException();
                }
                else if (current != receipt.OldHash || files.Exists(BackupPath(target))) throw new InvalidDataException();
                files.Delete(StagePath(target));
                // Keep a terminal receipt until the next candidate has passed
                // attestation and staging, then atomically publish its Prepared.
                if (receipt.Phase is not (UpdateTransactionPhase.RolledBack or UpdateTransactionPhase.Committed)) files.Delete(ReceiptPath(target));
                files.Delete(UpdateStartup.ReadyPath(target));
                receipt = null;
            }
            else if (files.Exists(BackupPath(target)) || files.Exists(StagePath(target)))
            { outcome = "待恢复"; throw new InvalidDataException(); }

            if (options.Nonce is null)
            { stage = UpdateApplyStage.WaitParent; await runtime.WaitForParentAsync(options, token); parentExited = true; }
            stage = UpdateApplyStage.Validate;
            if (Path.GetFullPath(runtime.Source).Equals(target, StringComparison.OrdinalIgnoreCase)
                || !Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            var oldIdentity = runtime.Identity(target);
            originalIdentity = oldIdentity;
            var next = runtime.Identity(runtime.Source);
            if (next.Version <= oldIdentity.Version) throw new InvalidDataException();
            await runtime.AttestAsync(runtime.Source, next, token);
            token.ThrowIfCancellationRequested();
            receipt = new(options.Nonce ?? Guid.NewGuid().ToString("N"), target, oldIdentity.Sha256, next.Sha256,
                oldIdentity.Version.ToString(3), next.Version.ToString(3), UpdateTransactionPhase.Prepared,
                ParentId: options.ParentProcessId, ParentStartTicks: options.ParentStartTicks);
            stage = UpdateApplyStage.Copy;
            stageOwned = true;
            files.Copy(runtime.Source, StagePath(target));
            if (Hash(StagePath(target)) != receipt.NewHash || runtime.Identity(StagePath(target)) != next) throw new InvalidDataException();
            Save(receipt);
            stage = UpdateApplyStage.WaitParent;
            if (!parentExited) { await runtime.WaitForParentAsync(options, token); parentExited = true; }
            token.ThrowIfCancellationRequested();
            // Recheck after the old process exits and before touching its image.
            stage = UpdateApplyStage.Validate;
            if (Hash(target) != receipt.OldHash || runtime.Identity(runtime.Source) != next || runtime.TargetExitState(target) != UpdateExitState.Exited) throw new InvalidDataException();
            if (Hash(StagePath(target)) != receipt.NewHash) throw new InvalidDataException();
            receipt = receipt with { Phase = UpdateTransactionPhase.Replacing }; Save(receipt);
            stage = UpdateApplyStage.Replace;
            files.Replace(StagePath(target), target, BackupPath(target)); replaced = true;
            receipt = receipt with { Phase = UpdateTransactionPhase.Replaced }; Save(receipt);
            stage = UpdateApplyStage.Start;
            // Persist intent before Process.Start: a crash/throw here cannot prove no child exists.
            receipt = receipt with { Phase = UpdateTransactionPhase.Launching }; Save(receipt);
            launchUnknown = true;
            child = runtime.Start(target, receipt.Nonce);
            launchUnknown = false;
            if (child is null) throw new InvalidOperationException();
            receipt = receipt with { Phase = UpdateTransactionPhase.Started, ProcessId = child.Id, ProcessStartTicks = child.StartTicks }; Save(receipt);
            if (!await runtime.WaitReadyAsync(receipt, token)) throw new TimeoutException();
            receipt = receipt with { Phase = UpdateTransactionPhase.Committed }; Save(receipt);
            outcome = "新版已就绪";
            stage = UpdateApplyStage.Cleanup;
            CleanupBackup(target, receipt);
        }
        catch (Exception error)
        {
            var diagnostic = UpdateApplyDiagnostic.Capture(stage, error);
            UpdateApplyDiagnostic? recovery = null;
            UpdateApplyDiagnostic? cleanup = null;
            if (held is not null && receipt is not null && stage == UpdateApplyStage.Replace)
            {
                try
                {
                    if (Hash(target) == receipt.NewHash && files.Exists(BackupPath(target)) && Hash(BackupPath(target)) == receipt.OldHash) replaced = true;
                    else if (Hash(target) != receipt.OldHash) outcome = "待恢复";
                }
                catch { outcome = "待恢复"; }
            }
            if (receipt is not null && outcome != "新版已就绪" && replaced)
            {
                try
                {
                    if (child is not null)
                    {
                        // Cooperative shutdown only. Cancellation must not skip safety reconciliation.
                        if (runtime.ExitState(child) != UpdateExitState.Exited) await runtime.RequestExitAsync(child);
                        if (runtime.ExitState(child) != UpdateExitState.Exited) throw new InvalidOperationException();
                    }
                    if (launchUnknown) throw new InvalidOperationException();
                    Restore(target, receipt);
                    receipt = receipt with { Phase = UpdateTransactionPhase.RolledBack }; Save(receipt);
                    outcome = $"已恢复 v{receipt.OldVersion}";
                }
                catch (Exception rollback)
                { recovery = UpdateApplyDiagnostic.Capture(UpdateApplyStage.Rollback, rollback); outcome = "待恢复"; }
            }
            if (parentExited && outcome != "待恢复" && outcome != "新版已就绪")
            {
                try
                {
                    if ((receipt is not null ? Hash(target) == receipt.OldHash && runtime.Identity(target).Version.ToString(3) == receipt.OldVersion
                            : originalIdentity is not null && Hash(target) == originalIdentity.Sha256)
                        && runtime.TargetExitState(target) == UpdateExitState.Exited)
                    {
                        if (runtime.Start(target, null) is null) throw new InvalidOperationException();
                        outcome = $"{(replaced ? "已恢复" : "保留")} v{receipt?.OldVersion ?? originalIdentity!.Version.ToString(3)}，已尝试正常启动";
                    }
                }
                catch (Exception restart) { recovery ??= UpdateApplyDiagnostic.Capture(UpdateApplyStage.Restart, restart); }
            }
            try { if (held is not null && stageOwned && files.Exists(StagePath(target))) files.Delete(StagePath(target)); }
            catch (Exception extra) { cleanup = UpdateApplyDiagnostic.Capture(UpdateApplyStage.Cleanup, extra); }
            throw new UpdateApplyException(diagnostic, cleanup, replaced, 1) { RecoveryFailure = recovery, RecoveryOutcome = outcome };
        }
        finally { held?.Dispose(); }
    }

    private UpdateReceipt? ReadReceipt(string target)
    {
        var path = ReceiptPath(target);
        if (!files.Exists(path)) return null;
        var bytes = files.Read(path);
        if (bytes.Length > 8192) throw new InvalidDataException();
        var receipt = JsonSerializer.Deserialize<UpdateReceipt>(bytes) ?? throw new InvalidDataException();
        if (receipt.Target != target || !Guid.TryParseExact(receipt.Nonce, "N", out _) || !Enum.IsDefined(receipt.Phase)
            || !ValidHash(receipt.OldHash) || !ValidHash(receipt.NewHash)
            || !Version.TryParse(receipt.OldVersion, out var old) || !Version.TryParse(receipt.NewVersion, out var next) || next <= old)
            throw new InvalidDataException();
        return receipt;
    }
    private void Save(UpdateReceipt receipt) => files.Write(ReceiptPath(receipt.Target), JsonSerializer.SerializeToUtf8Bytes(receipt));
    private string Hash(string path) => files.Hash(path);
    private static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    private void Restore(string target, UpdateReceipt receipt)
    {
        if (runtime.TargetExitState(target) != UpdateExitState.Exited || Hash(target) != receipt.NewHash || !files.Exists(BackupPath(target)) || Hash(BackupPath(target)) != receipt.OldHash
            || runtime.Identity(BackupPath(target)).Version != Version.Parse(receipt.OldVersion)) throw new InvalidDataException();
        files.Replace(BackupPath(target), target, null);
        if (Hash(target) != receipt.OldHash) throw new InvalidDataException();
    }
    private void CleanupBackup(string target, UpdateReceipt receipt)
    {
        if (Hash(target) != receipt.NewHash) throw new InvalidDataException();
        if (files.Exists(BackupPath(target)))
        { if (Hash(BackupPath(target)) != receipt.OldHash) throw new InvalidDataException(); files.Delete(BackupPath(target)); }
        files.Delete(UpdateStartup.ReadyPath(target));
    }
    internal static string ReceiptPath(string target) => target + ".update.json";
    internal static string BackupPath(string target) => target + ".update.bak";
    internal static string StagePath(string target) => target + ".update.stage";
}
