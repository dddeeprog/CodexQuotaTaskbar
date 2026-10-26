using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.UI;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Size = System.Windows.Size;
using SystemColors = System.Windows.SystemColors;

namespace CodexQuotaTaskbar.Host.Tests.UI;

[Collection("Native UI isolation")]
public sealed class DotTopViewsTests
{
    [Theory]
    [InlineData(160)] [InlineData(192)] [InlineData(224)] [InlineData(248)]
    public void Automatic_notice_is_a_single_bounded_row_with_no_reply_or_read_action(double width) => Sta(() =>
    {
        var notice = new DotTopNoticeView();
        notice.Display(Message("notice", "Synthetic first line\r\nsecond line\tthird line"), 3);
        Layout(notice, width);
        Assert.Equal(36, notice.PreferredHeightDip);
        Assert.InRange(notice.DesiredSize.Height, 1, 36);
        Assert.Equal("notice", notice.DisplayedMessageId);
        Assert.Equal("Synthetic first line second line third line", notice.SummaryText);
        Assert.Empty(Descendants(notice).OfType<TextBox>());
        var summaryButton = NoticeSummaryButton(notice);
        Assert.Equal(Visibility.Visible, summaryButton.Visibility);
        Assert.False(summaryButton.Focusable);
        Assert.Null(notice.CreateAcknowledgement());
        Assert.DoesNotContain(Descendants(notice).OfType<Button>(), button => Equals(button.Content, "发送"));
        Assert.False(notice.IsKeyboardFocusWithin);
    });

    [Fact]
    public void Notice_click_only_requests_open_and_does_not_acknowledge_or_send() => Sta(() =>
    {
        var notice = new DotTopNoticeView(); var opens = 0;
        notice.OpenRequested += (_, _) => opens++;
        notice.Display(Message("a", "Synthetic unread"), 1); Layout(notice, 224);
        Assert.Equal(0, opens);
        NoticeSummaryButton(notice).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, opens);
        Pump();
        Assert.Single(Descendants(notice).OfType<Button>());
        Assert.Null(notice.CreateAcknowledgement(singleDisplayedMessage: true));
        Assert.Empty(Descendants(notice).OfType<TextBox>());
        Assert.False(notice.IsKeyboardFocusWithin);
    });

    [Fact]
    public void Notice_summary_keeps_explicit_contrast_inside_the_system_button_template() => Sta(() =>
    {
        var notice = new DotTopNoticeView();
        notice.Display(Message("contrast", "Synthetic readable summary"), 2); Layout(notice, 224);
        var button = NoticeSummaryButton(notice);
        var summary = Assert.Single(Descendants(notice).OfType<TextBlock>(), text => text.Text == "Synthetic readable summary");
        var count = Assert.Single(Descendants(notice).OfType<TextBlock>(), text => text.Text == "2 条");
        notice.ApplyHighContrast(false);
        Assert.Equal(System.Windows.Media.Colors.WhiteSmoke, Assert.IsType<SolidColorBrush>(summary.Foreground).Color);
        Assert.Equal(summary.Foreground, button.Foreground);
        Assert.Equal(System.Windows.Media.Color.FromRgb(190, 190, 199), Assert.IsType<SolidColorBrush>(count.Foreground).Color);
        notice.ApplyHighContrast(true); Layout(notice, 224);
        Assert.Same(SystemColors.WindowTextBrush, summary.Foreground);
        Assert.Same(SystemColors.WindowTextBrush, button.Foreground);
        Assert.Same(SystemColors.WindowTextBrush, count.Foreground);
        Assert.Equal(36, notice.PreferredHeightDip);
    });

    [Theory]
    [InlineData(160, 72)] [InlineData(192, 88)] [InlineData(224, 104)] [InlineData(248, 220)]
    public void Cat_reader_has_no_input_send_or_page_entry_and_long_text_really_scrolls(double width, double availableHeight) => Sta(() =>
    {
        var view = new DotReadOnlyView();
        view.ApplyState(State(messages: [Message("long", string.Join("\n", Enumerable.Repeat("Synthetic long read-only message", 160)))])
            with { HasPendingAttempt = true, NeedsReview = true, RecoveryRequired = true });
        Assert.True(view.SetAvailableHeight(availableHeight));
        Layout(view, width);
        Assert.InRange(view.PreferredHeightDip, 72, 104);
        Assert.InRange(view.DesiredSize.Height, 1, view.PreferredHeightDip);
        Assert.True(view.MessageScroller.ScrollableHeight > 0);
        Assert.InRange(view.MessageScroller.ActualHeight, 1, 72);
        Assert.Empty(Descendants(view).OfType<TextBox>());
        var buttons = Descendants(view).OfType<Button>().ToArray();
        Assert.Equal(3, buttons.Length);
        Assert.DoesNotContain(buttons, button => Equals(button.Content, "发送") || Equals(button.Content, "网页登录"));
        var style = Assert.IsType<Style>(view.MessageScroller.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)]);
        Assert.Contains(style.Setters.OfType<Setter>(), setter => setter.Property == FrameworkElement.WidthProperty && Equals(setter.Value, 6d));
        Assert.False(view.IsKeyboardFocusWithin);
    });

    [Fact]
    public void Cat_reader_retains_current_message_when_new_unread_arrives_and_never_changes_draft() => Sta(() =>
    {
        var view = new DotReadOnlyView(); var actions = new List<DotInlineAction>();
        view.ActionRequested += actions.Add;
        view.ApplyState(State(messages: [Message("first", "Synthetic first")]) with { DraftText = "Synthetic preserved draft", ClientDraftRevision = 7 });
        view.Shown();
        view.ApplyState(State(sequence: 2, messages: [Message("first", "Synthetic first"), Message("second", "Synthetic second")])
            with { DraftText = "Synthetic preserved draft", ClientDraftRevision = 7, UnreadCount = 2 });
        Assert.Equal("first", view.SelectedMessageId);
        Layout(view, 224); view.Hidden(); Pump();
        Assert.DoesNotContain(actions, action => action.Kind is "send" or "draft" or "page" or "earlier-read");
        Assert.Empty(Descendants(view).OfType<TextBox>());
    });

    [Fact]
    public void Unshown_hidden_and_offline_reader_cannot_mark_message_read() => Sta(() =>
    {
        var view = new DotReadOnlyView(); var actions = new List<DotInlineAction>(); view.ActionRequested += actions.Add;
        view.ApplyState(State(messages: [Message("a", "Synthetic unread")]));
        Layout(view, 224); Pump();
        view.Shown(); view.Hidden(); Pump();
        view.SetOffline(); view.Shown(); Pump();
        Assert.DoesNotContain(actions, action => action.Kind == "read");
    });

    [Fact]
    public void Cat_reader_does_not_expose_preserved_white_draft_as_received_message() => Sta(() =>
    {
        var view = new DotReadOnlyView();
        view.ApplyState(State(messages: [Message("received", "Synthetic received reply")]) with
        { UnreadCount = 0, DraftText = "Synthetic private unsent draft", HasPendingAttempt = true, NeedsReview = true });
        Layout(view, 224);
        Assert.DoesNotContain(Descendants(view).OfType<TextBlock>(), text => text.Text.Contains("Synthetic private unsent draft", StringComparison.Ordinal));
        var header = Assert.Single(Descendants(view).OfType<TextBlock>(), text => text.Text == "Dot · 只读");
        Assert.Contains("草稿和发送状态已保留", Assert.IsType<string>(header.ToolTip));
        Assert.Empty(Descendants(view).OfType<TextBox>());
    });

    [Fact]
    public void Cat_reader_navigation_is_explicit_and_close_does_not_send_or_clear_unread() => Sta(() =>
    {
        var view = new DotReadOnlyView(); var actions = new List<DotInlineAction>(); var closed = 0;
        view.ActionRequested += actions.Add; view.CloseRequested += (_, _) => closed++;
        view.ApplyState(State(messages: [Message("a", "Synthetic A"), Message("b", "Synthetic B")]) with { UnreadCount = 2 });
        Layout(view, 224);
        ButtonNamed(view, "下一条消息").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("b", view.SelectedMessageId);
        ButtonNamed(view, "上一条消息").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("a", view.SelectedMessageId);
        ButtonNamed(view, "收起消息，保留未读提醒").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, closed);
        Assert.DoesNotContain(actions, action => action.Kind is "send" or "draft" or "page" or "earlier-read");
    });

    [Theory]
    [InlineData(180)] [InlineData(192)] [InlineData(220)] [InlineData(232)] [InlineData(400)]
    public void White_reply_can_limit_height_without_clipping_input_send_or_erasing_selection(double maximumHeight) => Sta(() =>
    {
        var view = new DotInlineView();
        view.ApplyState(State(messages: [Message("long", new string('文', 6000))]));
        view.InputControl.Text = "Synthetic white-mode draft"; view.InputControl.Select(2, 6);
        var revision = view.ClientDraftRevision;
        Assert.True(view.SetAvailableHeight(maximumHeight)); Layout(view, 224);
        Assert.InRange(view.PreferredHeightDip, view.MinimumRequiredHeightDip, Math.Min(maximumHeight, 232));
        Assert.InRange(view.MessageScroller.ActualHeight, 1, 72);
        Assert.InRange(view.InputControl.ActualHeight, 36, 72);
        Assert.InRange(view.SendControl.ActualHeight, 26, 28);
        Assert.Equal("Synthetic white-mode draft", view.DraftText);
        Assert.Equal(2, view.InputControl.SelectionStart); Assert.Equal(6, view.InputControl.SelectionLength);
        Assert.Equal(revision, view.ClientDraftRevision);
        view.Hidden();
        var reader = new DotReadOnlyView(); reader.ApplyState(State()); Layout(reader, 224);
        view.Shown(); Layout(view, 224);
        Assert.Equal("Synthetic white-mode draft", view.DraftText);
        Assert.Equal(2, view.InputControl.SelectionStart); Assert.Equal(6, view.InputControl.SelectionLength);
    });

    [Fact]
    public void Insufficient_space_is_rejected_without_cropping_or_mutating_views() => Sta(() =>
    {
        var reader = new DotReadOnlyView();
        Assert.False(reader.SetAvailableHeight(71)); Assert.False(reader.SetAvailableHeight(double.NaN));
        Assert.Equal(104, reader.PreferredHeightDip);
        var reply = new DotInlineView(); reply.InputControl.Text = "Synthetic safe draft";
        var naturalHeight = reply.PreferredHeightDip;
        Assert.False(reply.SetAvailableHeight(reply.MinimumRequiredHeightDip - 1));
        Assert.False(reply.SetAvailableHeight(double.PositiveInfinity));
        Assert.Equal(naturalHeight, reply.PreferredHeightDip); Assert.Equal("Synthetic safe draft", reply.DraftText);
        Assert.True(reply.SetAvailableHeight(180)); Assert.True(reply.SetAvailableHeight(232));
        Assert.Equal(naturalHeight, reply.PreferredHeightDip);
    });

    private static DotInlineState State(long sequence = 1, DotInlineMessage[]? messages = null) => new(
        "synthetic-top-session", sequence, 0, "", messages ?? [], 1, 0, "a", 1,
        true, false, false, false, true, false, "已连接 · 合成状态");
    private static DotInlineMessage Message(string id, string text) => new(id, text, DateTimeOffset.UtcNow, true);
    private static void Layout(FrameworkElement view, double width)
    {
        if (view is DotInlineView reply) reply.RecalculatePreferredHeight(width);
        view.Measure(new Size(width, view.Height)); view.Arrange(new Rect(0, 0, width, view.Height)); view.UpdateLayout();
    }
    private static Button NoticeSummaryButton(DotTopNoticeView notice)
    {
        return Assert.Single(Descendants(notice).OfType<Button>());
    }
    private static Button ButtonNamed(DependencyObject root, string name) => Assert.Single(Descendants(root).OfType<Button>(), button => AutomationProperties.GetName(button) == name);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try { action(); }
            catch (Exception error) { failure = error; }
            finally { dispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Synthetic top-view test did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
