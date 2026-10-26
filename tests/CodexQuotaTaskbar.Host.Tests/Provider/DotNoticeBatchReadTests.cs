using System.IO;
using CodexQuotaTaskbar.Host.Provider;

namespace CodexQuotaTaskbar.Host.Tests.Provider;

public sealed class DotNoticeBatchReadTests : IDisposable
{
    private static string TestRoot => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DianDao.NoticeBatchReadTests"));
    private readonly string directory = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));

    public DotNoticeBatchReadTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task Batch_mark_clears_current_count_but_preserves_stored_history_and_known_ids()
    {
        var inbox = Inbox("first", "second", "third");
        var before = inbox.Capture();
        var snapshots = new DotUnreadAcknowledgements(inbox);
        var token = snapshots.Issue(10)!;

        Assert.True(snapshots.Acknowledge(token, 10));
        Assert.Equal(0, inbox.UnreadCount);
        Assert.Null(snapshots.Issue(11));
        Assert.Equal(before.Messages.Select(item => item with { IsUnread = false }), inbox.Messages);
        Assert.Equal(before.KnownIds, inbox.Capture().KnownIds);

        var restored = await StoreAndReloadAsync(inbox, "batch.json");
        Assert.Equal(0, restored.UnreadCount);
        Assert.Equal(inbox.Messages, restored.Messages);
        Assert.False(restored.ObserveNotification(Message("first", "synthetic refreshed body")));
        Assert.Equal(0, restored.UnreadCount);
        Assert.True(restored.ObserveNotification(Message("next-batch")));
        Assert.Equal(1, restored.UnreadCount);
    }

    [Fact]
    public async Task In_flight_batch_mark_preserves_later_arrival_and_rejects_a_repeated_click_after_reload()
    {
        var inbox = Inbox("first", "second");
        var snapshots = new DotUnreadAcknowledgements(inbox);
        var token = snapshots.Issue(20)!;
        inbox.ObserveNotification(Message("arrived-after-click"));

        Assert.True(snapshots.Acknowledge(token, 20));
        Assert.False(snapshots.Acknowledge(token, 20));
        Assert.Equal("arrived-after-click", Assert.Single(inbox.Messages, item => item.IsUnread).Id);

        var restored = await StoreAndReloadAsync(inbox, "race.json");
        Assert.Equal(1, restored.UnreadCount);
        Assert.Equal(3, restored.Messages.Count);
        Assert.All(restored.Messages.Where(item => item.Id != "arrived-after-click"), item => Assert.False(item.IsUnread));
        var newReceiver = new DotUnreadAcknowledgements(restored);
        Assert.False(newReceiver.Acknowledge(token, 20));
        Assert.Equal(1, restored.UnreadCount);
        Assert.True(newReceiver.Acknowledge(newReceiver.Issue(1)!, 1));
        Assert.Equal(0, restored.UnreadCount);
    }

    [Fact]
    public async Task Restart_invalidates_an_unconsumed_old_batch_token_without_clearing_any_message()
    {
        var inbox = Inbox("first", "second");
        var oldReceiver = new DotUnreadAcknowledgements(inbox);
        var oldToken = oldReceiver.Issue(7)!;

        var restored = await StoreAndReloadAsync(inbox, "restart.json");
        var newReceiver = new DotUnreadAcknowledgements(restored);
        Assert.False(newReceiver.Acknowledge(oldToken, 7));
        Assert.Equal(2, restored.UnreadCount);
        var freshToken = newReceiver.Issue(1)!;
        Assert.NotEqual(oldToken, freshToken);
        Assert.True(newReceiver.Acknowledge(freshToken, 1));

        var reloadedAgain = await StoreAndReloadAsync(restored, "restart-after-mark.json");
        Assert.Equal(0, reloadedAgain.UnreadCount);
        Assert.Equal(2, reloadedAgain.Messages.Count);
        Assert.False(reloadedAgain.ObserveNotification(Message("second", "synthetic refresh")));
        Assert.Equal(0, reloadedAgain.UnreadCount);
    }

    [Fact]
    public async Task Legacy_anonymous_earlier_batch_is_confirmed_without_spending_a_later_message()
    {
        var inbox = new DotUnreadInbox();
        inbox.Restore(new(1, [], [], 11));
        var snapshots = new DotUnreadAcknowledgements(inbox);
        var token = snapshots.Issue(1)!;
        inbox.ObserveNotification(Message("later"));

        Assert.True(snapshots.Acknowledge(token, 1));
        Assert.Equal(0, inbox.EarlierUnreadCount);
        Assert.Equal(1, inbox.UnreadCount);
        Assert.True(Assert.Single(inbox.Messages).IsUnread);

        var restored = await StoreAndReloadAsync(inbox, "anonymous.json");
        Assert.Equal(0, restored.EarlierUnreadCount);
        Assert.Equal(1, restored.UnreadCount);
        Assert.Equal("later", Assert.Single(restored.Messages).Id);
    }

    [Fact]
    public async Task An_intervening_reader_keeps_ambiguous_anonymous_history_until_a_fresh_explicit_batch()
    {
        var inbox = new DotUnreadInbox();
        inbox.Restore(new(1, [], [], 11));
        var snapshots = new DotUnreadAcknowledgements(inbox);
        var oldToken = snapshots.Issue(1)!;
        Assert.True(inbox.AcknowledgeEarlier(3));
        inbox.ObserveNotification(Message("later"));

        Assert.False(snapshots.Acknowledge(oldToken, 1));
        Assert.Equal(8, inbox.EarlierUnreadCount);
        Assert.Equal(9, inbox.UnreadCount);
        var restored = await StoreAndReloadAsync(inbox, "anonymous-concurrent.json");
        Assert.Equal(9, restored.UnreadCount);
        Assert.True(Assert.Single(restored.Messages).IsUnread);

        var newReceiver = new DotUnreadAcknowledgements(restored);
        Assert.True(newReceiver.Acknowledge(newReceiver.Issue(1)!, 1));
        var final = await StoreAndReloadAsync(restored, "anonymous-final.json");
        Assert.Equal(0, final.UnreadCount);
        Assert.Equal(0, final.EarlierUnreadCount);
        Assert.Single(final.Messages);
        Assert.False(final.Messages[0].IsUnread);
    }

    private async Task<DotUnreadInbox> StoreAndReloadAsync(DotUnreadInbox inbox, string filename)
    {
        var store = new DotUnreadStore(Path.Combine(directory, filename));
        await store.SaveAsync(inbox.Capture());
        var restored = new DotUnreadInbox();
        restored.Restore(Assert.IsType<DotUnreadSnapshot>(await store.LoadAsync()));
        Assert.False(store.LastLoadInvalid);
        return restored;
    }

    private static DotUnreadInbox Inbox(params string[] ids)
    {
        var inbox = new DotUnreadInbox();
        foreach (var id in ids) Assert.True(inbox.ObserveNotification(Message(id)));
        return inbox;
    }

    private static DotMessage Message(string id, string? text = null) => new(id, "assistant", text ?? "synthetic " + id, DateTimeOffset.UtcNow);

    public void Dispose()
    {
        var full = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(full), TestRoot, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(full), "N", out _)
            || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing cleanup outside this own temporary batch-read test directory.");
        Directory.Delete(full, recursive: true);
    }
}
