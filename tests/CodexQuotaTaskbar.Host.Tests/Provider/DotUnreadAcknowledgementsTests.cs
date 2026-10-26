using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.Provider;

namespace CodexQuotaTaskbar.Host.Tests.Provider;

public sealed class DotUnreadAcknowledgementsTests
{
    [Fact]
    public void Snapshot_acknowledges_only_messages_present_before_the_click()
    {
        var inbox = Inbox("a", "b"); var snapshots = new DotUnreadAcknowledgements(inbox);
        var token = snapshots.Issue(10)!;
        inbox.ObserveNotification(Message("later"));
        Assert.True(snapshots.Acknowledge(token, 10));
        Assert.Equal(1, inbox.UnreadCount);
        Assert.Equal("later", Assert.Single(inbox.Messages, item => item.IsUnread).Id);
        Assert.False(snapshots.Acknowledge(token, 10));
        Assert.Equal(1, inbox.UnreadCount);
    }

    [Fact]
    public void Single_message_read_does_not_clear_its_snapshot_siblings()
    {
        var inbox = Inbox("a", "b"); var snapshots = new DotUnreadAcknowledgements(inbox);
        var token = snapshots.Issue(1)!;
        Assert.True(snapshots.Acknowledge(token, 1, "a"));
        Assert.Equal(1, inbox.UnreadCount);
        Assert.True(inbox.Messages.Single(item => item.Id == "b").IsUnread);
        Assert.False(snapshots.Acknowledge(token, 1, "b"));
    }

    [Theory]
    [InlineData(0)] [InlineData(9)] [InlineData(11)]
    public void Sequence_not_issued_for_a_token_is_rejected(long sequence)
    {
        var inbox = Inbox("a"); var snapshots = new DotUnreadAcknowledgements(inbox);
        var token = snapshots.Issue(10)!;
        Assert.False(snapshots.Acknowledge(token, sequence)); Assert.Equal(1, inbox.UnreadCount);
        Assert.True(snapshots.Acknowledge(token, 10));
    }

    [Fact]
    public void Unknown_token_or_message_cannot_choose_a_set_to_clear()
    {
        var inbox = Inbox("a"); var snapshots = new DotUnreadAcknowledgements(inbox);
        var token = snapshots.Issue(10)!;
        Assert.False(snapshots.Acknowledge(Guid.NewGuid().ToString("N"), 10));
        Assert.False(snapshots.Acknowledge(token, 10, "not-in-snapshot"));
        Assert.Equal(1, inbox.UnreadCount); Assert.True(snapshots.Acknowledge(token, 10));
    }

    [Fact]
    public void Stable_target_reuses_token_and_accepts_a_prior_published_sequence()
    {
        var inbox = Inbox("a"); var snapshots = new DotUnreadAcknowledgements(inbox);
        var token = snapshots.Issue(10);
        Assert.False(inbox.ObserveNotification(Message("a", "stream updated")));
        Assert.Equal(token, snapshots.Issue(15));
        Assert.True(snapshots.Acknowledge(token!, 10)); Assert.Null(snapshots.Issue(16));
    }

    [Fact]
    public void Old_tokens_are_bounded_without_discarding_unread_messages()
    {
        var inbox = Inbox("first"); var snapshots = new DotUnreadAcknowledgements(inbox);
        var first = snapshots.Issue(1)!;
        for (var i = 0; i < DotUnreadAcknowledgements.MaximumSnapshots; i++)
        { inbox.ObserveNotification(Message("new-" + i)); snapshots.Issue(i + 2); }
        Assert.False(snapshots.Acknowledge(first, 1));
        Assert.Equal(DotUnreadAcknowledgements.MaximumSnapshots + 1, inbox.UnreadCount);
    }

    [Fact]
    public void Older_body_and_id_eviction_preserves_exact_click_boundary()
    {
        var inbox = new DotUnreadInbox();
        for (var i = 0; i < 300; i++) inbox.ObserveNotification(Message("m-" + i));
        var snapshots = new DotUnreadAcknowledgements(inbox); var token = snapshots.Issue(1)!;
        for (var i = 300; i < 600; i++) inbox.ObserveNotification(Message("m-" + i));
        Assert.True(snapshots.Acknowledge(token, 1));
        Assert.Equal(300, inbox.UnreadCount);
    }

    [Fact]
    public void Anonymous_history_is_not_subtracted_after_another_reader_changed_it()
    {
        var inbox = new DotUnreadInbox();
        for (var i = 0; i < 300; i++) inbox.ObserveNotification(Message("m-" + i));
        var snapshots = new DotUnreadAcknowledgements(inbox); var token = snapshots.Issue(1)!;
        Assert.True(inbox.MarkRead("m-299"));
        for (var i = 300; i < 600; i++) inbox.ObserveNotification(Message("m-" + i));
        Assert.False(snapshots.Acknowledge(token, 1)); Assert.Equal(599, inbox.UnreadCount);
        var fresh = snapshots.Issue(2)!;
        Assert.True(snapshots.Acknowledge(fresh, 2)); Assert.Equal(0, inbox.UnreadCount);
    }

    [Fact]
    public void Read_and_known_id_state_survive_restore_without_renotifying()
    {
        var inbox = Inbox("a", "b"); var snapshots = new DotUnreadAcknowledgements(inbox);
        Assert.True(snapshots.Acknowledge(snapshots.Issue(1)!, 1));
        var restored = new DotUnreadInbox(); restored.Restore(inbox.Capture());
        Assert.Equal(0, restored.UnreadCount);
        Assert.False(restored.ObserveNotification(Message("a", "updated")));
        Assert.Equal(0, restored.UnreadCount);
    }

    [Fact]
    public async Task Explicit_read_persists_and_an_old_token_does_not_survive_receiver_restart()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DianDao.UnreadAckTests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            var store = new DotUnreadStore(System.IO.Path.Combine(directory, "unread.json"));
            var inbox = Inbox("a", "b"); var snapshots = new DotUnreadAcknowledgements(inbox);
            var token = snapshots.Issue(1)!;
            Assert.True(snapshots.Acknowledge(token, 1, "a"));
            await store.SaveAsync(inbox.Capture());
            var restored = new DotUnreadInbox(); restored.Restore((await store.LoadAsync())!);
            Assert.Equal(1, restored.UnreadCount);
            Assert.False(restored.Messages.Single(item => item.Id == "a").IsUnread);
            Assert.True(restored.Messages.Single(item => item.Id == "b").IsUnread);
            Assert.False(restored.ObserveNotification(Message("a", "stream updated")));
            Assert.False(new DotUnreadAcknowledgements(restored).Acknowledge(token, 1));
            Assert.Equal(1, restored.UnreadCount);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public void Acknowledgement_wire_is_identity_bound_and_cannot_carry_draft_or_count()
    {
        var action = new DotInlineAction("ack-notice", ReadToken: Guid.NewGuid().ToString("N"),
            ReadSessionId: Guid.NewGuid().ToString("N"), ReadSequence: 4);
        Assert.True(DotInlineWire.ValidAction(action));
        Assert.Equal(action, DotInlineWire.Deserialize<DotInlineAction>(DotInlineWire.Serialize(action)));
        Assert.False(DotInlineWire.ValidAction(action with { Count = 13 }));
        Assert.False(DotInlineWire.ValidAction(action with { Text = "synthetic draft" }));
        Assert.False(DotInlineWire.ValidAction(action with { ReadSessionId = "wrong" }));
        Assert.False(DotInlineWire.ValidAction(action with { ReadSequence = 0 }));
    }

    private static DotUnreadInbox Inbox(params string[] ids)
    { var value = new DotUnreadInbox(); foreach (var id in ids) value.ObserveNotification(Message(id)); return value; }
    private static DotMessage Message(string id, string text = "synthetic") => new(id, "assistant", text, DateTimeOffset.UtcNow);
}
