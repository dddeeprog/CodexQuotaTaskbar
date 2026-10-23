using System.Runtime.ExceptionServices;
using System.Windows.Input;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.Provider;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class DotChatRuntimeFactAttribute : FactAttribute
{
    public DotChatRuntimeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CQT_DOT_RUNTIME_TEST") != "1") Skip = "Opt-in official Dot webpage test.";
    }
}

public sealed class DotChatWindowTests
{
    [Fact]
    public void Bubble_is_native_hidden_at_start_and_stops_without_loading_a_browser()
    {
        RunSta(async () =>
        {
            var window = new DotChatWindow();
            Assert.Equal(OverlayTypography.Interface, window.FontFamily);
            Assert.False(((Button)window.FindName("SendButton")).IsEnabled);
            Assert.False(window.IsVisible);
            Assert.Null(window.Browser.CoreWebView2);
            window.Stop(); window.Stop();
            await Task.CompletedTask;
        });
    }

    [Fact]
    public void Clicking_message_opens_composer_and_preserves_draft_and_selection_without_a_browser()
    {
        RunSta(async () =>
        {
            var window = new DotChatWindow();
            try
            {
                window.ApplyAnchor(new(640, 800, 880, 0, 0, 1920, 1040, 96, true, 0));
                var input = (TextBox)window.FindName("Input");
                input.Text = "保留未发送草稿"; input.Select(2, 3);
                Assert.False(((ScrollViewer)window.FindName("MessageScroller")).Focusable);
                ((Button)window.FindName("MessageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(Visibility.Visible, ((StackPanel)window.FindName("Composer")).Visibility);
                Assert.True(input.IsKeyboardFocused);
                Assert.Equal("保留未发送草稿", input.Text);
                Assert.Equal(2, input.SelectionStart); Assert.Equal(3, input.SelectionLength);
                window.Close();
                Assert.Equal("保留未发送草稿", input.Text);
                Assert.Null(window.Browser.CoreWebView2);
            }
            finally { window.Stop(); }
        });
    }

    [Theory]
    [InlineData(ModifierKeys.None, true)]
    [InlineData(ModifierKeys.Shift, false)]
    [InlineData(ModifierKeys.Control, false)]
    [InlineData(ModifierKeys.Alt, false)]
    public void Plain_enter_uses_send_path_and_modified_enter_remains_available_to_textbox(ModifierKeys modifiers, bool handled)
    {
        RunSta(async () =>
        {
            var window = new DotChatWindow();
            try
            {
                var input = (TextBox)window.FindName("Input");
                input.Text = "未连接时也保留草稿";
                var key = NewKey(window, Key.Enter);
                await window.HandleInputKeyAsync(key, modifiers);
                Assert.Equal(handled, key.Handled);
                Assert.Equal("未连接时也保留草稿", input.Text);
                Assert.Null(window.Browser.CoreWebView2);
            }
            finally { window.Stop(); }
        });
    }

    [Fact]
    public void Ime_enter_and_composition_completion_cannot_use_send_path_until_key_is_released()
    {
        RunSta(async () =>
        {
            var window = new DotChatWindow();
            try
            {
                var input = (TextBox)window.FindName("Input");
                var composition = new TextComposition(InputManager.Current, input, "拼音", TextCompositionAutoComplete.Off);
                input.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
                    { RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent });
                input.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
                    { RoutedEvent = TextCompositionManager.PreviewTextInputUpdateEvent });
                var confirming = NewKey(window, Key.Enter);
                await window.HandleInputKeyAsync(confirming, ModifierKeys.None);
                Assert.False(confirming.Handled);
                input.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice,
                    new TextComposition(InputManager.Current, input, "拼音", TextCompositionAutoComplete.Off))
                    { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });
                var sameTurn = NewKey(window, Key.Enter);
                await window.HandleInputKeyAsync(sameTurn, ModifierKeys.None);
                Assert.False(sameTurn.Handled);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                // A held confirmation key must stay blocked after composition completion.
                var held = NewKey(window, Key.Enter);
                await window.HandleInputKeyAsync(held, ModifierKeys.None);
                Assert.True(held.Handled);
                var released = NewKey(window, Key.Enter); released.RoutedEvent = Keyboard.PreviewKeyUpEvent;
                input.RaiseEvent(released);
                var processed = NewKey(window, Key.ImeProcessed);
                await window.HandleInputKeyAsync(processed, ModifierKeys.None);
                Assert.False(processed.Handled);
                var plain = NewKey(window, Key.Enter);
                await window.HandleInputKeyAsync(plain, ModifierKeys.None);
                Assert.True(plain.Handled);
                var normalText = new TextComposition(InputManager.Current, input, "a", TextCompositionAutoComplete.On);
                input.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, normalText)
                    { RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent });
                input.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, normalText)
                    { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });
                var afterNormalText = NewKey(window, Key.Enter);
                await window.HandleInputKeyAsync(afterNormalText, ModifierKeys.None);
                Assert.True(afterNormalText.Handled);
                Assert.Null(window.Browser.CoreWebView2);
            }
            finally { window.Stop(); }
        });
    }

    [Theory]
    [InlineData(960, 900, 96)]
    [InlineData(4, 4, 144)]
    [InlineData(1918, 1078, 192)]
    public void Bubble_stays_in_work_area(int x, int y, double dpi)
    {
        var anchor = new DotAnchor(x, y, y + 56, 0, 0, 1920, 1080, dpi, true, 0);
        var result = DotChatWindow.CalculateBounds(anchor, 320, 260);
        Assert.True(result.Left >= 8 && result.Top >= 8 && result.Right <= 1912 && result.Bottom <= 1072);
        if (y > result.Height + 12) Assert.True(result.Bottom <= y);
    }

    [Fact]
    public void New_message_tracker_skips_history_user_messages_missing_timestamps_and_dismissed_updates()
    {
        var now = DateTimeOffset.UtcNow;
        var tracker = new DotMessageTracker(now);
        Assert.Null(tracker.Observe([new("old", "assistant", "旧消息", now.AddSeconds(-1)), new("u", "user", "用户消息", now), new("missing", "assistant", "不猜时间", null)]));
        var first = new DotMessage("a", "assistant", "新消息", now.AddSeconds(1));
        Assert.Equal(first, tracker.Observe([first]));
        Assert.Null(tracker.Observe([first]));
        Assert.Equal("新消息继续", tracker.Observe([first with { Text = "新消息继续" }])!.Text);
        tracker.Dismiss();
        Assert.Null(tracker.Observe([first with { Text = "收起后继续流式输出" }]));
        Assert.NotNull(tracker.Observe([new("b", "assistant", "另一条新消息", now.AddSeconds(2))]));
    }

    [Fact]
    public void Negative_monitor_coordinates_are_supported()
    {
        var anchor = new DotAnchor(-600, 700, 780, -1920, 0, 0, 1040, 144, true, 0);
        var result = DotChatWindow.CalculateBounds(anchor, 320, 180);
        Assert.Equal(-600, result.Left + result.Width / 2);
        Assert.True(result.Bottom < 700);
    }

    [DotChatRuntimeFact]
    public void Hidden_official_page_receives_new_message_in_bubble_and_clears_confirmed_draft()
    {
        RunSta(async () =>
        {
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            var window = new DotChatWindow();
            try
            {
                window.ApplyAnchor(new(640, 800, 880, 0, 0, 1920, 1040, 96, true, 0));
                window.Start();
                for (var i = 0; i < 120 && !window.Connected; i++) await Task.Delay(500);
                Assert.True(window.Connected, ((TextBlock)window.FindName("MessageText")).Text);
                Assert.False(window.IsVisible); // No old history pop-up on connection.
                if (Environment.GetEnvironmentVariable("CQT_DOT_SEND_TEST") == "1")
                {
                    var token = "DOT-BUBBLE-" + Guid.NewGuid().ToString("N")[..8];
                    window.Reply();
                    var input = (TextBox)window.FindName("Input");
                    input.Text = $"这是额度岛消息气泡的回归测试。请只回复 {token}，不要调用工具、创建任务或修改文件。";
                    for (var i = 0; i < 20 && !((Button)window.FindName("SendButton")).IsEnabled; i++) await Task.Delay(100);
                    await window.SendAsync();
                    // Hide the bubble while waiting: browser remains live, reply must bring it back.
                    window.Close();
                    var text = (TextBlock)window.FindName("MessageText");
                    for (var i = 0; i < 120 && text.Text != token; i++) await Task.Delay(500);
                    Assert.True(text.Text == token, "Expected test reply in native bubble; status: " + ((TextBlock)window.FindName("StatusText")).Text);
                    Assert.Empty(input.Text);
                    Assert.True(window.IsVisible);
                    Assert.Equal(Visibility.Collapsed, ((StackPanel)window.FindName("Composer")).Visibility);
                    if (Environment.GetEnvironmentVariable("CQT_DOT_PROOF_DIR") is { Length: > 0 } directory)
                    {
                        Directory.CreateDirectory(directory);
                        window.UpdateLayout();
                        var pixels = new RenderTargetBitmap((int)window.ActualWidth * 2, (int)window.ActualHeight * 2, 192, 192, PixelFormats.Pbgra32);
                        pixels.Render(window);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(pixels));
                        using var file = File.Create(Path.Combine(directory, "dot-bubble.png")); encoder.Save(file);
                    }
                    window.Reply();
                    Assert.Equal(Visibility.Visible, ((StackPanel)window.FindName("Composer")).Visibility);
                    window.ApplyAnchor(new(640, 800, 880, 0, 0, 1920, 1040, 96, false, 0));
                    Assert.False(window.IsVisible);
                }
            }
            finally { window.Stop(); }
        });
    }

    private static KeyEventArgs NewKey(DotChatWindow window, Key key) => new(Keyboard.PrimaryDevice,
        System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle()),
        Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
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
                catch (Exception e) { failure = e; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(135)), "Dot runtime check timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
