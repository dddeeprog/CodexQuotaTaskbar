using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Controls;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.Provider;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

[Collection("Native UI isolation")]
public sealed class DotInlineBackendTests
{
    [Fact]
    public void One_real_read_path_keeps_all_new_replies_and_ignores_history_and_user_rows() => Sta(() =>
    {
        var window = new DotChatWindow(inline: true);
        try
        {
            Apply(window, Row("old", "assistant", "old history", false), Row("u", "user", "user input", true),
                Row("a", "assistant", "first reply", true), Row("b", "assistant", "second reply", true),
                Row("c", "assistant", "third reply", true));
            var state = window.InlineSnapshot();
            Assert.Equal(3, state.UnreadCount); Assert.Equal(3, state.NotificationSerial);
            Assert.Equal("c", state.NotificationId);
            Assert.Equal(new[] { "a", "b", "c" }, state.Messages.Select(message => message.Id));
            Apply(window, Row("a", "assistant", "first reply expanded", true), Row("b", "assistant", "second reply", true),
                Row("c", "assistant", "third reply", true));
            state = window.InlineSnapshot();
            Assert.Equal(3, state.UnreadCount); Assert.Equal(3, state.NotificationSerial);
            Assert.Equal("first reply expanded", state.Messages[0].Text);
            Assert.False(window.IsVisible); Assert.Null(window.Browser.CoreWebView2);
            Assert.Equal(System.Windows.Visibility.Collapsed, ((StackPanel)window.FindName("Composer")).Visibility);
        }
        finally { window.Stop(); }
    });

    [Fact]
    public void Read_confirms_only_named_message_and_does_not_clear_draft_or_pending_attempt() => Sta(() =>
    {
        var window = new DotChatWindow(inline: true);
        try
        {
            Apply(window, Row("a", "assistant", "one", true), Row("b", "assistant", "two", true));
            ((TextBox)window.FindName("Input")).Text = "synthetic new draft";
            Set(window, "pendingText", "synthetic old attempt");
            var issued = window.InlineSnapshot();
            Action(window, new("ack-notice", MessageId: "a", ReadToken: issued.ReadToken,
                ReadSessionId: issued.SessionId, ReadSequence: issued.ReadSequence));
            var state = window.InlineSnapshot();
            Assert.Equal(1, state.UnreadCount); Assert.False(state.Messages[0].Unread); Assert.True(state.Messages[1].Unread);
            Assert.Equal("synthetic new draft", state.DraftText); Assert.True(state.HasPendingAttempt);
            Assert.Null(window.Browser.CoreWebView2);
        }
        finally { window.Stop(); }
    });

    [Fact]
    public void A_retained_newer_same_text_revision_survives_an_existing_attempt_receipt() => Sta(() =>
    {
        var window = new DotChatWindow(inline: true);
        try
        {
            ((TextBox)window.FindName("Input")).Text = "same synthetic text";
            Set(window, "inlineClientDraftRevision", 10L);
            Set(window, "pendingText", "same synthetic text");
            Set(window, "pendingDraftRevision", Get(window, "draftRevision"));
            Set(window, "pendingBaselineComplete", true);
            ((HashSet<string>)Get(window, "beforeSend")!).Add("existing");
            // Model a revision made before the composer was removed. The
            // confirmation path must still distinguish identical newer text.
            Set(window, "draftRevision", (long)Get(window, "draftRevision")! + 1);
            Set(window, "inlineClientDraftRevision", 11L);
            Set(window, "messages", new[] { new DotMessage("new-user", "user", "same synthetic text", DateTimeOffset.UtcNow, true) });
            Invoke(window, "CheckPendingSend");
            var state = window.InlineSnapshot();
            Assert.False(state.HasPendingAttempt); Assert.Equal(11, state.ClientDraftRevision);
            Assert.Equal("same synthetic text", state.DraftText);
            Assert.False((bool)Get(window, "draftClearedOnReceipt")!);
        }
        finally { window.Stop(); }
    });

    [Fact]
    public void Queued_send_is_ignored_without_mutating_retained_revision_or_attempt_status() => Sta(() =>
    {
        var window = new DotChatWindow(inline: true);
        try
        {
            ((TextBox)window.FindName("Input")).Text = "newer draft arrived while guard saved";
            Set(window, "inlineClientDraftRevision", 11L);
            var before = window.InlineSnapshot();
            var request = Guid.NewGuid().ToString("N");
            Action(window, new("send", "original Enter draft", 10, RequestId: request));
            var state = window.InlineSnapshot();
            Assert.Equal(before.LastRequestId, state.LastRequestId); Assert.Equal(before.AttemptState, state.AttemptState);
            Assert.Equal("newer draft arrived while guard saved", state.DraftText);
            Assert.False(state.HasPendingAttempt); Assert.Null(window.Browser.CoreWebView2);
            var revision = Get(window, "draftRevision");
            Action(window, new("send", "original Enter draft", 10, RequestId: request));
            Assert.Equal(revision, Get(window, "draftRevision"));
        }
        finally { window.Stop(); }
    });

    [Theory]
    [InlineData("reviewed_sent", 10)]
    [InlineData("reviewed_sent", 11)]
    [InlineData("reviewed_not_sent", 10)]
    public void Removed_recovery_controls_cannot_clear_retained_draft_or_unknown_attempt(string kind, long currentRevision) => Sta(() =>
    {
        var window = new DotChatWindow(inline: true);
        try
        {
            ((TextBox)window.FindName("Input")).Text = "synthetic retained draft";
            Set(window, "inlineClientDraftRevision", currentRevision);
            Set(window, "pendingText", "synthetic interrupted attempt");
            Set(window, "needsReview", true);
            Set(window, "attemptState", "uncertain");
            Action(window, new(kind, "synthetic retained draft", currentRevision, RequestId: Guid.NewGuid().ToString("N"),
                OriginalDraftRevision: 10));
            var state = window.InlineSnapshot();
            Assert.Equal("synthetic retained draft", state.DraftText);
            Assert.True(state.HasPendingAttempt); Assert.Equal("uncertain", state.AttemptState);
            Assert.True(state.NeedsReview); Assert.Equal(currentRevision, state.ClientDraftRevision);
        }
        finally { window.Stop(); }
    });

    [Fact]
    public void Snapshot_read_cannot_consume_new_overflow_and_legacy_count_actions_are_ignored() => Sta(() =>
    {
        var window = new DotChatWindow(inline: true);
        try
        {
            var inbox = (DotUnreadInbox)Get(window, "inlineInbox")!;
            for (var i = 0; i < 65; i++) inbox.ObserveNotification(new("n" + i, "assistant", "synthetic", DateTimeOffset.UtcNow));
            var issued = window.InlineSnapshot();
            inbox.ObserveNotification(new("another", "assistant", "synthetic", DateTimeOffset.UtcNow));
            Action(window, new("earlier-read", Count: 2)); Assert.Equal(2, inbox.EarlierUnreadCount);
            Action(window, new("earlier-read", Count: 1)); Assert.Equal(2, inbox.EarlierUnreadCount);
            var action = new DotInlineAction("ack-notice", ReadToken: issued.ReadToken,
                ReadSessionId: issued.SessionId, ReadSequence: issued.ReadSequence);
            Action(window, action);
            Assert.Equal(1, inbox.UnreadCount); Assert.Equal(0, inbox.EarlierUnreadCount);
            Assert.True(inbox.Messages.Single(item => item.Id == "another").IsUnread);
            Action(window, action); Assert.Equal(1, inbox.UnreadCount);
        }
        finally { window.Stop(); }
    });

    private static object Row(string id, string role, string text, bool fresh) => new
    { id, role, text, time = (fresh ? DateTimeOffset.UtcNow.AddMinutes(1) : DateTimeOffset.UtcNow.AddDays(-1)).ToString("o"), confirmable = true };
    private static void Apply(DotChatWindow window, params object[] rows)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { state = "ready", draftEmpty = true, messages = rows }));
        window.ApplyReadResult(json.RootElement);
    }
    private static object? Get(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Action(DotChatWindow window, DotInlineAction action) => Invoke(window, "HandleInlineAction", action);
    private static void Invoke(object target, string method, params object?[] args)
    {
        try { target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
    }
    private static void Sta(Action test)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { test(); } catch (Exception failure) { error = failure; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Synthetic backend check timed out");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
