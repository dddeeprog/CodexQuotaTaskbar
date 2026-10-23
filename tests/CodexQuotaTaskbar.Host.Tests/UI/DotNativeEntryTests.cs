using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

// Offline regression tests: never call Start(), SendAsync(), or a real input API.
public sealed class DotNativeEntryTests
{
    [Theory]
    [InlineData(false, true, false, false, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(true, false, false, true, false)]
    [InlineData(false, true, true, false, false)]
    [InlineData(false, true, false, true, false)]
    [InlineData(false, true, false, false, true)]
    public void Message_click_opens_native_reply_in_every_connection_state(
        bool attention, bool ready, bool busy, bool needsReview, bool pending)
    {
        VerifyNativeEntry(attention, ready, busy, needsReview, pending, useCommand: false);
    }

    [Theory]
    [InlineData(false, true, false, false, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(true, false, false, true, false)]
    [InlineData(false, true, true, false, false)]
    [InlineData(false, true, false, true, false)]
    [InlineData(false, true, false, false, true)]
    public void Reply_command_opens_native_reply_without_waiting_for_connection(
        bool attention, bool ready, bool busy, bool needsReview, bool pending)
    {
        // Requires extracting the existing pipe-dispatch lambda into private HandleCommand(DotCommand?).
        VerifyNativeEntry(attention, ready, busy, needsReview, pending, useCommand: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_reply_hides_an_already_visible_page_without_clearing_send_review(bool useCommand)
    {
        VerifyNativeEntry(attention: true, ready: false, busy: false, needsReview: true,
            pending: false, useCommand: useCommand, pageWasVisible: true);
    }

    private static void VerifyNativeEntry(bool attention, bool ready, bool busy, bool needsReview,
        bool pending, bool useCommand, bool pageWasVisible = false)
    {
        RunSta(async () =>
        {
            var window = new DotChatWindow();
            try
            {
                var anchor = new DotAnchor(640, 800, 880, 0, 0, 1920, 1040, 96, true, 0);
                window.ApplyAnchor(anchor);
                var input = (TextBox)window.FindName("Input");
                var composer = (StackPanel)window.FindName("Composer");
                var page = (Window)GetField(window, "pageWindow")!;
                const string draft = "保留这段未发送草稿";
                input.Text = draft;
                input.Select(2, 3);
                if (attention) InvokePrivate(window, "NotifyProblem", "离线模拟：Dot 需要登录或检查发送状态。");
                else InvokePrivate(window, "Present");
                SetField(window, "ready", ready);
                SetField(window, "busy", busy);
                SetField(window, "needsReview", needsReview);
                SetField(window, "pendingText", pending ? "An earlier message is awaiting confirmation." : null);
                InvokePrivate(window, "UpdateSend");
                var sendingWasEnabled = ((Button)window.FindName("SendButton")).IsEnabled;
                var pendingBefore = GetField(window, "pendingText");

                Assert.True(window.IsVisible);
                Assert.Equal(Visibility.Collapsed, composer.Visibility);
                Assert.False(page.IsVisible);
                Assert.Null(window.Browser.CoreWebView2);
                if (pageWasVisible)
                {
                    // Show only an empty native host. Remove the WebView from its visual tree first;
                    // the test never initializes or navigates a browser.
                    page.Content = new TextBlock { Text = "Offline page-host visibility fixture" };
                    page.ShowInTaskbar = true;
                    page.Show();
                    page.Activate();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.True(page.IsVisible);
                    Assert.Null(window.Browser.CoreWebView2);
                }

                if (useCommand) InvokePrivate(window, "HandleCommand", new DotCommand("reply", anchor));
                else ((Button)window.FindName("MessageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                Assert.Equal(Visibility.Visible, composer.Visibility);
                Assert.True(input.IsKeyboardFocused);
                Assert.False(page.IsVisible);
                Assert.False(page.ShowInTaskbar);
                Assert.Null(window.Browser.CoreWebView2);
                Assert.Equal(draft, input.Text);
                Assert.Equal(2, input.SelectionStart);
                Assert.Equal(3, input.SelectionLength);
                Assert.Equal(pendingBefore, GetField(window, "pendingText"));
                Assert.Equal(ready, (bool)GetField(window, "ready")!);
                Assert.Equal(busy, (bool)GetField(window, "busy")!);
                Assert.Equal(needsReview, (bool)GetField(window, "needsReview")!);
                Assert.Equal(sendingWasEnabled, ((Button)window.FindName("SendButton")).IsEnabled);

                window.Close(); // Dismiss, not Stop: must retain the unsent native draft.
                Assert.False(window.IsVisible);
                Assert.Equal(draft, input.Text);
                Assert.False(page.IsVisible);
            }
            finally { window.Stop(); }
        });
    }

    private static object? GetField(DotChatWindow window, string name) =>
        typeof(DotChatWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);

    private static void SetField(DotChatWindow window, string name, object? value) =>
        typeof(DotChatWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);

    private static void InvokePrivate(DotChatWindow window, string name, params object?[] arguments)
    {
        var method = typeof(DotChatWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        try { method.Invoke(window, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
    }

    private static void RunSta(Func<Task> test)
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
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Offline native entry test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
