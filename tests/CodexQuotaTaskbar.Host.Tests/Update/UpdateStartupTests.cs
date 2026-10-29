using System.Security.Cryptography;
using System.Text.Json;
using CodexQuotaTaskbar.Host.Update;

namespace CodexQuotaTaskbar.Host.Tests.Update;

public sealed class UpdateStartupTests : IDisposable
{
    private readonly string root = Path.Combine(TestSupport.RepositoryPaths.Root, "artifacts", "reliability", "update", "startup-" + Guid.NewGuid().ToString("N"));
    private string Target => Path.Combine(root, "CodexQuotaTaskbar.exe");
    private readonly string hash = Convert.ToHexString(SHA256.HashData("new"u8));
    private readonly UpdateReceipt receipt;
    public UpdateStartupTests()
    {
        Directory.CreateDirectory(root); File.WriteAllText(Target, "new");
        receipt = new(Guid.NewGuid().ToString("N"), Target, new string('A', 64), hash, "1.0.0", "2.0.0", UpdateTransactionPhase.Started, 101, 123);
        File.WriteAllBytes(UpdateTransaction.ReceiptPath(Target), JsonSerializer.SerializeToUtf8Bytes(receipt));
    }
    [Fact] public void Current_nonce_actual_version_and_live_process_are_required()
    {
        UpdateStartup.PublishReady(Target, receipt.Nonce, new(new(2, 0, 0), hash), new(101, 123));
        Assert.True(UpdateStartup.IsReady(receipt, _ => UpdateExitState.Alive));
        Assert.False(UpdateStartup.IsReady(receipt, _ => UpdateExitState.Exited));
        Assert.False(UpdateStartup.IsReady(receipt with { Nonce = Guid.NewGuid().ToString("N") }, _ => UpdateExitState.Alive));
        Assert.False(UpdateStartup.IsReady(receipt with { ProcessStartTicks = 124 }, _ => UpdateExitState.Alive));
    }
    [Theory] [InlineData("nonce")] [InlineData("version")] [InlineData("hash")]
    public void Wrong_identity_never_publishes_ready(string mismatch)
    {
        Assert.Throws<InvalidDataException>(() => UpdateStartup.PublishReady(Target,
            mismatch == "nonce" ? Guid.NewGuid().ToString("N") : receipt.Nonce,
            new(mismatch == "version" ? new(1, 0, 0) : new(2, 0, 0), mismatch == "hash" ? new string('B', 64) : hash), new(101, 123)));
        Assert.False(File.Exists(UpdateStartup.ReadyPath(Target)));
    }
    [Fact] public void Leftover_ready_file_from_another_transaction_is_rejected()
    {
        File.WriteAllBytes(UpdateStartup.ReadyPath(Target), JsonSerializer.SerializeToUtf8Bytes(receipt with { Nonce = Guid.NewGuid().ToString("N") }));
        Assert.False(UpdateStartup.IsReady(receipt, _ => UpdateExitState.Alive));
    }
    [Fact] public void Prepared_handshake_binds_parent_nonce_versions_and_staged_bytes()
    {
        var prepared = receipt with { Phase = UpdateTransactionPhase.Prepared, ParentId = 42, ParentStartTicks = 321 };
        File.WriteAllText(Target, "old");
        prepared = prepared with { OldHash = Convert.ToHexString(SHA256.HashData("old"u8)) };
        File.WriteAllText(UpdateTransaction.StagePath(Target), "new");
        File.WriteAllBytes(UpdateTransaction.ReceiptPath(Target), JsonSerializer.SerializeToUtf8Bytes(prepared));
        var options = new UpdateApplyOptions(42, Target) { Nonce = receipt.Nonce, ParentStartTicks = 321 };
        Assert.True(UpdateStartup.IsPrepared(options, new(new(2, 0, 0), hash)));
        Assert.False(UpdateStartup.IsPrepared(options with { Nonce = Guid.NewGuid().ToString("N") }, new(new(2, 0, 0), hash)));
        Assert.False(UpdateStartup.IsPrepared(options with { ParentStartTicks = 322 }, new(new(2, 0, 0), hash)));
        File.WriteAllText(UpdateTransaction.StagePath(Target), "changed");
        Assert.False(UpdateStartup.IsPrepared(options, new(new(2, 0, 0), hash)));
    }
    public void Dispose() => Directory.Delete(root, true);
}
