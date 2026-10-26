using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.Lifetime;

namespace CodexQuotaTaskbar.Host.Tests.Lifetime;

[Collection("Native UI isolation")]
public sealed class DotReplyModeTests
{
    [Fact]
    public void Every_appearance_rejects_new_send_without_checkpoint_or_draft_mutation() => Sta(async () =>
    {
        var path = TemporaryGuard();
        using var receiver = new DotChatProcess(inline: true, sendGuardPath: path);
        Invoke(receiver, "AcceptState", State());
        var before = receiver.LatestState!;
        foreach (var allowed in new[] { false, true, false })
        {
            receiver.SetReplyAllowed(allowed);
            Assert.False(receiver.ReplyAllowed); Assert.False(receiver.LatestState!.CanSubmit);
            Assert.False(await receiver.SendInlineActionAsync(Send()));
            Assert.False(File.Exists(path));
            Assert.Equal(before.DraftText, receiver.LatestState.DraftText);
            Assert.Equal(before.AttemptState, receiver.LatestState.AttemptState);
            Assert.Equal(before.LastRequestId, receiver.LatestState.LastRequestId);
            Assert.False(receiver.LatestState.HasPendingAttempt);
        }
    });

    [Fact]
    public void A_queued_send_is_rejected_before_waiting_for_the_guard_gate() => Sta(async () =>
    {
        var path = TemporaryGuard();
        using var receiver = new DotChatProcess(inline: true, sendGuardPath: path);
        using var fake = Process.GetCurrentProcess();
        Set(receiver, "process", fake); Set(receiver, "generation", 1L);
        Invoke(receiver, "AcceptState", State());
        var before = receiver.LatestState!;
        var gate = Get<SemaphoreSlim>(receiver, "guardGate"); await gate.WaitAsync();
        try
        {
            var sending = receiver.SendInlineActionAsync(Send());
            Assert.True(sending.IsCompleted); Assert.False(await sending);
            receiver.SetReplyAllowed(true);
            Assert.False(receiver.ReplyAllowed);
            Assert.False(File.Exists(path)); Assert.Null(Get<string?>(receiver, "guardedRequestId"));
            Assert.Equal(before.DraftText, receiver.LatestState!.DraftText);
            Assert.Equal(before.AttemptState, receiver.LatestState.AttemptState);
            Assert.Equal(0, gate.CurrentCount);
        }
        finally { gate.Release(); Set(receiver, "process", null); }
    });

    [Fact]
    public void Removed_composer_actions_do_not_write_or_clear_an_existing_checkpoint() => Sta(async () =>
    {
        var writes = new List<DotInlineSendCheckpoint>();
        using var receiver = new DotChatProcess(inline: true, sendGuardPath: TemporaryGuard(), persistCheckpoint: checkpoint =>
        { writes.Add(checkpoint); return Task.CompletedTask; });
        var request = Guid.NewGuid().ToString("N");
        Set(receiver, "guardedRequestId", request); Set(receiver, "uncertainAfterDisconnect", true);
        Invoke(receiver, "AcceptState", State() with { DraftText = "synthetic newer draft", ClientDraftRevision = 9 });
        foreach (var action in new[] { Send(), new DotInlineAction("draft", "replacement", 10),
            new DotInlineAction("page"), new DotInlineAction("reviewed_sent", "replacement", 10),
            new DotInlineAction("reviewed_not_sent", "replacement", 10) })
            Assert.False(await receiver.SendInlineActionAsync(action));
        Assert.Empty(writes); Assert.Equal(request, Get<string?>(receiver, "guardedRequestId"));
        Assert.True(Get<bool>(receiver, "uncertainAfterDisconnect"));
        Assert.Equal("synthetic newer draft", receiver.LatestState!.DraftText);
        Assert.Equal(9, receiver.LatestState.ClientDraftRevision);
        Assert.True(receiver.LatestState.RecoveryRequired); Assert.True(receiver.LatestState.HasPendingAttempt);
    });

    [Fact]
    public void A_rejected_send_never_enters_the_pipe_gate_or_clears_a_prior_unknown_attempt() => Sta(async () =>
    {
        var writes = new List<DotInlineSendCheckpoint>();
        using var receiver = new DotChatProcess(inline: true, sendGuardPath: TemporaryGuard(), persistCheckpoint: checkpoint =>
        { writes.Add(checkpoint); return Task.CompletedTask; });
        using var fake = Process.GetCurrentProcess();
        Set(receiver, "process", fake); Set(receiver, "generation", 1L);
        var existing = Guid.NewGuid().ToString("N");
        Set(receiver, "guardedRequestId", existing); Set(receiver, "uncertainAfterDisconnect", true);
        Invoke(receiver, "AcceptState", State());
        var gate = Get<SemaphoreSlim>(receiver, "writeGate"); await gate.WaitAsync();
        try
        {
            var sending = receiver.SendInlineActionAsync(Send());
            Assert.True(sending.IsCompleted); Assert.False(await sending);
            receiver.SetReplyAllowed(false); receiver.SetReplyAllowed(true);
            Assert.Equal(0, gate.CurrentCount); Assert.Empty(writes);
            Assert.Equal(existing, Get<string?>(receiver, "guardedRequestId"));
            Assert.True(receiver.LatestState!.HasPendingAttempt); Assert.True(receiver.LatestState.NeedsReview);
            Assert.Equal("synthetic current draft", receiver.LatestState.DraftText);
        }
        finally { gate.Release(); Set(receiver, "process", null); }
    });

    [Fact]
    public void Existing_dispatched_attempt_can_confirm_without_granting_new_send_permission() => Sta(() =>
    {
        var writes = new List<DotInlineSendCheckpoint>();
        using var receiver = new DotChatProcess(inline: true, sendGuardPath: TemporaryGuard(), persistCheckpoint: checkpoint =>
        { writes.Add(checkpoint); return Task.CompletedTask; });
        var request = Guid.NewGuid().ToString("N");
        Set(receiver, "guardedRequestId", request);
        Invoke(receiver, "AcceptState", State() with { HasPendingAttempt = true, LastRequestId = request, AttemptState = "dispatched" });
        receiver.SetReplyAllowed(false);
        Assert.True(receiver.LatestState!.HasPendingAttempt);
        Invoke(receiver, "AcceptState", State() with { LastRequestId = request, AttemptState = "confirmed", DraftText = "" });
        Assert.False(receiver.LatestState!.HasPendingAttempt); Assert.False(receiver.LatestState.CanSubmit);
        Assert.Null(Get<string?>(receiver, "guardedRequestId"));
        Assert.Single(writes); Assert.False(writes[0].Uncertain);
        receiver.SetReplyAllowed(true); Assert.False(receiver.ReplyAllowed); Assert.False(receiver.LatestState.CanSubmit);
        return Task.CompletedTask;
    });

    [Fact]
    public void Appearance_changes_never_unlock_unknown_attempts_or_overwrite_a_newer_draft() => Sta(async () =>
    {
        var writes = new List<DotInlineSendCheckpoint>();
        using var receiver = new DotChatProcess(inline: true, sendGuardPath: TemporaryGuard(), persistCheckpoint: checkpoint =>
        { writes.Add(checkpoint); return Task.CompletedTask; });
        var request = Guid.NewGuid().ToString("N");
        Set(receiver, "guardedRequestId", request); Set(receiver, "uncertainAfterDisconnect", true);
        Invoke(receiver, "AcceptState", State() with { DraftText = "synthetic newer draft", ClientDraftRevision = 9 });
        receiver.SetReplyAllowed(false); Assert.False(await receiver.SendInlineActionAsync(Send()));
        receiver.SetReplyAllowed(true);
        Assert.False(receiver.LatestState!.CanSubmit); Assert.True(receiver.LatestState.RecoveryRequired);
        Assert.Equal(request, Get<string?>(receiver, "guardedRequestId"));
        Assert.Equal("synthetic newer draft", receiver.LatestState.DraftText); Assert.Equal(9, receiver.LatestState.ClientDraftRevision);
        Assert.Empty(writes);
    });

    [Fact]
    public void Older_wire_default_remains_readable_but_parent_projects_notification_only_policy() 
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(DotInlineWire.Serialize(State()))!.AsObject();
        Assert.True(node.Remove("ReplyAllowed"));
        var restored = DotInlineWire.Deserialize<DotInlineState>(node.ToJsonString())!;
        Assert.True(DotInlineWire.ValidState(restored)); Assert.True(restored.ReplyAllowed);
        Assert.True(restored.CanSubmit); Assert.False((restored with { ReplyAllowed = false }).CanSubmit);
    }

    private static DotInlineAction Send() => new("send", "synthetic current draft", 4,
        RequestId: Guid.NewGuid().ToString("N"));
    private static DotInlineState State() => new(Guid.NewGuid().ToString("N"), 1, 4, "synthetic current draft", [],
        0, 0, null, 0, true, false, false, false, true, false, "ready");
    private static string TemporaryGuard() => Path.Combine(Path.GetTempPath(), "DianDao.DotReplyModeTests",
        Guid.NewGuid().ToString("N"), "not-opened.json");
    private static T Get<T>(object target, string field) => (T)target.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Set(object target, string field, object? value) => target.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Invoke(object target, string method, params object?[] args)
    {
        try { target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
    }
    private static void Sta(Func<Task> test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await test(); }
                catch (Exception error) { failure = error; }
                finally { dispatcher.InvokeShutdown(); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Synthetic mode-gate check timed out");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
