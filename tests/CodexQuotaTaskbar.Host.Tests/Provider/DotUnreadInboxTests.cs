using CodexQuotaTaskbar.Host.Provider;

namespace CodexQuotaTaskbar.Host.Tests.Provider;

public sealed class DotUnreadInboxTests
{
    [Fact]
    public void Distinct_messages_are_retained_and_only_explicit_read_acknowledges_each()
    {
        var inbox = new DotUnreadInbox();
        Assert.True(inbox.ObserveNotification(Message("first")));
        Assert.True(inbox.ObserveNotification(Message("second")));
        Assert.Equal(2, inbox.UnreadCount);
        Assert.Equal(new[] { "first", "second" }, inbox.Messages.Select(message => message.Id));
        Assert.True(inbox.MarkRead("first"));
        Assert.False(inbox.MarkRead("first"));
        Assert.False(inbox.MarkRead("missing"));
        Assert.Equal(1, inbox.UnreadCount);
        Assert.True(inbox.Messages[1].IsUnread);
    }

    [Fact]
    public void Streaming_changes_never_renotify_or_mark_an_acknowledged_message_unread()
    {
        var inbox = new DotUnreadInbox();
        Assert.True(inbox.ObserveNotification(Message("stable", "a")));
        Assert.False(inbox.ObserveNotification(Message("stable", "ab")));
        Assert.Equal("ab", Assert.Single(inbox.Messages).Text);
        Assert.Equal(1, inbox.UnreadCount);
        Assert.True(inbox.MarkRead("stable"));
        Assert.False(inbox.ObserveNotification(Message("stable", "abc")));
        Assert.Equal("abc", Assert.Single(inbox.Messages).Text);
        Assert.Equal(0, inbox.UnreadCount);
    }

    [Fact]
    public void Body_eviction_keeps_earlier_unread_count_and_known_id_deduplication()
    {
        var inbox = new DotUnreadInbox();
        for (var index = 0; index < 70; index++) Assert.True(inbox.ObserveNotification(Message($"message-{index}")));
        Assert.Equal(64, inbox.Messages.Count);
        Assert.Equal(6, inbox.EarlierUnreadCount);
        Assert.Equal(70, inbox.UnreadCount);
        Assert.False(inbox.ObserveNotification(Message("message-0", "streaming old content")));
        Assert.Equal(64, inbox.Messages.Count);
        Assert.Equal(70, inbox.UnreadCount);
        Assert.True(inbox.MarkRead("message-69"));
        Assert.Equal(69, inbox.UnreadCount);
        Assert.Equal(6, inbox.EarlierUnreadCount);
    }

    [Fact]
    public void Evicted_read_messages_do_not_inflate_earlier_unread_count()
    {
        var inbox = new DotUnreadInbox();
        inbox.ObserveNotification(Message("read-first")); inbox.MarkRead("read-first");
        for (var index = 0; index < 64; index++) inbox.ObserveNotification(Message($"message-{index}"));
        Assert.Equal(0, inbox.EarlierUnreadCount);
        Assert.Equal(64, inbox.UnreadCount);
    }

    [Fact]
    public void A_delayed_read_acknowledgement_can_read_a_body_evicted_from_the_active_view()
    {
        var inbox = new DotUnreadInbox();
        for (var index = 0; index < 70; index++) inbox.ObserveNotification(Message($"message-{index}"));
        Assert.DoesNotContain(inbox.Messages, message => message.Id == "message-0");
        Assert.True(inbox.MarkRead("message-0"));
        Assert.False(inbox.MarkRead("message-0"));
        Assert.Equal(5, inbox.EarlierUnreadCount);
        Assert.Equal(69, inbox.UnreadCount);
        Assert.False(inbox.ObserveNotification(Message("message-0", "later streaming content")));
        Assert.Equal(69, inbox.UnreadCount);
    }

    [Fact]
    public void Earlier_id_mapping_is_bounded_but_also_deduplicates_outside_the_known_window()
    {
        var inbox = new DotUnreadInbox();
        for (var index = 0; index < 210; index++) inbox.ObserveNotification(Message($"message-{index}"));
        var snapshot = inbox.Capture();
        Assert.Equal(128, snapshot.EarlierUnreadIds!.Length);
        Assert.DoesNotContain("message-18", snapshot.KnownIds);
        Assert.Contains("message-18", snapshot.EarlierUnreadIds);
        Assert.False(inbox.ObserveNotification(Message("message-18", "streamed again")));
        Assert.Equal(210, inbox.UnreadCount);
        Assert.True(inbox.MarkRead("message-18"));
        Assert.Equal(209, inbox.UnreadCount);
        Assert.False(inbox.MarkRead("message-0")); // Outside both bounded ID sets; explicit earlier review remains available.
    }

    [Fact]
    public void Explicit_partial_acknowledgement_of_earlier_messages_does_not_read_recent_messages()
    {
        var inbox = new DotUnreadInbox();
        for (var index = 0; index < 70; index++) inbox.ObserveNotification(Message($"message-{index}"));
        Assert.False(inbox.AcknowledgeEarlier(0));
        Assert.False(inbox.AcknowledgeEarlier(-1));
        Assert.False(inbox.AcknowledgeEarlier(7));
        Assert.True(inbox.AcknowledgeEarlier(4));
        Assert.Equal(2, inbox.EarlierUnreadCount);
        Assert.Equal(66, inbox.UnreadCount);
        Assert.All(inbox.Messages, item => Assert.True(item.IsUnread));
        Assert.False(inbox.MarkRead("message-0"));
        Assert.True(inbox.MarkRead("message-4"));
        Assert.Equal(1, inbox.EarlierUnreadCount);
        Assert.False(inbox.ObserveNotification(Message("message-0", "stream after explicit earlier review")));
        Assert.Equal(65, inbox.UnreadCount);
    }

    [Fact]
    public void Earlier_acknowledgement_consumes_unknown_oldest_count_before_recent_evicted_ids()
    {
        var inbox = new DotUnreadInbox();
        for (var index = 0; index < 210; index++) inbox.ObserveNotification(Message($"message-{index}"));
        Assert.True(inbox.AcknowledgeEarlier(18));
        Assert.Equal(128, inbox.EarlierUnreadCount);
        Assert.Equal(128, inbox.Capture().EarlierUnreadIds!.Length);
        Assert.True(inbox.MarkRead("message-18"));
        Assert.Equal(127, inbox.EarlierUnreadCount);
        Assert.All(inbox.Messages, item => Assert.True(item.IsUnread));
    }

    [Fact]
    public async Task Concurrent_new_messages_and_streaming_preserve_counts_during_earlier_acknowledgement()
    {
        var inbox = new DotUnreadInbox();
        for (var index = 0; index < 70; index++) inbox.ObserveNotification(Message($"message-{index}"));
        var add = Task.Run(() =>
        {
            for (var index = 70; index < 170; index++) Assert.True(inbox.ObserveNotification(Message($"message-{index}")));
        });
        var stream = Task.Run(() =>
        {
            for (var index = 0; index < 50; index++) Assert.False(inbox.ObserveNotification(Message("message-69", $"stream-{index}")));
        });
        var acknowledge = Task.Run(() => Assert.True(inbox.AcknowledgeEarlier(6)));
        await Task.WhenAll(add, stream, acknowledge);
        Assert.Equal(100, inbox.EarlierUnreadCount);
        Assert.Equal(164, inbox.UnreadCount);
        Assert.Equal(64, inbox.Messages.Count);
        Assert.All(inbox.Messages, item => Assert.True(item.IsUnread));
        Assert.True(DotUnreadInbox.IsValid(inbox.Capture()));
    }

    [Fact]
    public void Snapshot_roundtrip_preserves_read_flags_overflow_and_deduplication()
    {
        var inbox = new DotUnreadInbox();
        for (var index = 0; index < 140; index++) inbox.ObserveNotification(Message($"message-{index}"));
        inbox.MarkRead("message-139");
        var saved = inbox.Capture();
        Assert.Equal(128, saved.KnownIds.Length);
        Assert.Equal(64, saved.Messages.Length);
        var loaded = new DotUnreadInbox(); loaded.Restore(saved);
        Assert.Equal(inbox.Messages, loaded.Messages);
        Assert.Equal(inbox.UnreadCount, loaded.UnreadCount);
        Assert.Equal(76, loaded.EarlierUnreadCount);
        Assert.False(loaded.ObserveNotification(Message("message-139", "new stream")));
        Assert.False(loaded.Messages[^1].IsUnread);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nid")]
    public void Invalid_id_is_not_accepted(string id)
    {
        var inbox = new DotUnreadInbox();
        Assert.False(inbox.ObserveNotification(Message(id)));
        Assert.Equal(0, inbox.UnreadCount);
    }

    [Fact]
    public void Non_assistant_blank_and_oversized_id_are_rejected()
    {
        var inbox = new DotUnreadInbox();
        Assert.False(inbox.ObserveNotification(Message("user") with { Role = "user" }));
        Assert.False(inbox.ObserveNotification(Message("blank", "  ")));
        Assert.False(inbox.ObserveNotification(Message(new string('x', 257))));
        Assert.Empty(inbox.Messages);
    }

    [Fact]
    public void Identifier_length_boundary_matches_the_inline_protocol()
    {
        var inbox = new DotUnreadInbox();
        Assert.True(inbox.ObserveNotification(Message(new string('x', 256))));
        Assert.False(inbox.ObserveNotification(Message(new string('y', 257))));
        Assert.Single(inbox.Messages);
    }

    [Fact]
    public void Body_memory_is_bounded_and_public_collections_do_not_mutate_state()
    {
        var inbox = new DotUnreadInbox();
        inbox.ObserveNotification(Message("large", new string('x', 9000)));
        Assert.Equal(8000, Assert.Single(inbox.Messages).Text.Length);
        Assert.EndsWith("…", inbox.Messages[0].Text);
        var captured = inbox.Capture();
        captured.Messages[0] = captured.Messages[0] with { IsUnread = false };
        captured.KnownIds[0] = "changed";
        Assert.Equal(1, inbox.UnreadCount);
        Assert.Equal("large", inbox.Capture().KnownIds[0]);
    }

    [Fact]
    public void Bounded_unicode_text_does_not_split_a_surrogate_pair()
    {
        var inbox = new DotUnreadInbox();
        inbox.ObserveNotification(Message("unicode", new string('x', 7998) + "😀" + new string('x', 20)));
        var text = Assert.Single(inbox.Messages).Text;
        Assert.Equal(7999, text.Length);
        Assert.EndsWith("…", text);
        Assert.False(char.IsHighSurrogate(text[^2]));
    }

    [Fact]
    public void Invalid_snapshot_is_rejected_without_clearing_existing_state()
    {
        var inbox = new DotUnreadInbox(); inbox.ObserveNotification(Message("keep"));
        Assert.Throws<ArgumentException>(() => inbox.Restore(new(2, [], [], 0)));
        Assert.Throws<ArgumentException>(() => inbox.Restore(new(1, [new("missing", "text", null, true)], [], 0)));
        Assert.Throws<ArgumentException>(() => inbox.Restore(new(1, [], ["duplicate", "duplicate"], 0)));
        Assert.Throws<ArgumentException>(() => inbox.Restore(new(1, [], [], -1)));
        Assert.Throws<ArgumentException>(() => inbox.Restore(new(1, [], [], 0, ["not-counted"])));
        Assert.Throws<ArgumentException>(() => inbox.Restore(new(1, [], [], 2, ["duplicate", "duplicate"])));
        Assert.Equal("keep", Assert.Single(inbox.Messages).Id);
    }

    private static DotMessage Message(string id, string text = "synthetic notification") =>
        new(id, "assistant", text, DateTimeOffset.Parse("2026-10-24T16:00:00Z"));
}
