using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.UI;
using Button = System.Windows.Controls.Button;

namespace CodexQuotaTaskbar.Host.Tests.UI;

[Collection("Native UI isolation")]
public sealed class DotInlineViewTests
{
    [Theory]
    [InlineData(192)] [InlineData(224)] [InlineData(248)]
    public void Long_message_and_draft_have_bounded_native_layout(double width) => Sta(() =>
    {
        var view = new DotInlineView();
        view.ApplyState(State(messages: [Message("a", new string('文', 6000))]) with { DraftText = new string('草', 8000) });
        view.RecalculatePreferredHeight(width);
        view.Measure(new Size(width, view.PreferredHeightDip));
        view.Arrange(new Rect(0, 0, width, view.PreferredHeightDip)); view.UpdateLayout();
        Assert.Equal(232, view.PreferredHeightDip);
        Assert.InRange(view.DesiredSize.Height, 0, view.PreferredHeightDip);
        Assert.InRange(view.MessageScroller.ActualHeight, 1, 72);
        Assert.InRange(view.InputControl.ActualHeight, 36, 72);
        Assert.Equal(8000, view.InputControl.MaxLength);
    });

    [Fact]
    public void Stale_snapshots_and_hiding_cannot_overwrite_new_draft_or_selection() => Sta(() =>
    {
        var view = new DotInlineView(); view.ApplyState(State());
        var actions = new List<DotInlineAction>(); view.ActionRequested += actions.Add;
        view.InputControl.Text = "Synthetic newer draft"; view.InputControl.Select(3, 7);
        var revision = view.ClientDraftRevision;
        view.ApplyState(State(sequence: 2) with { DraftText = "older value", ClientDraftRevision = revision - 1 });
        view.Hidden(); view.Shown();
        Assert.Equal("Synthetic newer draft", view.DraftText);
        Assert.Equal(3, view.InputControl.SelectionStart); Assert.Equal(7, view.InputControl.SelectionLength);
        Assert.Contains(actions, action => action.Kind == "draft" && action.DraftRevision == revision);
        Assert.DoesNotContain(actions, action => action.Kind == "send" || action.Kind == "read");
    });

    [Fact]
    public void Matching_receipt_clears_only_the_same_revision() => Sta(() =>
    {
        var view = new DotInlineView(); view.ApplyState(State()); view.InputControl.Text = "Synthetic sent draft";
        var sentRevision = view.ClientDraftRevision;
        view.ApplyState(State(sequence: 2) with { ClientDraftRevision = sentRevision, DraftText = "" });
        Assert.Equal("", view.DraftText);
        view.InputControl.Text = "Synthetic sent draft";
        view.ApplyState(State(sequence: 3) with { ClientDraftRevision = sentRevision, DraftText = "" });
        Assert.Equal("Synthetic sent draft", view.DraftText);
    });

    [Fact]
    public void Composition_start_advances_revision_before_any_text_change_and_old_receipt_is_safe() => Sta(() =>
    {
        var view = new DotInlineView(); view.ApplyState(State()); view.InputControl.Text = "Synthetic draft";
        var old = view.ClientDraftRevision;
        var actions = new List<DotInlineAction>(); view.ActionRequested += actions.Add;
        var composition = new TextComposition(InputManager.Current, view.InputControl, "", TextCompositionAutoComplete.Off);
        view.InputControl.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
        { RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent });
        Assert.True(view.IsComposing); Assert.True(view.ClientDraftRevision > old);
        view.ApplyState(State(sequence: 2) with { ClientDraftRevision = old, DraftText = "" });
        Assert.Equal("Synthetic draft", view.DraftText);
        Assert.Contains(actions, action => action.Kind == "draft" && action.Text == "Synthetic draft" && action.DraftRevision > old);
    });

    [Fact]
    public void Reconnect_resynchronizes_current_draft_but_never_replays_a_send() => Sta(() =>
    {
        var view = new DotInlineView(); view.ApplyState(State()); view.InputControl.Text = "Synthetic offline draft";
        view.InputControl.Select(2, 5);
        var actions = new List<DotInlineAction>(); view.ActionRequested += actions.Add;
        view.SetOffline(); view.ApplyState(State() with { SessionId = "new-synthetic-session", DraftText = "" });
        Assert.Equal("Synthetic offline draft", view.DraftText);
        Assert.Equal(2, view.InputControl.SelectionStart); Assert.Equal(5, view.InputControl.SelectionLength);
        Assert.Single(actions, action => action.Kind == "draft");
        Assert.DoesNotContain(actions, action => action.Kind == "send");
    });

    [Fact]
    public void New_messages_do_not_replace_the_current_reading_target() => Sta(() =>
    {
        var view = new DotInlineView();
        view.ApplyState(State(messages: [Message("a", "Synthetic first message")])); view.Shown();
        view.ApplyState(State(sequence: 2, messages: [Message("a", "Synthetic first message"), Message("b", "Synthetic second message")]));
        Assert.Equal("a", view.SelectedMessageId);
        Assert.True(view.MessageScroller.Focusable); // User can Tab/click here for keyboard scrolling; ApplyState never focuses it.
    });

    [Fact]
    public void Unshown_or_hidden_view_does_not_mark_messages_read() => Sta(() =>
    {
        var view = new DotInlineView(); var actions = new List<DotInlineAction>(); view.ActionRequested += actions.Add;
        view.ApplyState(State(messages: [Message("a", "Synthetic unread")]));
        Layout(view, 224);
        view.Shown(); view.Hidden();
        Drain();
        Assert.DoesNotContain(actions, action => action.Kind == "read");
    });

    [Theory]
    [InlineData(ModifierKeys.None, true, 1)]
    [InlineData(ModifierKeys.Shift, false, 0)]
    [InlineData(ModifierKeys.Control, false, 0)]
    public void Enter_sends_once_and_modified_enter_is_left_to_textbox(ModifierKeys modifiers, bool handled, int count) => Sta(() =>
    {
        var view = new DotInlineView(); var actions = new List<DotInlineAction>(); view.ActionRequested += actions.Add;
        view.ApplyState(State()); view.InputControl.Text = "Synthetic Enter test";
        var host = new Window { Content = view, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            var key = Key(host, System.Windows.Input.Key.Enter);
            view.HandleInputKeyAsync(key, modifiers).GetAwaiter().GetResult();
            view.HandleInputKeyAsync(Key(host, System.Windows.Input.Key.Enter), modifiers).GetAwaiter().GetResult();
            Assert.Equal(handled, key.Handled);
            Assert.Equal(count, actions.Count(action => action.Kind == "send"));
            foreach (var send in actions.Where(action => action.Kind == "send")) Assert.True(Guid.TryParseExact(send.RequestId, "N", out _));
        }
        finally { host.Close(); }
    });

    [Fact]
    public void Ime_confirmation_enter_never_sends() => Sta(() =>
    {
        var view = new DotInlineView(); view.ApplyState(State()); view.InputControl.Text = "Synthetic composition";
        var actions = new List<DotInlineAction>(); view.ActionRequested += actions.Add;
        var host = new Window { Content = view, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            var composition = new TextComposition(InputManager.Current, view.InputControl, "拼音", TextCompositionAutoComplete.Off);
            view.InputControl.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
            { RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent });
            var key = Key(host, System.Windows.Input.Key.Enter);
            view.HandleInputKeyAsync(key, ModifierKeys.None).GetAwaiter().GetResult();
            Assert.False(key.Handled); Assert.DoesNotContain(actions, action => action.Kind == "send");
            Assert.Equal("Synthetic composition", view.DraftText);
        }
        finally { host.Close(); }
    });

    [Fact]
    public void Pending_review_and_offline_states_disable_send_but_keep_editable_draft() => Sta(() =>
    {
        foreach (var blocked in new[] { State() with { HasPendingAttempt = true }, State() with { NeedsReview = true }, State() with { Online = false }, State() with { ReceiptStructureReady = false } })
        {
            var view = new DotInlineView(); view.ApplyState(blocked); view.InputControl.Text = "Synthetic preserved draft";
            Assert.False(view.SendControl.IsEnabled); Assert.False(view.InputControl.IsReadOnly);
            Assert.Equal("Synthetic preserved draft", view.DraftText);
        }
    });

    [Fact]
    public void Definitive_not_dispatched_ack_releases_click_latch_without_clearing_draft() => Sta(() =>
    {
        var view = new DotInlineView(); var actions = new List<DotInlineAction>(); view.ActionRequested += actions.Add;
        view.ApplyState(State()); view.InputControl.Text = "Synthetic rejected send";
        view.SendControl.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var sent = Assert.Single(actions, action => action.Kind == "send");
        Assert.False(view.SendControl.IsEnabled);
        view.ApplyState(State(sequence: 2) with { ClientDraftRevision = view.ClientDraftRevision, DraftText = view.DraftText,
            LastRequestId = sent.RequestId, AttemptState = "not_dispatched" });
        Assert.True(view.SendControl.IsEnabled); Assert.Equal("Synthetic rejected send", view.DraftText);
        Assert.Single(actions, action => action.Kind == "send");
    });

    [Fact]
    public void Earlier_unread_confirmation_cannot_include_overflow_arriving_after_page_open() => Sta(() =>
    {
        var view = new DotInlineView(); var actions = new List<DotInlineAction>(); view.ActionRequested += actions.Add;
        view.ApplyState(State() with { EarlierUnreadCount = 2, UnreadCount = 2 });
        Invoke(view, "EarlierMessages");
        Assert.Single(actions, action => action.Kind == "page");
        Assert.DoesNotContain(actions, action => action.Kind == "earlier-read");
        view.ApplyState(State(sequence: 2) with { PageVisible = true, EarlierUnreadCount = 2, UnreadCount = 2 });
        view.ApplyState(State(sequence: 3) with { PageVisible = false, EarlierUnreadCount = 5, UnreadCount = 5 });
        Invoke(view, "EarlierMessages");
        Assert.Equal(2, Assert.Single(actions, action => action.Kind == "earlier-read").Count);
    });

    [Fact]
    public void Unknown_recovery_choices_require_manual_page_review_and_never_send() => Sta(() =>
    {
        var view = new DotInlineView(); var actions = new List<DotInlineAction>(); view.ActionRequested += actions.Add;
        view.ApplyState(State() with { RecoveryRequired = true, NeedsReview = true });
        view.InputControl.Text = "Synthetic newer recovery draft";
        Invoke(view, "EmitReview", "reviewed_sent");
        Assert.DoesNotContain(actions, action => action.Kind == "reviewed_sent");
        view.ApplyState(State(sequence: 2) with { RecoveryRequired = true, RecoveryCanReview = true, NeedsReview = true,
            ClientDraftRevision = view.ClientDraftRevision, DraftText = view.DraftText });
        Invoke(view, "EmitReview", "reviewed_not_sent");
        Assert.Single(actions, action => action.Kind == "reviewed_not_sent");
        Assert.DoesNotContain(actions, action => action.Kind == "send");
        Assert.Equal("Synthetic newer recovery draft", view.DraftText);
    });

    [Fact]
    public void Notification_is_small_hidden_nonactivating_and_contains_no_composer() => Sta(() =>
    {
        var window = new DotNotificationWindow();
        try
        {
            var opened = 0; window.OpenRequested += (_, _) => opened++;
            window.Display(Message("a", new string('文', 5000)), 3);
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(240, 80)); content.Arrange(new Rect(0, 0, 240, 80)); content.UpdateLayout();
            Assert.False(window.IsVisible); Assert.False(window.ShowActivated); Assert.False(window.ShowInTaskbar);
            Assert.Equal(240, window.PreferredWidthDip); Assert.Equal(80, window.PreferredHeightDip);
            Assert.InRange(content.DesiredSize.Height, 0, 80); Assert.Equal(0, opened);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Long_message_has_a_local_dark_thin_scrollbar_and_preserves_a_real_scroll_range() => Sta(() =>
    {
        var view = new DotInlineView();
        view.ApplyState(State(messages: [Message("a", string.Join("\n", Enumerable.Repeat("Synthetic long message line", 50)))]));
        Layout(view, 224);
        Assert.True(view.MessageScroller.ScrollableHeight > 70);
        Assert.True(view.MessageScroller.Focusable);
        Assert.Equal(PanningMode.VerticalOnly, view.MessageScroller.PanningMode);
        Assert.Equal(ScrollBarVisibility.Auto, view.MessageScroller.VerticalScrollBarVisibility);
        var style = Assert.IsType<Style>(view.MessageScroller.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)]);
        Assert.Equal(typeof(System.Windows.Controls.Primitives.ScrollBar), style.TargetType);
        Assert.Contains(style.Setters.OfType<Setter>(), setter => setter.Property == FrameworkElement.WidthProperty && (double)setter.Value == 6);
        view.MessageScroller.ScrollToBottom(); view.UpdateLayout(); Drain();
        Assert.True(view.MessageScroller.VerticalOffset > 0);
        Assert.InRange(view.MessageScroller.VerticalOffset, view.MessageScroller.ScrollableHeight - 1, view.MessageScroller.ScrollableHeight + 1);
    });

    [Theory]
    [InlineData(192)] [InlineData(224)]
    public void Explicit_recovery_choices_fit_the_same_bounded_footer(double width) => Sta(() =>
    {
        var view = new DotInlineView();
        view.ApplyState(State() with { RecoveryRequired = true, RecoveryCanReview = true, NeedsReview = true });
        Layout(view, width);
        var choices = (Grid)view.GetType().GetField("reviewActions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(view)!;
        Assert.Equal(Visibility.Visible, choices.Visibility);
        Assert.Equal(2, choices.Children.Count);
        Assert.All(choices.Children.OfType<Button>(), button =>
        {
            Assert.True(button.IsEnabled); Assert.InRange(button.ActualWidth, 1, width / 2);
            var top = button.TransformToAncestor(view).Transform(new Point());
            Assert.InRange(top.X, 0, width); Assert.InRange(top.Y + button.ActualHeight, 0, view.PreferredHeightDip);
        });
    });

    private static void Layout(DotInlineView view, double width)
    {
        view.RecalculatePreferredHeight(width);
        view.Measure(new Size(width, view.PreferredHeightDip));
        view.Arrange(new Rect(0, 0, width, view.PreferredHeightDip)); view.UpdateLayout();
    }

    private static DotInlineMessage Message(string id, string text) => new(id, text, DateTimeOffset.UnixEpoch, true);
    private static DotInlineState State(long sequence = 1, DotInlineMessage[]? messages = null) => new(
        "synthetic-session", sequence, 0, "", messages ?? [], messages?.Count(message => message.Unread) ?? 0,
        0, null, 0, true, false, false, false, true, false, "已连接");
    private static void Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name,
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(target, args);
    private static KeyEventArgs Key(Window host, System.Windows.Input.Key key) => new(Keyboard.PrimaryDevice,
        HwndSource.FromHwnd(new WindowInteropHelper(host).EnsureHandle()), Environment.TickCount, key)
    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
    private static void Drain()
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
            try { action(); }
            catch (Exception error) { failure = error; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Synthetic inline view proof timed out");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
