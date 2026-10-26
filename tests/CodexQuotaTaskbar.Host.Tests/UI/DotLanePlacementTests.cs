using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.UI;
using Button = System.Windows.Controls.Button;

namespace CodexQuotaTaskbar.Host.Tests.UI;

[Collection("Native UI isolation")]
public sealed class DotLanePlacementTests
{
    [Theory]
    [InlineData(96)] [InlineData(144)] [InlineData(192)]
    public void Supplied_lane_contains_collapsed_and_expanded_native_bounds(int dpi)
    {
        var anchor = Anchor(dpi: dpi);
        var lane = anchor.Lane!;
        foreach (var height in new[] { 100d, 260d, 1000d })
        {
            var actual = DotChatWindow.CalculateBounds(anchor, 320, height);
            Assert.InRange(actual.Left, lane.Left, lane.Right);
            Assert.InRange(actual.Right, lane.Left, lane.Right);
            Assert.InRange(actual.Top, lane.Top, lane.Bottom);
            Assert.InRange(actual.Bottom, lane.Top, lane.Bottom);
            Assert.True(actual.Width > 0 && actual.Height > 0);
        }
    }

    [Fact]
    public void Zero_lane_returns_no_rectangle_and_old_null_lane_keeps_legacy_placement()
    {
        Assert.Equal(System.Drawing.Rectangle.Empty, DotChatWindow.CalculateBounds(Anchor() with { Lane = new(0, 0, 0, 0) }, 320, 300));
        var legacy = Anchor() with { Lane = null };
        var result = DotChatWindow.CalculateBounds(legacy, 320, 260);
        Assert.Equal(320, result.Width); Assert.Equal(260, result.Height);
        Assert.Equal(legacy.Y - 264, result.Top);
    }

    [Fact]
    public void Lane_translation_preserves_composition_selection_attempt_and_auto_reply_deadline() => Sta(window =>
    {
        Composer(window).Visibility = Visibility.Visible;
        Input(window).Text = "Synthetic newer draft"; Input(window).Select(3, 7);
        Set(window, "composing", true); Set(window, "compositionGeneration", 7); Set(window, "imeEnterDown", true);
        Set(window, "pendingText", "Synthetic previously dispatched content");
        Set(window, "attemptId", "offline-lane-attempt"); Set(window, "attemptState", "unknown");
        Set(window, "needsReview", true); Set(window, "busy", true);
        var timer = (DispatcherTimer)Field(window, "autoReplyTimer")!;
        Set(window, "autoReplyDeadline", TimeSpan.FromHours(1)); timer.Interval = TimeSpan.FromHours(1); timer.Start();
        window.ApplyAnchor(Anchor()); Settle(window);
        var state = State(window);
        for (var i = 1; i <= 40; i++) window.ApplyAnchor(Anchor(dx: i * 2));
        Settle(window);
        Assert.Equal(state, State(window));
        Assert.False(window.LaneUnavailable); Assert.True(window.IsVisible);
        Assert.False(window.IsActive); Assert.False(Input(window).IsKeyboardFocused);
        Assert.Null(window.Browser.CoreWebView2);
    });

    [Fact]
    public void Temporarily_unavailable_lane_preserves_draft_and_pending_then_restores_presentation() => Sta(window =>
    {
        Composer(window).Visibility = Visibility.Visible;
        Input(window).Text = "Synthetic unsent content"; Input(window).Select(2, 4);
        Set(window, "pendingText", "Synthetic uncertain attempt"); Set(window, "attemptState", "unknown");
        Set(window, "autoReplyDeadline", TimeSpan.FromHours(1));
        var timer = (DispatcherTimer)Field(window, "autoReplyTimer")!;
        timer.Interval = TimeSpan.FromHours(1); timer.Start();
        window.ApplyAnchor(Anchor()); Settle(window);
        var state = State(window);
        window.ApplyAnchor(Anchor() with { Lane = new(0, 0, 0, 0) });
        Assert.False(window.IsVisible); Assert.True(window.LaneUnavailable);
        Assert.Equal(state, State(window));
        window.ApplyAnchor(Anchor(dx: 20)); Settle(window);
        Assert.True(window.IsVisible); Assert.False(window.LaneUnavailable);
        Assert.Equal(state, State(window)); Assert.Null(window.Browser.CoreWebView2);
    });

    [Fact]
    public void Short_lane_never_clips_a_reply_composer_and_does_not_change_its_visibility_state() => Sta(window =>
    {
        Composer(window).Visibility = Visibility.Visible;
        Input(window).Text = "Synthetic draft";
        var lane = Anchor().Lane!;
        window.ApplyAnchor(Anchor() with { Lane = lane with { Bottom = lane.Top + 160 } });
        Assert.True(window.LaneUnavailable); Assert.False(window.IsVisible);
        Assert.Equal(Visibility.Visible, Composer(window).Visibility);
        Assert.Equal("Synthetic draft", Input(window).Text);
        window.ApplyAnchor(Anchor()); Settle(window);
        Assert.True(window.IsVisible); Assert.False(window.LaneUnavailable);
        Assert.True(Input(window).ActualHeight >= Input(window).MinHeight);
        Assert.True(((Button)window.FindName("SendButton")).ActualHeight >= 28);
    });

    private static DotAnchor Anchor(int dx = 0, int dpi = 96) => new(-30000 + dx, -30000, -29700,
        -50000, -50000, -10000, -10000, dpi, true, 0,
        new(-32000 + dx, -30400, -31360 + dx, -29400));

    private static TextBox Input(DotChatWindow window) => (TextBox)window.FindName("Input");
    private static StackPanel Composer(DotChatWindow window) => (StackPanel)window.FindName("Composer");
    private static object? Field(DotChatWindow window, string name) => typeof(DotChatWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
    private static void Set(DotChatWindow window, string name, object? value) => typeof(DotChatWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    private static string State(DotChatWindow window) => JsonSerializer.Serialize(new
    {
        text = Input(window).Text, selection = Input(window).SelectionStart, length = Input(window).SelectionLength,
        composer = Composer(window).Visibility, pending = Field(window, "pendingText"), attempt = Field(window, "attemptState"),
        attemptId = Field(window, "attemptId"), review = Field(window, "needsReview"), busy = Field(window, "busy"),
        composing = Field(window, "composing"), generation = Field(window, "compositionGeneration"), imeEnter = Field(window, "imeEnterDown"),
        deadline = Field(window, "autoReplyDeadline"), timer = ((DispatcherTimer)Field(window, "autoReplyTimer")!).IsEnabled,
        wanted = Field(window, "wanted"), closed = Field(window, "closed"), lifetime = ((CancellationTokenSource)Field(window, "lifetime")!).IsCancellationRequested,
    });

    private static void Settle(DotChatWindow window)
    {
        for (var i = 0; i < 3; i++)
        {
            window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        }
    }

    private static void Sta(Action<DotChatWindow> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            DotChatWindow? window = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                window = new DotChatWindow { Opacity = 0, ShowActivated = false, ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000 };
                window.ApplyAnchor(Anchor());
                typeof(DotChatWindow).GetMethod("Present", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                Settle(window); action(window);
            }
            catch (Exception error) { failure = error; }
            finally { window?.Stop(); Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Synthetic Dot lane proof timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
