using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.Overlay;
using CodexQuotaTaskbar.Host.Provider;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class DotNoticeOnlyTests
{
    [Fact]
    public void Presentation_alone_does_not_acknowledge_or_create_a_window() => Sta(() =>
    {
        var view = new DotTopNoticeView(); var opens = 0;
        view.OpenRequested += (_, _) => opens++; var state = State();
        view.Display(state.Messages[0], state); Arrange(view, 280, 36);
        Assert.Equal(0, opens); Assert.Null(Window.GetWindow(view));
        var openButton = Assert.Single(Descendants(view).OfType<Button>());
        Assert.False(openButton.Focusable);
        openButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, opens);
        // The shared single-message receipt remains available to the
        // coordinator; the strip itself exposes no batch-mark button.
        var action = Assert.IsType<DotInlineAction>(view.CreateAcknowledgement(singleDisplayedMessage: true));
        Assert.Equal("one", action.MessageId); Assert.Equal("ack-notice", action.Kind);
        Assert.Equal(state.ReadToken, action.ReadToken); Assert.Equal(state.ReadSequence, action.ReadSequence);
        Assert.Equal(1, state.UnreadCount);
        Assert.False(view.IsKeyboardFocusWithin);
    });

    [Fact]
    public void Signed_notice_reclaims_the_old_button_column_without_changing_its_row_height() => Sta(() =>
    {
        var view = new DotTopNoticeView(); var state = State();
        view.Display(state.Messages[0], state); Arrange(view, 280, 36);
        var root = Assert.IsType<Grid>(view.Content);
        var column = Assert.Single(root.ColumnDefinitions.Cast<ColumnDefinition>());
        Assert.True(column.Width.IsStar);
        Assert.Single(Descendants(view).OfType<Button>());
        Assert.Empty(Descendants(view).OfType<TextBox>());
        var summary = Assert.Single(Descendants(view).OfType<TextBlock>(), text => text.Text == view.SummaryText);
        Assert.True(summary.ActualWidth > 200, "The removed action must not leave a reserved column.");
        Assert.Equal(36, view.ActualHeight);
        Assert.Equal(36, view.PreferredHeightDip);
    });

    [Fact]
    public void Single_view_action_keeps_the_child_issued_sequence_not_parent_display_sequence() => Sta(() =>
    {
        var view = new DotTopNoticeView(); var state = State() with { Sequence = 999, ReadSequence = 7 };
        view.Display(state.Messages[0], state);
        var action = view.CreateAcknowledgement(singleDisplayedMessage: true)!;
        Assert.Equal(7, action.ReadSequence); Assert.Equal("one", action.MessageId);
    });

    [Theory]
    [InlineData("短消息", 500, true)]
    [InlineData("较长的合成正文，不应该把省略摘要冒充全文", 10, false)]
    [InlineData("源内容截断…", 500, false)]
    public void Full_text_test_is_conservative(string text, double width, bool expected) => Sta(() =>
    {
        var view = new DotTopNoticeView(); view.Display(new("one", text, null, true), 1);
        Assert.Equal(expected, view.CanDisplayEntireText(width));
        Assert.Null(view.CreateAcknowledgement());
    });

    [Fact]
    public void An_ancestor_clip_prevents_a_partial_intro_from_being_considered_full() => Sta(() =>
    {
        var view = new DotTopNoticeView(); view.Display(new("one", "短消息", null, true), 1);
        var clip = new Border { ClipToBounds = true, Child = view };
        Arrange(clip, 280, 18);
        Assert.False(view.IsFullyVisibleWithin(clip));
        Arrange(clip, 280, 36);
        Assert.True(view.IsFullyVisibleWithin(clip));
    });

    [Fact]
    public void Earlier_only_row_is_a_count_without_a_strip_batch_mark_entry() => Sta(() =>
    {
        var view = new DotTopNoticeView(); var opens = 0;
        view.OpenRequested += (_, _) => opens++;
        var state = State() with { Messages = [], UnreadCount = 7, EarlierUnreadCount = 7 };
        view.DisplayEarlier(state); Arrange(view, 280, 36);
        Assert.Null(view.DisplayedMessageId);
        Assert.Equal("还有 7 条更早提醒", view.SummaryText);
        Assert.False(view.DisplayedTextIsComplete);
        Assert.Null(view.CreateAcknowledgement(singleDisplayedMessage: true));
        Assert.Equal(0, opens);
        Assert.Single(Descendants(view).OfType<Button>()).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, opens);
        Assert.Equal(7, state.UnreadCount);
        // The existing cat-head action factory, rather than a strip control,
        // supplies explicit batch acknowledgement of anonymous earlier items.
        var action = Assert.IsType<DotInlineAction>(OverlayCoordinator.CurrentNoticeAcknowledgement(state));
        Assert.Null(action.MessageId); Assert.Equal(state.ReadToken, action.ReadToken);
        Assert.Equal(state.ReadSequence, action.ReadSequence);
    });

    [Fact]
    public void Legacy_queued_actions_do_not_modify_retained_draft_or_pending_state() => Sta(() =>
    {
        var window = new DotChatWindow(inline: true);
        try
        {
            var input = (TextBox)window.FindName("Input"); input.Text = "synthetic retained draft";
            Set(window, "pendingText", "synthetic pending"); Set(window, "needsReview", true);
            var before = window.InlineSnapshot();
            foreach (var action in new[] { new DotInlineAction("draft", "replacement", 9),
                new DotInlineAction("send", "replacement", 9, RequestId: Guid.NewGuid().ToString("N")),
                new DotInlineAction("reviewed_sent", "replacement", 9, RequestId: Guid.NewGuid().ToString("N")),
                new DotInlineAction("page") })
                Invoke(window, "HandleInlineAction", action);
            var after = window.InlineSnapshot();
            Assert.Equal(before.DraftText, after.DraftText);
            Assert.Equal(before.HasPendingAttempt, after.HasPendingAttempt);
            Assert.True(after.NeedsReview); Assert.False(after.ReplyAllowed); Assert.False(after.CanSubmit);
            Assert.False(after.PageVisible); Assert.Null(PresentationSource.FromVisual(window));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Receiver_rejects_another_session_and_preserves_a_later_arrival() => Sta(() =>
    {
        var window = new DotChatWindow(inline: true);
        try
        {
            var inbox = (DotUnreadInbox)Get(window, "inlineInbox")!;
            inbox.ObserveNotification(new("one", "assistant", "synthetic", DateTimeOffset.UtcNow));
            var snapshot = window.InlineSnapshot();
            var action = new DotInlineAction("ack-notice", ReadToken: snapshot.ReadToken,
                ReadSessionId: Guid.NewGuid().ToString("N"), ReadSequence: snapshot.ReadSequence);
            Invoke(window, "HandleInlineAction", action); Assert.Equal(1, inbox.UnreadCount);
            inbox.ObserveNotification(new("later", "assistant", "synthetic", DateTimeOffset.UtcNow));
            Invoke(window, "HandleInlineAction", action with { ReadSessionId = snapshot.SessionId });
            Assert.Equal(1, inbox.UnreadCount); Assert.True(inbox.Messages.Single(item => item.Id == "later").IsUnread);
        }
        finally { window.Close(); }
    });

    private static DotInlineState State() => new(Guid.NewGuid().ToString("N"), 1, 0, "",
        [new("one", "短消息", DateTimeOffset.UtcNow, true)], 1, 0, "one", 1, true, false, false,
        false, true, false, "synthetic", ReplyAllowed: false, ReadToken: Guid.NewGuid().ToString("N"), ReadSequence: 1);
    private static void Arrange(FrameworkElement view, double width, double height)
    { view.Measure(new(width, height)); view.Arrange(new(0, 0, width, height)); view.UpdateLayout(); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static object? Get(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value);
    private static void Set(object value, string name, object? data) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(value, data);
    private static void Invoke(object value, string name, object data) => value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(value, [data]);
    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception caught) { error = caught; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Synthetic notice test did not finish.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
