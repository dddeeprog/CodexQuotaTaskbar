using CodexQuotaTaskbar.Host.Resets;
using CodexQuotaTaskbar.Core.Quota;
using System.IO;

namespace CodexQuotaTaskbar.Host.Tests.Resets;

public sealed class ResetCreditUseServiceTests
{
    [Fact]
    public void Personal_use_service_is_separate_from_public_confirmation_history()
    {
        Assert.NotNull(typeof(ResetConfirmationStore).Assembly.GetType("CodexQuotaTaskbar.Host.Resets.ResetCreditUseService"));
    }

    [Fact]
    public async Task Confirmation_cancel_and_identity_change_never_send()
    {
        using var fixture = new Fixture();
        var confirmation = await fixture.Service.PrepareConfirmationAsync(false);
        fixture.Service.CancelConfirmation();
        await fixture.Service.ConfirmAsync(confirmation!.Token);
        Assert.Empty(fixture.Transport.Keys);
        confirmation = await fixture.Service.PrepareConfirmationAsync(false);
        fixture.Transport.Context = fixture.Transport.Context with { ConnectionEpoch = "changed" };
        await fixture.Service.ConfirmAsync(confirmation!.Token);
        Assert.Empty(fixture.Transport.Keys);
    }

    [Fact]
    public async Task Final_read_failure_and_unavailable_cards_never_send()
    {
        using var fixture = new Fixture();
        foreach (var cards in new[] { QuotaResetCreditsSnapshot.Unknown, new(0, [], DateTimeOffset.UtcNow),
            new QuotaResetCreditsSnapshot(1, null, DateTimeOffset.UtcNow, true),
            new QuotaResetCreditsSnapshot(1, [new("codexRateLimits", "available", DateTimeOffset.UtcNow.AddMinutes(-1))], DateTimeOffset.UtcNow),
            new QuotaResetCreditsSnapshot(7, [new("codexRateLimits", "available", DateTimeOffset.UtcNow.AddMinutes(-1))], DateTimeOffset.UtcNow) })
        {
            fixture.Transport.Context = fixture.Transport.Context with { Cards = cards };
            Assert.Null(await fixture.Service.PrepareConfirmationAsync(false));
        }
        fixture.Transport.Context = Context();
        var confirmation = await fixture.Service.PrepareConfirmationAsync(false);
        fixture.Transport.ReadFails = true;
        await fixture.Service.ConfirmAsync(confirmation!.Token);
        Assert.Empty(fixture.Transport.Keys);
    }

    [Fact]
    public async Task Workspace_change_after_confirmation_preparation_never_sends()
    {
        using var fixture = new Fixture();
        fixture.Transport.Context = Context() with { WorkspaceBinding = "synthetic-workspace-A" };
        var confirmation = await fixture.Service.PrepareConfirmationAsync(false);
        Assert.NotNull(confirmation);
        fixture.Transport.Context = fixture.Transport.Context with { WorkspaceBinding = "synthetic-workspace-B" };
        await fixture.Service.ConfirmAsync(confirmation.Token);
        Assert.Empty(fixture.Transport.Keys);
        Assert.False(File.Exists(fixture.Path));
    }

    [Fact]
    public async Task Unknown_survives_restart_and_explicit_retry_uses_same_key_even_when_count_zero()
    {
        using var fixture = new Fixture();
        fixture.Transport.Outcome = "future";
        var confirmation = await fixture.Service.PrepareConfirmationAsync(false);
        await fixture.Service.ConfirmAsync(confirmation!.Token);
        Assert.Equal(ResetCreditUseState.Unknown, fixture.Service.View.State);
        var key = Assert.Single(fixture.Transport.Keys);
        fixture.Service.CancelConfirmation(); // Closing and reopening a view must retain the warning.
        Assert.Equal(ResetCreditUseState.Unknown, fixture.Service.View.State);
        Assert.Equal("此前请求结果尚未确认", fixture.Service.View.Message);
        Assert.Null(await fixture.Service.PrepareConfirmationAsync(false));
        fixture.Restart();
        Assert.Equal(ResetCreditUseState.Unknown, fixture.Service.View.State);
        Assert.False(string.IsNullOrWhiteSpace(fixture.Service.View.Message));
        fixture.Transport.Context = Context() with { ConnectionEpoch = "new-connection", Cards = new(0, [], DateTimeOffset.UtcNow) };
        fixture.Transport.Outcome = "alreadyRedeemed";
        confirmation = await fixture.Service.PrepareConfirmationAsync(true);
        Assert.True(confirmation!.IsRetry);
        await fixture.Service.ConfirmAsync(confirmation.Token);
        Assert.Equal(new[] { key, key }, fixture.Transport.Keys);
        Assert.Equal(ResetCreditUseState.Completed, fixture.Service.View.State);
        var persisted = await File.ReadAllTextAsync(fixture.Path);
        Assert.DoesNotContain("Synthetic user", persisted);
        Assert.DoesNotContain("new-connection", persisted);
        fixture.Restart();
        Assert.Equal(ResetCreditUseState.Completed, fixture.Service.View.State);
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("alreadyRedeemed")]
    [InlineData("noCredit")]
    [InlineData("nothingToReset")]
    public async Task All_definite_outcomes_remain_complete_when_refresh_fails(string outcome)
    {
        using var fixture = new Fixture();
        fixture.Transport.Outcome = outcome;
        fixture.Transport.RefreshFails = true;
        var confirmation = await fixture.Service.PrepareConfirmationAsync(false);
        await fixture.Service.ConfirmAsync(confirmation!.Token);
        Assert.Equal(ResetCreditUseState.Completed, fixture.Service.View.State);
        Assert.False(fixture.Service.View.CanRetry);
        Assert.Contains("最新卡/额度读取失败", fixture.Service.View.Message);
        Assert.Contains("不要重复使用", fixture.Service.View.Message);
        Assert.False(fixture.Service.CanStartNewRequest(fixture.Transport.Context.Cards));
        Assert.False(fixture.Service.CanStartNewRequest(QuotaResetCreditsSnapshot.Unknown));
        Assert.True(fixture.Service.CanStartNewRequest(new(1, null, DateTimeOffset.UtcNow.AddMinutes(1))));
        Assert.Null(await fixture.Service.PrepareConfirmationAsync(false)); // old snapshot predates sent request
        Assert.Single(fixture.Transport.Keys);
    }

    [Fact]
    public async Task Pending_is_durable_before_send_and_busy_close_cannot_cancel_or_duplicate()
    {
        using var fixture = new Fixture();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.OnConsume = async () =>
        {
            Assert.Contains("pending", await File.ReadAllTextAsync(fixture.Path));
            return await release.Task;
        };
        var confirmation = await fixture.Service.PrepareConfirmationAsync(false);
        var sending = fixture.Service.ConfirmAsync(confirmation!.Token);
        Assert.Equal(ResetCreditUseState.Sending, fixture.Service.View.State);
        fixture.Service.CancelConfirmation();
        await fixture.Service.ConfirmAsync(confirmation.Token);
        Assert.Null(await fixture.Service.PrepareConfirmationAsync(false));
        release.SetResult("reset");
        await sending;
        Assert.Single(fixture.Transport.Keys);
        Assert.Equal(ResetCreditUseState.Completed, fixture.Service.View.State);
    }

    [Fact]
    public async Task Corrupt_store_second_process_and_write_failure_fail_closed()
    {
        using var fixture = new Fixture();
        using (var second = new ResetCreditPendingStore(fixture.Path)) Assert.True(second.IsBlocked);
        var confirmation = await fixture.Service.PrepareConfirmationAsync(false);
        Directory.CreateDirectory(fixture.Path); // rename cannot replace a directory
        await fixture.Service.ConfirmAsync(confirmation!.Token);
        Assert.Empty(fixture.Transport.Keys);
        Assert.Equal(ResetCreditUseState.Blocked, fixture.Service.View.State);
        Directory.Delete(fixture.Path);
        fixture.Service.Dispose();
        await File.WriteAllTextAsync(fixture.Path, "corrupt");
        fixture.Restart();
        Assert.Equal(ResetCreditUseState.Blocked, fixture.Service.View.State);
        Assert.Null(await fixture.Service.PrepareConfirmationAsync(false));
    }

    [Fact]
    public async Task Retry_different_account_and_failed_read_keep_original_unknown()
    {
        using var fixture = new Fixture();
        fixture.Transport.OnConsume = () => throw new IOException("simulated transport loss");
        var confirmation = await fixture.Service.PrepareConfirmationAsync(false);
        await fixture.Service.ConfirmAsync(confirmation!.Token);
        fixture.Restart();
        fixture.Transport.Context = fixture.Transport.Context with { AccountFingerprint = new string('b',64) };
        Assert.Null(await fixture.Service.PrepareConfirmationAsync(true));
        Assert.Equal(ResetCreditUseState.Unknown, fixture.Service.View.State);
        fixture.Transport.ReadFails = true;
        Assert.Null(await fixture.Service.PrepareConfirmationAsync(true));
        Assert.Equal(ResetCreditUseState.Unknown, fixture.Service.View.State);
        Assert.Single(fixture.Transport.Keys);
    }

    [Fact]
    public async Task Receipt_write_failure_keeps_durable_pending_for_same_key_recovery()
    {
        using var fixture = new Fixture();
        FileStream? receiptBlocker = null;
        fixture.Transport.OnConsume = () =>
        {
            receiptBlocker = new FileStream(fixture.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Task.FromResult("reset");
        };
        var confirmation = await fixture.Service.PrepareConfirmationAsync(false);
        await fixture.Service.ConfirmAsync(confirmation!.Token);
        Assert.Equal(ResetCreditUseState.Blocked, fixture.Service.View.State);
        Assert.Contains("已使用1张", fixture.Service.View.Message);
        Assert.Contains("结果记录保存失败", fixture.Service.View.Message);
        Assert.Contains("pending", await File.ReadAllTextAsync(fixture.Path));
        fixture.Service.CancelConfirmation();
        Assert.Contains("已使用1张", fixture.Service.View.Message);
        receiptBlocker!.Dispose();
        fixture.Restart();
        Assert.Equal(ResetCreditUseState.Unknown, fixture.Service.View.State);
        Assert.Single(fixture.Transport.Keys);
    }

    [Fact]
    public async Task New_pending_write_failure_does_not_reuse_previous_success_message()
    {
        using var fixture = new Fixture();
        var first = await fixture.Service.PrepareConfirmationAsync(false);
        await fixture.Service.ConfirmAsync(first!.Token);
        Assert.Equal(ResetCreditUseState.Completed, fixture.Service.View.State);
        var previousReceipt = await File.ReadAllTextAsync(fixture.Path);
        // A synthetic later provider snapshot authorizes a separate explicit request.
        fixture.Transport.Context = Context() with { Cards = new(1, null, DateTimeOffset.UtcNow.AddMinutes(1)) };
        var next = await fixture.Service.PrepareConfirmationAsync(false);
        Assert.NotNull(next);
        using (var pendingWriteBlocker = new FileStream(fixture.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await fixture.Service.ConfirmAsync(next.Token);
            Assert.Equal(ResetCreditUseState.Blocked, fixture.Service.View.State);
            Assert.DoesNotContain("已使用1张", fixture.Service.View.Message);
            Assert.Contains("已禁止继续使用", fixture.Service.View.Message);
            Assert.Equal(previousReceipt, await File.ReadAllTextAsync(fixture.Path));
        }
        Assert.Single(fixture.Transport.Keys);
    }

    [Fact]
    public async Task Readonly_check_never_consumes_and_count_changes_do_not_complete_unknown()
    {
        using var fixture = new Fixture();
        await fixture.Service.CheckAsync();
        Assert.Empty(fixture.Transport.Keys);
        fixture.Transport.Outcome = "future";
        var confirmation = await fixture.Service.PrepareConfirmationAsync(false);
        await fixture.Service.ConfirmAsync(confirmation!.Token);
        fixture.Transport.Context = Context() with { Cards = new(0, [], DateTimeOffset.UtcNow) };
        await fixture.Service.CheckAsync();
        Assert.Equal(ResetCreditUseState.Unknown, fixture.Service.View.State);
        Assert.Single(fixture.Transport.Keys);
        Assert.Contains("卡数变化不能确认结果", fixture.Service.View.Message);
        fixture.Transport.ReadFails = true;
        await fixture.Service.CheckAsync();
        Assert.Equal(ResetCreditUseState.Unknown, fixture.Service.View.State);
        Assert.Contains("此前请求结果仍未确认", fixture.Service.View.Message);
        Assert.Single(fixture.Transport.Keys);
        fixture.Restart();
        Assert.Equal(ResetCreditUseState.Unknown, fixture.Service.View.State);
    }

    [Theory]
    [InlineData(false, "已只读刷新最新卡与额度信息")]
    [InlineData(true, "只读刷新失败，请稍后重试")]
    public async Task Idle_readonly_refresh_uses_normal_status_without_prior_unknown_warning(bool readFails, string message)
    {
        using var fixture = new Fixture();
        fixture.Transport.ReadFails = readFails;
        await fixture.Service.CheckAsync();
        Assert.Equal(ResetCreditUseState.Idle, fixture.Service.View.State);
        Assert.Equal(message, fixture.Service.View.Message);
        Assert.False(fixture.Service.View.CanRetry);
        Assert.DoesNotContain("此前请求", fixture.Service.View.Message);
        Assert.Empty(fixture.Transport.Keys);
        Assert.False(File.Exists(fixture.Path));
    }

    [Fact]
    public async Task Null_read_context_is_refresh_failure_instead_of_success()
    {
        using var fixture = new Fixture();
        fixture.Transport.ReturnsNull = true;
        await fixture.Service.CheckAsync();
        Assert.Equal(ResetCreditUseState.Idle, fixture.Service.View.State);
        Assert.Contains("只读刷新失败", fixture.Service.View.Message);
        Assert.DoesNotContain("已只读刷新", fixture.Service.View.Message);
        Assert.Empty(fixture.Transport.Keys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completed_check_failure_keeps_known_result_and_shows_refresh_failure(bool returnsNull)
    {
        using var fixture = new Fixture();
        var confirmation = await fixture.Service.PrepareConfirmationAsync(false);
        await fixture.Service.ConfirmAsync(confirmation!.Token);
        fixture.Transport.ReturnsNull = returnsNull;
        fixture.Transport.ReadFails = !returnsNull;
        await fixture.Service.CheckAsync();
        Assert.Equal(ResetCreditUseState.Completed, fixture.Service.View.State);
        Assert.Contains("已使用1张", fixture.Service.View.Message);
        Assert.Contains("只读刷新失败", fixture.Service.View.Message);
        Assert.False(fixture.Service.View.CanRetry);
        Assert.Single(fixture.Transport.Keys);
    }

    private static ResetCreditUseContext Context() => new(new string('a',64), "synthetic-epoch", "Synthetic user",
        new(1, null, DateTimeOffset.UtcNow), true);

    [Fact]
    public async Task Unreadable_existing_pending_record_is_blocked_instead_of_first_run()
    {
        using var fixture = new Fixture();
        fixture.Transport.Outcome = "future";
        var confirmation = await fixture.Service.PrepareConfirmationAsync(false);
        await fixture.Service.ConfirmAsync(confirmation!.Token);
        fixture.Service.Dispose();
        using var simulatedReadFailure = new FileStream(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var blocked = new ResetCreditPendingStore(fixture.Path);
        Assert.True(blocked.IsBlocked);
        Assert.Null(blocked.Current);
        Assert.False(blocked.Save(new(new string('a',64), Guid.NewGuid().ToString("N"), "pending", null, DateTimeOffset.UtcNow)));
    }
    private sealed class FakeTransport : IResetCreditUseTransport
    {
        internal ResetCreditUseContext Context = ResetCreditUseServiceTests.Context();
        internal List<string> Keys = [];
        internal string Outcome = "reset";
        internal bool ReadFails, RefreshFails, ReturnsNull;
        internal Func<Task<string>>? OnConsume;
        public Task<ResetCreditUseContext?> ReadContextAsync(CancellationToken ct) => ReadFails ? throw new IOException() : Task.FromResult<ResetCreditUseContext?>(ReturnsNull ? null : Context);
        public Task<string> ConsumeAsync(ResetCreditUseContext context, string key, CancellationToken ct)
        { Keys.Add(key); Assert.False(ct.CanBeCanceled); return OnConsume?.Invoke() ?? Task.FromResult(Outcome); }
        public Task RefreshAsync(CancellationToken ct) => RefreshFails ? throw new IOException() : Task.CompletedTask;
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "reset-use-tests", Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(directory, "pending.json");
        internal readonly FakeTransport Transport = new();
        internal ResetCreditUseService Service;
        internal Fixture() { Service = new(Transport, new(Path)); }
        internal void Restart() { Service.Dispose(); Service = new(Transport, new(Path)); }
        public void Dispose() { Service.Dispose(); Directory.Delete(directory, recursive: true); }
    }
}
