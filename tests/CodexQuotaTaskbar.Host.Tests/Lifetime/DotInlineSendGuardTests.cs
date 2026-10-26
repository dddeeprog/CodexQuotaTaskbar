using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.Lifetime;

[Collection("Native UI isolation")]
public sealed class DotInlineSendGuardTests
{
    [Fact]
    public async Task Checkpoint_roundtrips_request_and_revision_without_content_or_input_history()
    {
        var root = Path.Combine(Path.GetTempPath(), "DianDao.DotInlineGuardTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "guard.json");
        try
        {
            var store = new DotInlineSendGuard(path);
            Assert.False((await store.LoadAsync()).Uncertain);
            var checkpoint = new DotInlineSendCheckpoint(true, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
                17, Guid.NewGuid().ToString("N"));
            await store.SaveAsync(checkpoint);
            Assert.Equal(checkpoint, await store.LoadAsync());
            var keys = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path)).RootElement
                .EnumerateObject().Select(property => property.Name).Order().ToArray();
            Assert.Equal(new[] { "DraftEpoch", "DraftRevision", "RequestId", "Uncertain", "Utc" }, keys);
            await store.SaveAsync(new(false, null, DateTimeOffset.UtcNow));
            Assert.False((await store.LoadAsync()).Uncertain);
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Invalid_guard_is_preserved_and_not_mistaken_for_clear()
    {
        var root = Path.Combine(Path.GetTempPath(), "DianDao.DotInlineGuardTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "guard.json");
        try
        {
            await File.WriteAllTextAsync(path, "not JSON");
            var store = new DotInlineSendGuard(path);
            await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => store.LoadAsync());
            Assert.Equal("not JSON", await File.ReadAllTextAsync(path));
            await File.WriteAllTextAsync(path, "{}");
            await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => store.LoadAsync());
            await File.WriteAllTextAsync(path, "{\"Uncertain\":true,\"RequestId\":null,\"Utc\":\"2026-10-24T00:00:00Z\"}");
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
            await File.WriteAllTextAsync(path, new string('x', 4097));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Offline_projection_advances_version_and_reaches_real_view_then_new_raw_state_remains_ordered() => Sta(() =>
    {
        using var process = new DotChatProcess(inline: true, sendGuardPath: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "never-opened.json"));
        var view = new DotInlineView();
        var states = new List<DotInlineState>();
        process.StateChanged += state => { states.Add(state); view.ApplyState(state); };
        var raw = State(1);
        Invoke(process, "AcceptState", raw);
        Assert.False(process.LatestState!.CanSubmit); Assert.False(process.LatestState.ReplyAllowed);
        Invoke(process, "PublishOfflineState");
        Assert.False(process.LatestState!.Online); Assert.False(process.LatestState.CanSubmit);
        Assert.True(states[1].Sequence > states[0].Sequence);
        Invoke(process, "AcceptState", raw with { Sequence = 2 });
        Assert.True(states[2].Sequence > states[1].Sequence); Assert.False(states[2].CanSubmit);
        Assert.False(states[2].ReplyAllowed);
    });

    [Fact]
    public void Unknown_latch_survives_new_child_session_and_rejects_optimistic_ready() => Sta(() =>
    {
        using var process = new DotChatProcess(inline: true, sendGuardPath: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "never-opened.json"));
        Set(process, "uncertainAfterDisconnect", true);
        Set(process, "guardedRequestId", Guid.NewGuid().ToString("N"));
        Invoke(process, "AcceptState", State(1));
        var first = process.LatestState!;
        Assert.False(first.CanSubmit); Assert.True(first.RecoveryRequired); Assert.False(first.RecoveryCanReview);
        Invoke(process, "AcceptState", State(1) with { SessionId = Guid.NewGuid().ToString("N") });
        Assert.False(process.LatestState!.CanSubmit); Assert.True(process.LatestState.HasPendingAttempt);
        Assert.True(process.LatestState.NeedsReview);
    });

    private static DotInlineState State(long sequence) => new(Guid.NewGuid().ToString("N"), sequence, 0, "synthetic", [],
        0, 0, null, 0, true, false, false, false, true, false, "ready");
    private static void Set(object target, string field, object? value) => target.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Invoke(object target, string method, params object?[] args)
    {
        try { target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
    }
    private static void Sta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { test(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Synthetic guard check timed out");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
