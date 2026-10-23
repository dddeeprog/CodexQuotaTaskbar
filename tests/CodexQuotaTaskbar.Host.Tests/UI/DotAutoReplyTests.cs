using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.Provider;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

// Local WPF state only: no Start, account, WebView, injected key or real user window.
public sealed class DotAutoReplyTests
{
    [Fact]
    public void New_reply_opens_without_focus_and_ten_second_expiry_keeps_the_notification_and_connection()
    {
        WithWindow(window =>
        {
            var before = Clock(window);
            Read(window, Assistant("new-reply"));
            var after = Clock(window);
            var deadline = Deadline(window)!.Value;
            Assert.InRange(deadline, before + TimeSpan.FromSeconds(10), after + TimeSpan.FromSeconds(10));
            Assert.True(Timer(window).IsEnabled);
            Assert.Equal(Visibility.Visible, Composer(window).Visibility);
            Assert.False(Input(window).IsKeyboardFocused);
            Assert.False(window.IsActive);
            Assert.False(Page(window).IsVisible);
            Assert.True((bool)Get(window, "wanted")!);

            window.CheckAutoReplyTimeout(deadline - TimeSpan.FromTicks(1));
            Assert.Equal(Visibility.Visible, Composer(window).Visibility);
            window.CheckAutoReplyTimeout(deadline);
            Assert.Equal(Visibility.Collapsed, Composer(window).Visibility);
            Assert.Equal(112, ((ScrollViewer)window.FindName("MessageScroller")).MaxHeight);
            Assert.True((bool)Get(window, "wanted")!);
            Assert.Null(Deadline(window));
            Assert.False(Timer(window).IsEnabled);
            Assert.True(window.IsVisible);
            Assert.Equal("new-reply", ((DotMessageTracker)Get(window, "tracker")!).Latest!.Id);
            Assert.False((bool)Get(window, "closed")!);
            Assert.False(((CancellationTokenSource)Get(window, "lifetime")!).IsCancellationRequested);
            Assert.True(window.Connected);
            Assert.Null(window.Browser.CoreWebView2);
        });
    }

    [Fact]
    public void Real_dispatcher_timer_waits_ten_seconds_then_collapses_only_the_composer()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                var window = new DotChatWindow { Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
                try
                {
                    window.ApplyAnchor(Anchor(visible: true));
                    Read(window, Assistant("timer-reply"));
                    Assert.NotNull(Deadline(window));
                    await Task.Delay(TimeSpan.FromSeconds(9));
                    Assert.Equal(Visibility.Visible, Composer(window).Visibility);
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    Assert.Equal(Visibility.Collapsed, Composer(window).Visibility);
                    Assert.True(window.IsVisible);
                    Assert.True((bool)Get(window, "wanted")!);
                    Assert.False(window.IsActive);
                    Assert.False(Input(window).IsKeyboardFocused);
                    Assert.False(Page(window).IsVisible);
                    Assert.False(Timer(window).IsEnabled);
                    Assert.Null(window.Browser.CoreWebView2);
                    Assert.False(((CancellationTokenSource)Get(window, "lifetime")!).IsCancellationRequested);
                }
                catch (Exception error) { failure = error; }
                finally { window.Stop(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Local dispatcher timer check timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void History_user_rows_and_missing_timestamps_do_not_auto_open()
    {
        WithWindow(window =>
        {
            Read(window,
                Assistant("history", time: DateTimeOffset.UtcNow.AddDays(-1)),
                Assistant("user", role: "user"),
                Assistant("unknown-time", time: null));
            Assert.Equal(Visibility.Collapsed, Composer(window).Visibility);
            Assert.Null(Deadline(window));
            Assert.False(Timer(window).IsEnabled);
        });
    }

    [Fact]
    public void Repeated_reads_and_streaming_updates_neither_extend_timeout_nor_reopen_expired_composer()
    {
        WithWindow(window =>
        {
            var reply = Assistant("single-id");
            Read(window, reply);
            var deadline = Deadline(window)!.Value;
            Read(window, reply);
            Read(window, reply with { Text = "Synthetic streamed continuation." });
            Assert.Equal(deadline, Deadline(window));
            window.CheckAutoReplyTimeout(deadline);
            Read(window, reply with { Text = "Synthetic final continuation." });
            Assert.Equal(Visibility.Collapsed, Composer(window).Visibility);
            Assert.Null(Deadline(window));
            Assert.True((bool)Get(window, "wanted")!);
        });
    }

    [Fact]
    public void A_second_new_reply_replaces_the_deadline_and_the_old_expiry_cannot_close_it()
    {
        WithWindow(window =>
        {
            Read(window, Assistant("first"));
            var oldDeadline = Deadline(window)!.Value;
            Read(window, Assistant("second"));
            var currentDeadline = Deadline(window)!.Value;
            Assert.True(currentDeadline > oldDeadline);
            window.CheckAutoReplyTimeout(oldDeadline);
            Assert.Equal(Visibility.Visible, Composer(window).Visibility);
            Assert.Equal(currentDeadline, Deadline(window));
            window.CheckAutoReplyTimeout(currentDeadline);
            Assert.Equal(Visibility.Collapsed, Composer(window).Visibility);
        });
    }

    [Fact]
    public void Existing_draft_and_selection_are_preserved_and_are_not_given_an_auto_close_timer()
    {
        WithWindow(window =>
        {
            Input(window).Text = "Synthetic existing draft.";
            Input(window).Select(3, 7);
            Read(window, Assistant("new"));
            Assert.Equal(Visibility.Visible, Composer(window).Visibility);
            Assert.Null(Deadline(window));
            Assert.Equal("Synthetic existing draft.", Input(window).Text);
            Assert.Equal(3, Input(window).SelectionStart);
            Assert.Equal(7, Input(window).SelectionLength);
            window.CheckAutoReplyTimeout(Clock(window) + TimeSpan.FromMinutes(1));
            Assert.Equal(Visibility.Visible, Composer(window).Visibility);
            Assert.False(Page(window).IsVisible);
        });
    }

    [Fact]
    public void Starting_a_draft_cancels_auto_close_and_clearing_it_does_not_rearm_the_old_timer()
    {
        WithWindow(window =>
        {
            Read(window, Assistant("new"));
            var deadline = Deadline(window)!.Value;
            Input(window).Text = "Synthetic newer draft.";
            Input(window).Select(2, 4);
            Assert.Null(Deadline(window));
            window.CheckAutoReplyTimeout(deadline);
            Assert.Equal(Visibility.Visible, Composer(window).Visibility);
            Assert.Equal("Synthetic newer draft.", Input(window).Text);
            Assert.Equal(2, Input(window).SelectionStart);
            Assert.Equal(4, Input(window).SelectionLength);
            Input(window).Clear();
            window.CheckAutoReplyTimeout(deadline + TimeSpan.FromSeconds(30));
            Assert.Equal(Visibility.Visible, Composer(window).Visibility);
            Assert.Null(Deadline(window));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ime_start_or_update_cancels_auto_close_without_committing_or_sending(bool update)
    {
        WithWindow(window =>
        {
            Read(window, Assistant("new"));
            var deadline = Deadline(window)!.Value;
            RaiseComposition(window, update);
            Assert.Null(Deadline(window));
            window.CheckAutoReplyTimeout(deadline);
            Assert.Equal(Visibility.Visible, Composer(window).Visibility);
            Assert.True((bool)Get(window, "composing")!);
            Assert.Empty(Input(window).Text);
            Assert.Null(Get(window, "pendingText"));
            Assert.Null(window.Browser.CoreWebView2);
        });
    }

    [Fact]
    public void An_ime_composition_already_in_progress_prevents_arming_on_arrival()
    {
        WithWindow(window =>
        {
            RaiseComposition(window, update: false);
            Read(window, Assistant("new"));
            Assert.Equal(Visibility.Visible, Composer(window).Visibility);
            Assert.Null(Deadline(window));
            window.CheckAutoReplyTimeout(Clock(window) + TimeSpan.FromMinutes(1));
            Assert.Equal(Visibility.Visible, Composer(window).Visibility);
            Assert.True((bool)Get(window, "composing")!);
        });
    }

    [Theory]
    [InlineData("enter")]
    [InlineData("down")]
    [InlineData("wheel")]
    public void Mouse_interaction_with_the_composer_cancels_automatic_expiry(string interaction)
    {
        WithWindow(window =>
        {
            Read(window, Assistant("new"));
            var deadline = Deadline(window)!.Value;
            if (interaction == "enter")
                Composer(window).RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                    { RoutedEvent = Mouse.MouseEnterEvent });
            else if (interaction == "down")
                Composer(window).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = Mouse.PreviewMouseDownEvent });
            else
                Composer(window).RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            Assert.Null(Deadline(window));
            window.CheckAutoReplyTimeout(deadline);
            Assert.Equal(Visibility.Visible, Composer(window).Visibility);
        });
    }

    [Fact]
    public void Keyboard_interaction_cancels_expiry_without_injecting_a_key_into_any_window()
    {
        WithWindow(window =>
        {
            Read(window, Assistant("new"));
            var deadline = Deadline(window)!.Value;
            var source = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle());
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Left)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            window.HandleInputKeyAsync(key, ModifierKeys.None).GetAwaiter().GetResult();
            Assert.Null(Deadline(window));
            window.CheckAutoReplyTimeout(deadline);
            Assert.Equal(Visibility.Visible, Composer(window).Visibility);
            Assert.Null(Get(window, "pendingText"));
        });
    }

    [Fact]
    public void Manual_reply_cancels_an_old_timer_and_new_replies_do_not_time_out_a_manual_composer()
    {
        WithWindow(window =>
        {
            InitializeHiddenNative(window);
            Read(window, Assistant("first"));
            var deadline = Deadline(window)!.Value;
            // Keep Reply's intentional Activate/Focus calls on a hidden, test-owned window.
            window.ApplyAnchor(Anchor(visible: false));
            window.Reply();
            Assert.Null(Deadline(window));
            Read(window, Assistant("second"));
            Assert.Null(Deadline(window));
            window.CheckAutoReplyTimeout(deadline + TimeSpan.FromMinutes(1));
            Assert.Equal(Visibility.Visible, Composer(window).Visibility);
            Assert.False(Page(window).IsVisible);
            window.ApplyAnchor(Anchor(visible: true));
            Assert.Null(Deadline(window));
        });
    }

    [Fact]
    public void Explicit_dismiss_cancels_timeout_and_same_message_updates_cannot_reopen_it()
    {
        WithWindow(window =>
        {
            InitializeHiddenNative(window);
            var reply = Assistant("new");
            Read(window, reply);
            var deadline = Deadline(window)!.Value;
            window.Close();
            Assert.Null(Deadline(window));
            Assert.False((bool)Get(window, "closed")!);
            Assert.False((bool)Get(window, "wanted")!);
            window.CheckAutoReplyTimeout(deadline);
            Read(window, reply with { Text = "Synthetic update after dismiss." });
            Assert.Equal(Visibility.Collapsed, Composer(window).Visibility);
            Assert.Null(Deadline(window));
            Assert.False((bool)Get(window, "wanted")!);
        });
    }

    [Fact]
    public void A_receipt_and_new_reply_in_the_same_read_clear_only_the_sent_revision_then_auto_expand()
    {
        WithWindow(window =>
        {
            const string sent = "Synthetic pending submission.";
            Input(window).Text = sent;
            Set(window, "pendingText", sent);
            Set(window, "pendingSince", DateTime.UtcNow);
            Set(window, "pendingDraftRevision", Get(window, "draftRevision"));
            Set(window, "pendingBaselineComplete", true);
            ((HashSet<string>)Get(window, "beforeSend")!).Add("before");
            Read(window, Assistant("receipt", role: "user") with { Text = sent }, Assistant("response"));
            Assert.Equal("confirmed", Get(window, "attemptState"));
            Assert.Null(Get(window, "pendingText"));
            Assert.Empty(Input(window).Text);
            Assert.Equal(Visibility.Visible, Composer(window).Visibility);
            Assert.NotNull(Deadline(window));
            Assert.False(Page(window).IsVisible);
        });
    }

    [Fact]
    public void Stopping_a_test_owned_window_removes_the_timer_without_creating_a_browser()
    {
        WithWindow(window =>
        {
            Read(window, Assistant("new"));
            Assert.True(Timer(window).IsEnabled);
            Assert.Null(window.Browser.CoreWebView2);
            window.Stop();
            Assert.Null(Deadline(window));
            Assert.False(Timer(window).IsEnabled);
            Assert.True((bool)Get(window, "closed")!);
            Assert.True(((CancellationTokenSource)Get(window, "lifetime")!).IsCancellationRequested);
        });
    }

    [Fact]
    public void Hidden_anchor_waits_for_actual_presentation_before_the_ten_second_countdown()
    {
        WithWindow(window =>
        {
            window.ApplyAnchor(Anchor(visible: false));
            Read(window, Assistant("new"));
            Assert.Equal(Visibility.Visible, Composer(window).Visibility);
            Assert.False(window.IsVisible);
            Assert.Null(Deadline(window));
            Assert.True((bool)Get(window, "autoReplyAwaitingPresentation")!);
            Assert.False(Timer(window).IsEnabled);
            window.CheckAutoReplyTimeout(Clock(window) + TimeSpan.FromHours(1));
            Assert.Equal(Visibility.Visible, Composer(window).Visibility);
            var before = Clock(window);
            window.ApplyAnchor(Anchor(visible: true));
            var after = Clock(window);
            Assert.True(window.IsVisible);
            Assert.InRange(Deadline(window)!.Value, before + TimeSpan.FromSeconds(10), after + TimeSpan.FromSeconds(10));
            Assert.False((bool)Get(window, "autoReplyAwaitingPresentation")!);
            Assert.False(window.IsActive);
            Assert.False(Input(window).IsKeyboardFocused);
        });
    }

    [Fact]
    public void A_new_reply_while_hidden_removes_an_old_visible_deadline_until_presentation()
    {
        WithWindow(window =>
        {
            Read(window, Assistant("visible-first"));
            var oldDeadline = Deadline(window)!.Value;
            window.ApplyAnchor(Anchor(visible: false));
            Read(window, Assistant("hidden-second"));
            Assert.False(window.IsVisible);
            Assert.Null(Deadline(window));
            Assert.True((bool)Get(window, "autoReplyAwaitingPresentation")!);
            Assert.False(Timer(window).IsEnabled);
            window.CheckAutoReplyTimeout(oldDeadline + TimeSpan.FromMinutes(1));
            Assert.Equal(Visibility.Visible, Composer(window).Visibility);
            window.ApplyAnchor(Anchor(visible: true));
            Assert.True(window.IsVisible);
            Assert.NotNull(Deadline(window));
            Assert.True(Deadline(window) > oldDeadline);
        });
    }

    private static DotMessage Assistant(string id, string role = "assistant") =>
        new(id, role, "Synthetic local reply.", DateTimeOffset.UtcNow.AddSeconds(1), true,
            "article", "message-row", "message-body");

    private static DotMessage Assistant(string id, DateTimeOffset? time, string role = "assistant") =>
        Assistant(id, role) with { Time = time };

    private static void Read(DotChatWindow window, params DotMessage[] messages)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            state = "ready", draftEmpty = true,
            messages = messages.Select(message => new
            {
                id = message.Id, role = message.Role, text = message.Text,
                time = message.Time?.ToString("o") ?? "", confirmable = message.Confirmable,
                idSource = message.IdSource, roleSource = message.RoleSource, textSource = message.TextSource
            }),
            diagnostics = new { baselineComplete = true, totalArticles = messages.Length, returnedMessages = messages.Length }
        }));
        window.ApplyReadResult(json.RootElement);
    }

    private static void RaiseComposition(DotChatWindow window, bool update)
    {
        var composition = new TextComposition(InputManager.Current, Input(window), "Synthetic IME", TextCompositionAutoComplete.Off);
        Input(window).RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
        {
            RoutedEvent = update ? TextCompositionManager.PreviewTextInputUpdateEvent : TextCompositionManager.PreviewTextInputStartEvent
        });
    }

    private static void InitializeHiddenNative(DotChatWindow window)
    {
        // Reply() requires a previously shown HWND; this transparent, nonactivating window is test-owned.
        window.Show(); window.Hide();
    }

    private static CodexQuotaTaskbar.Host.Lifetime.DotAnchor Anchor(bool visible) =>
        new(-10000, -10000, -9944, -12000, -12000, -8000, -8000, 96, visible, 0);

    private static TextBox Input(DotChatWindow window) => (TextBox)window.FindName("Input");
    private static StackPanel Composer(DotChatWindow window) => (StackPanel)window.FindName("Composer");
    private static Window Page(DotChatWindow window) => (Window)Get(window, "pageWindow")!;
    private static DispatcherTimer Timer(DotChatWindow window) => (DispatcherTimer)Get(window, "autoReplyTimer")!;
    private static TimeSpan? Deadline(DotChatWindow window) => (TimeSpan?)Get(window, "autoReplyDeadline");
    private static TimeSpan Clock(DotChatWindow window) => ((Stopwatch)Get(window, "opened")!).Elapsed;
    private static object? Get(DotChatWindow window, string field) =>
        typeof(DotChatWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
    private static void Set(DotChatWindow window, string field, object? value) =>
        typeof(DotChatWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);

    private static void WithWindow(Action<DotChatWindow> test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            DotChatWindow? window = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                window = new DotChatWindow { Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
                // Synthetic offscreen work area and full transparency: never obscure a user's app.
                window.ApplyAnchor(Anchor(visible: true));
                test(window);
            }
            catch (Exception error) { failure = error; }
            finally { window?.Stop(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Local automatic reply check timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
