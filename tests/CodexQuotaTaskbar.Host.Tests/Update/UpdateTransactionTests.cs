using System.Security.Cryptography;
using System.Text.Json;
using CodexQuotaTaskbar.Host.Update;

namespace CodexQuotaTaskbar.Host.Tests.Update;

// Real journal, hashes and fixture files; no process is created or killed.
public sealed class UpdateTransactionTests : IDisposable
{
    private readonly string root = Path.Combine(TestSupport.RepositoryPaths.Root, "artifacts", "reliability", "update", "fixture-" + Guid.NewGuid().ToString("N"));
    private readonly Runtime runtime;
    private readonly FaultFiles files = new();
    private string Target => Path.Combine(root, "中文 空格", "CodexQuotaTaskbar.exe");
    private string Source => Path.Combine(root, "download", "new.exe");
    public UpdateTransactionTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
        Directory.CreateDirectory(Path.GetDirectoryName(Source)!);
        File.WriteAllText(Target, "old"); File.WriteAllText(Source, "new");
        runtime = new Runtime(Source);
    }
    private Task Apply(CancellationToken token = default) => new UpdateTransaction(runtime, files).ExecuteAsync(new(42, Target), token);
    [Fact] public async Task Ready_success_commits_new_bytes_and_removes_backup()
    {
        await Apply();
        Assert.Equal("new", File.ReadAllText(Target)); Assert.False(File.Exists(UpdateTransaction.BackupPath(Target)));
        Assert.Equal(UpdateTransactionPhase.Committed, Receipt().Phase);
    }
    [Theory] [InlineData("parent-timeout")] [InlineData("parent-cancel")] [InlineData("attest")]
    public async Task Preconditions_leave_original_untouched(string failure)
    {
        runtime.Failure = failure;
        var error = await Assert.ThrowsAsync<UpdateApplyException>(() => Apply());
        Assert.Equal("old", File.ReadAllText(Target)); Assert.False(File.Exists(UpdateTransaction.BackupPath(Target)));
        Assert.Equal(failure == "attest" ? UpdateApplyStage.Validate : UpdateApplyStage.WaitParent, error.Failure.Stage);
    }
    [Theory] [InlineData("old")] [InlineData("same")]
    public async Task Non_increasing_version_cannot_enter_transaction(string version)
    {
        runtime.SourceVersion = version == "same" ? new(1, 0, 0) : new(0, 9, 0);
        await Assert.ThrowsAsync<UpdateApplyException>(() => Apply()); Assert.Equal("old", File.ReadAllText(Target));
    }
    [Theory] [InlineData("copy", false)] [InlineData("replace", false)] [InlineData("disk", true)]
    public async Task Write_failure_preserves_original_and_primary_when_cleanup_also_fails(string failure, bool cleanup)
    {
        files.Failure = failure; files.CleanupFailure = cleanup;
        var error = await Assert.ThrowsAsync<UpdateApplyException>(() => Apply());
        Assert.Equal("old", File.ReadAllText(Target));
        Assert.Equal(failure == "replace" ? UpdateApplyStage.Replace : UpdateApplyStage.Copy, error.Failure.Stage);
        Assert.Equal(cleanup, error.CleanupFailure is not null);
        Assert.DoesNotContain("secret", UpdateStatus.Failure(error).Detail);
    }
    [Theory] [InlineData("null")] [InlineData("exited")] [InlineData("wrong-ready-exited")]
    public async Task Confirmed_child_absence_restores_and_restarts_old_version(string launch)
    {
        runtime.Launch = launch;
        var error = await Assert.ThrowsAsync<UpdateApplyException>(() => Apply());
        Assert.Equal("old", File.ReadAllText(Target)); Assert.Equal(UpdateTransactionPhase.RolledBack, Receipt().Phase);
        Assert.Contains("已恢复", error.SafeDetail);
    }
    [Theory] [InlineData("throw")] [InlineData("timeout")] [InlineData("wrong-nonce")]
    public async Task Uncertain_child_keeps_new_target_and_old_backup(string launch)
    {
        runtime.Launch = launch;
        var error = await Assert.ThrowsAsync<UpdateApplyException>(() => Apply());
        Assert.Equal("new", File.ReadAllText(Target)); Assert.Equal("old", File.ReadAllText(UpdateTransaction.BackupPath(Target)));
        Assert.Contains("待恢复", error.SafeDetail);
        await Assert.ThrowsAsync<UpdateApplyException>(() => Apply());
        Assert.Equal("old", File.ReadAllText(UpdateTransaction.BackupPath(Target)));
    }
    [Fact] public async Task Rollback_failure_remains_attached_to_start_failure_and_preserves_backup()
    {
        runtime.Launch = "null"; files.Failure = "rollback";
        var error = await Assert.ThrowsAsync<UpdateApplyException>(() => Apply());
        Assert.Equal(UpdateApplyStage.Start, error.Failure.Stage); Assert.Contains("待恢复", error.SafeDetail);
        Assert.True(File.Exists(UpdateTransaction.BackupPath(Target)));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Interrupted_replacement_is_reconciled_from_hashes_before_fresh_attempt(bool replaced)
    {
        var receipt = new UpdateReceipt(Guid.NewGuid().ToString("N"), Target, Hash("old"), Hash("new"), "1.0.0", "2.0.0", UpdateTransactionPhase.Replacing);
        File.WriteAllText(UpdateTransaction.ReceiptPath(Target), JsonSerializer.Serialize(receipt));
        if (replaced) { File.WriteAllText(UpdateTransaction.BackupPath(Target), "old"); File.WriteAllText(Target, "new"); }
        runtime.Failure = "attest";
        await Assert.ThrowsAsync<UpdateApplyException>(() => Apply());
        Assert.Equal("old", File.ReadAllText(Target));
    }
    [Theory] [InlineData("broken")] [InlineData("external")]
    public async Task Corrupt_or_externally_changed_transaction_never_overwrites_target(string kind)
    {
        File.WriteAllText(UpdateTransaction.ReceiptPath(Target), kind == "broken" ? "{" : JsonSerializer.Serialize(new UpdateReceipt(Guid.NewGuid().ToString("N"), Target, Hash("old"), Hash("new"), "1.0.0", "2.0.0", UpdateTransactionPhase.Replaced)));
        if (kind == "external") File.WriteAllText(Target, "external");
        await Assert.ThrowsAsync<UpdateApplyException>(() => Apply()); Assert.Equal(kind == "broken" ? "old" : "external", File.ReadAllText(Target));
    }
    [Fact] public async Task Exclusive_lock_blocks_second_helper_without_modifying_target()
    {
        using var held = File.Open(Target + ".update.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<UpdateApplyException>(() => Apply()); Assert.Equal("old", File.ReadAllText(Target));
    }
    [Fact] public async Task Lock_loser_does_not_delete_the_other_helpers_stage()
    {
        File.WriteAllText(UpdateTransaction.StagePath(Target), "other helper owns this");
        using var held = File.Open(Target + ".update.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<UpdateApplyException>(() => Apply());
        Assert.Equal("other helper owns this", File.ReadAllText(UpdateTransaction.StagePath(Target)));
    }
    [Fact] public async Task Recovery_waits_for_existing_parent_before_restoring_old_image()
    {
        File.WriteAllText(UpdateTransaction.ReceiptPath(Target), JsonSerializer.Serialize(new UpdateReceipt(Guid.NewGuid().ToString("N"), Target, Hash("old"), Hash("new"), "1.0.0", "2.0.0", UpdateTransactionPhase.Replacing)));
        File.WriteAllText(UpdateTransaction.BackupPath(Target), "old"); File.WriteAllText(Target, "new");
        runtime.Failure = "parent-timeout";
        await Assert.ThrowsAsync<UpdateApplyException>(() => Apply());
        Assert.Equal("new", File.ReadAllText(Target)); Assert.Equal("old", File.ReadAllText(UpdateTransaction.BackupPath(Target)));
    }
    [Fact] public async Task Replace_that_changes_bytes_then_throws_is_reconciled_and_restored()
    {
        files.Failure = "replace-after";
        var error = await Assert.ThrowsAsync<UpdateApplyException>(() => Apply());
        Assert.Equal("old", File.ReadAllText(Target)); Assert.Contains("已恢复", error.SafeDetail);
    }
    [Fact] public async Task Another_live_target_process_blocks_replacement_even_after_parent_exits()
    {
        runtime.TargetState = UpdateExitState.Alive;
        await Assert.ThrowsAsync<UpdateApplyException>(() => Apply());
        Assert.Equal("old", File.ReadAllText(Target)); Assert.False(File.Exists(UpdateTransaction.BackupPath(Target)));
    }
    [Fact] public async Task New_caller_is_not_asked_to_exit_until_same_directory_copy_is_verified()
    {
        files.Failure = "copy";
        runtime.Failure = "parent-timeout";
        var error = await Assert.ThrowsAsync<UpdateApplyException>(() => new UpdateTransaction(runtime, files).ExecuteAsync(
            new(42, Target) { Nonce = Guid.NewGuid().ToString("N"), ParentStartTicks = 123 }, default));
        Assert.Equal(UpdateApplyStage.Copy, error.Failure.Stage);
        Assert.Equal("old", File.ReadAllText(Target));
    }
    [Fact] public async Task Rolled_back_failed_hash_is_blocked_across_new_transaction_instances()
    {
        runtime.Launch = "null";
        await Assert.ThrowsAsync<UpdateApplyException>(() => Apply());
        runtime.Launch = "ready";
        await Assert.ThrowsAsync<UpdateApplyException>(() => Apply());
        Assert.Equal("old", File.ReadAllText(Target)); Assert.Equal(UpdateTransactionPhase.RolledBack, Receipt().Phase);
    }
    [Theory] [InlineData("Started")] [InlineData("Launching")] [InlineData("Replacing")] [InlineData("Replaced")]
    public async Task Restore_completed_before_journal_write_is_recognized_from_old_hash(string phase)
    {
        var receipt = new UpdateReceipt(Guid.NewGuid().ToString("N"), Target, Hash("old"), Hash("new"), "1.0.0", "2.0.0", Enum.Parse<UpdateTransactionPhase>(phase), 101, 124);
        File.WriteAllBytes(UpdateTransaction.ReceiptPath(Target), JsonSerializer.SerializeToUtf8Bytes(receipt));
        runtime.Launch = "exited"; runtime.Failure = "attest";
        await Assert.ThrowsAsync<UpdateApplyException>(() => Apply());
        Assert.Equal(UpdateTransactionPhase.RolledBack, Receipt().Phase); Assert.Equal("old", File.ReadAllText(Target));
    }
    [Fact] public async Task Failed_preparation_of_newer_candidate_keeps_previous_failed_version_gate()
    {
        File.WriteAllBytes(UpdateTransaction.ReceiptPath(Target), JsonSerializer.SerializeToUtf8Bytes(new UpdateReceipt(Guid.NewGuid().ToString("N"), Target, Hash("old"), Hash("new"), "1.0.0", "2.0.0", UpdateTransactionPhase.RolledBack)));
        File.WriteAllText(Source, "newB"); runtime.SourceVersion = new(3, 0, 0); runtime.Failure = "attest";
        await Assert.ThrowsAsync<UpdateApplyException>(() => Apply());
        Assert.Equal("2.0.0", Receipt().NewVersion); Assert.Equal(UpdateTransactionPhase.RolledBack, Receipt().Phase);
        File.WriteAllText(Source, "new"); runtime.SourceVersion = new(2, 0, 0); runtime.Failure = null;
        await Assert.ThrowsAsync<UpdateApplyException>(() => Apply()); Assert.Equal("old", File.ReadAllText(Target));
    }
    [Fact] public async Task Legitimate_higher_candidate_commits_after_previous_version_rolled_back()
    {
        runtime.Launch = "null";
        await Assert.ThrowsAsync<UpdateApplyException>(() => Apply());
        Assert.Equal(UpdateTransactionPhase.RolledBack, Receipt().Phase);
        File.WriteAllText(Source, "newB"); runtime.SourceVersion = new(3, 0, 0); runtime.Launch = "ready";
        await Apply();
        Assert.Equal("newB", File.ReadAllText(Target));
        Assert.Equal("3.0.0", Receipt().NewVersion); Assert.Equal(Hash("newB"), Receipt().NewHash);
        Assert.Equal(UpdateTransactionPhase.Committed, Receipt().Phase);
        Assert.False(File.Exists(UpdateTransaction.BackupPath(Target)));
    }
    [Theory] [InlineData("Replacing")] [InlineData("Replaced")]
    public async Task Recovery_of_replaced_image_preserves_failed_hash_tombstone(string phase)
    {
        File.WriteAllBytes(UpdateTransaction.ReceiptPath(Target), JsonSerializer.SerializeToUtf8Bytes(new UpdateReceipt(Guid.NewGuid().ToString("N"), Target, Hash("old"), Hash("new"), "1.0.0", "2.0.0", Enum.Parse<UpdateTransactionPhase>(phase))));
        File.WriteAllText(UpdateTransaction.BackupPath(Target), "old"); File.WriteAllText(Target, "new");
        await Assert.ThrowsAsync<UpdateApplyException>(() => Apply());
        Assert.Equal("old", File.ReadAllText(Target)); Assert.Equal(UpdateTransactionPhase.RolledBack, Receipt().Phase);
    }
    [Theory] [InlineData("journal-prepared")] [InlineData("journal-replaced")]
    public async Task Journal_publication_failure_never_discards_original(string fault)
    {
        files.Failure = fault;
        var error = await Assert.ThrowsAsync<UpdateApplyException>(() => Apply());
        Assert.Equal("old", File.ReadAllText(Target));
        Assert.DoesNotContain("secret", error.SafeDetail);
    }
    [Theory] [InlineData("cancel-exited")] [InlineData("cancel-unknown")]
    public async Task Cancellation_after_replace_reconciles_child_before_any_rollback(string launch)
    {
        runtime.Launch = launch;
        var error = await Assert.ThrowsAsync<UpdateApplyException>(() => Apply());
        Assert.Equal(UpdateApplyErrorKind.Cancelled, error.Failure.Kind);
        Assert.Equal(launch == "cancel-exited" ? "old" : "new", File.ReadAllText(Target));
        Assert.Equal(launch != "cancel-exited", File.Exists(UpdateTransaction.BackupPath(Target)));
    }
    [Fact] public async Task Cleanup_failure_after_verified_ready_keeps_committed_image_and_backup()
    {
        files.Failure = "cleanup-backup";
        var error = await Assert.ThrowsAsync<UpdateApplyException>(() => Apply());
        Assert.Equal(UpdateApplyStage.Cleanup, error.Failure.Stage); Assert.Equal("new", File.ReadAllText(Target));
        Assert.Equal(UpdateTransactionPhase.Committed, Receipt().Phase); Assert.Equal("old", File.ReadAllText(UpdateTransaction.BackupPath(Target)));
    }
    [Fact] public async Task Journal_with_a_different_target_or_nonce_cannot_be_replayed()
    {
        File.WriteAllBytes(UpdateTransaction.ReceiptPath(Target), JsonSerializer.SerializeToUtf8Bytes(new UpdateReceipt("wrong", Source, Hash("old"), Hash("new"), "1.0.0", "2.0.0", UpdateTransactionPhase.Prepared)));
        await Assert.ThrowsAsync<UpdateApplyException>(() => Apply()); Assert.Equal("old", File.ReadAllText(Target));
    }
    private UpdateReceipt Receipt() => JsonSerializer.Deserialize<UpdateReceipt>(File.ReadAllBytes(UpdateTransaction.ReceiptPath(Target)))!;
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private sealed class Runtime(string source) : IUpdateRuntime
    {
        public string Source => source;
        internal string? Failure; internal string Launch = "ready"; internal Version SourceVersion = new(2, 0, 0);
        internal UpdateExitState TargetState = UpdateExitState.Exited;
        public UpdatePackageIdentity Identity(string path) => new(path == source || File.ReadAllText(path).StartsWith("new", StringComparison.Ordinal) ? SourceVersion : new Version(1, 0, 0), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        public Task AttestAsync(string path, UpdatePackageIdentity identity, CancellationToken token) => Failure == "attest" ? Task.FromException(new InvalidDataException("secret")) : Task.CompletedTask;
        public Task WaitForParentAsync(UpdateApplyOptions options, CancellationToken token) => Failure switch { "parent-timeout" => Task.FromException(new TimeoutException("secret")), "parent-cancel" => Task.FromException(new OperationCanceledException("secret")), _ => Task.CompletedTask };
        public UpdateProcess? Start(string target, string? nonce) => nonce is null ? new(100, 123) : Launch switch { "null" => null, "throw" => throw new InvalidOperationException("secret"), _ => new(101, 124) };
        public Task<bool> WaitReadyAsync(UpdateReceipt receipt, CancellationToken token) => Launch.StartsWith("cancel-", StringComparison.Ordinal) ? Task.FromException<bool>(new OperationCanceledException("secret")) : Task.FromResult(Launch == "ready");
        public UpdateExitState ExitState(UpdateProcess process) => Launch is "exited" or "wrong-ready-exited" or "cancel-exited" ? UpdateExitState.Exited : UpdateExitState.Unknown;
        public UpdateExitState TargetExitState(string target) => TargetState;
        public Task RequestExitAsync(UpdateProcess process) => Task.CompletedTask;
    }
    private sealed class FaultFiles : IUpdateFiles
    {
        private readonly SystemUpdateFiles inner = new(); internal string? Failure; internal bool CleanupFailure;
        public bool Exists(string path) => inner.Exists(path); public byte[] Read(string path) => inner.Read(path);
        public string Hash(string path) => inner.Hash(path);
        public void Write(string path, byte[] bytes)
        {
            var receipt = JsonSerializer.Deserialize<UpdateReceipt>(bytes);
            if (receipt is not null && (Failure == "journal-prepared" && receipt.Phase == UpdateTransactionPhase.Prepared
                || Failure == "journal-replaced" && receipt.Phase == UpdateTransactionPhase.Replaced))
            { Failure = null; throw new IOException("secret"); }
            inner.Write(path, bytes);
        }
        public void Copy(string source, string target)
        {
            if (Failure is "copy" or "disk") { inner.Write(target, [1]); throw Failure == "copy" ? new UnauthorizedAccessException("secret") : new IOException("secret", unchecked((int)0x80070070)); }
            inner.Copy(source, target);
        }
        public void Replace(string source, string target, string? backup)
        {
            if (Failure == "replace" || Failure == "rollback" && source.EndsWith(".bak", StringComparison.Ordinal)) throw new IOException("secret");
            inner.Replace(source, target, backup);
            if (Failure == "replace-after" && backup is not null) throw new IOException("secret");
        }
        public void Delete(string path) { if (CleanupFailure && path.EndsWith(".stage", StringComparison.Ordinal) || Failure == "cleanup-backup" && path.EndsWith(".bak", StringComparison.Ordinal)) throw new IOException("secret"); inner.Delete(path); }
        public IDisposable Lock(string path) => inner.Lock(path);
    }
}
