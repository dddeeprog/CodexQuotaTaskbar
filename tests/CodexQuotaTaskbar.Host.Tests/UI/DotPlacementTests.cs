using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.Platform;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

// Synthetic, transparent, offscreen HWNDs only. Never Start, Reply, Activate, Focus or send.
public sealed class DotPlacementTests
{
    [Fact]
    public void Identical_anchors_reuse_material_and_skip_every_native_placement()
    {
        WithWindow(window =>
        {
            var anchor = Anchor();
            var brush = Glass(window).Background;
            var calls = Counters(window);
            var bounds = NativeBounds(window);
            for (var i = 0; i < 1000; i++) window.ApplyAnchor(anchor);
            Settle(window);
            Assert.Equal(calls, Counters(window));
            Assert.Same(brush, Glass(window).Background);
            Assert.Equal(bounds, NativeBounds(window));
            AssertDormantBrowser(window);
        });
    }

    [Fact]
    public void Translation_moves_the_actual_hwnd_without_resizing_restyling_or_changing_chat_state()
    {
        WithWindow(window =>
        {
            ShowComposer(window);
            Input(window).Text = "Synthetic newer unsent draft, unrelated to an older attempt.";
            Input(window).Select(10, 8);
            SeedPendingAttempt(window);
            Settle(window);
            var state = Snapshot(window);
            var brush = Glass(window).Background;
            var input = Input(window);
            var message = window.FindName("MessageText");
            var composer = Composer(window);
            var initial = NativeBounds(window);
            var calls = Counters(window);

            for (var i = 1; i <= 100; i++) window.ApplyAnchor(Anchor(dx: i * 2, dy: i));
            Settle(window);

            Assert.Equal(calls.Placement + 100, window.PlacementCount);
            Assert.Equal(calls.Resize, window.PlacementResizeCount);
            Assert.Equal(calls.Style, window.PlacementStyleCount);
            Assert.Equal(state, Snapshot(window));
            Assert.Same(brush, Glass(window).Background);
            Assert.Same(input, Input(window));
            Assert.Same(message, window.FindName("MessageText"));
            Assert.Same(composer, Composer(window));
            var final = NativeBounds(window);
            Assert.Equal(initial.Width, final.Width);
            Assert.Equal(initial.Height, final.Height);
            Assert.Equal(initial.X + 200, final.X);
            Assert.Equal(initial.Y + 100, final.Y);
            AssertBounds(window, Anchor(dx: 200, dy: 100));
            AssertDormantBrowser(window);
        });
    }

    [Fact]
    public void Translation_preserves_an_active_auto_reply_deadline_without_rearming_it()
    {
        WithWindow(window =>
        {
            Invoke(window, "ExpandReplyForNewMessage");
            Settle(window);
            var deadline = (TimeSpan?)Field(window, "autoReplyDeadline");
            Assert.NotNull(deadline);
            Assert.True(Timer(window).IsEnabled);
            var state = Snapshot(window);
            for (var i = 1; i <= 100; i++) window.ApplyAnchor(Anchor(dx: i, dy: i * 2));
            Settle(window);
            Assert.Equal(state, Snapshot(window));
            Assert.Equal(deadline, (TimeSpan?)Field(window, "autoReplyDeadline"));
            Assert.True(Timer(window).IsEnabled);
            Assert.Equal(Visibility.Visible, Composer(window).Visibility);
            AssertDormantBrowser(window);
        });
    }

    [Fact]
    public void Translation_does_not_interrupt_an_existing_composition_or_its_selection()
    {
        WithWindow(window =>
        {
            ShowComposer(window);
            Input(window).Text = "Synthetic composition draft.";
            Input(window).Select(4, 6);
            Set(window, "composing", true);
            Set(window, "compositionGeneration", 17);
            Set(window, "imeEnterDown", true);
            var state = Snapshot(window);
            for (var i = 1; i <= 80; i++) window.ApplyAnchor(Anchor(dx: -i, dy: i));
            Settle(window);
            Assert.Equal(state, Snapshot(window));
            Assert.True((bool)Field(window, "composing")!);
            AssertDormantBrowser(window);
        });
    }

    [Fact]
    public void A_real_transparency_change_updates_material_once_without_moving_the_hwnd()
    {
        WithWindow(window =>
        {
            var brush = Glass(window).Background;
            var calls = Counters(window);
            var bounds = NativeBounds(window);
            var changed = Anchor() with { Transparency = 43 };
            window.ApplyAnchor(changed);
            Settle(window);
            Assert.Equal(calls.Style + 1, window.PlacementStyleCount);
            Assert.Equal(calls.Placement, window.PlacementCount);
            Assert.Equal(calls.Resize, window.PlacementResizeCount);
            Assert.Equal(bounds, NativeBounds(window));
            if (!SystemParameters.HighContrast) Assert.NotSame(brush, Glass(window).Background);
            var changedBrush = Glass(window).Background;
            var afterChange = Counters(window);
            for (var i = 0; i < 100; i++) window.ApplyAnchor(changed);
            Assert.Equal(afterChange, Counters(window));
            Assert.Same(changedBrush, Glass(window).Background);
            AssertDormantBrowser(window);
        });
    }

    [Fact]
    public void Composer_expansion_and_collapse_still_resize_and_preserve_the_notification()
    {
        WithWindow(window =>
        {
            ((TextBlock)window.FindName("MessageText")).Text = "Synthetic line one.\nSynthetic line two.\nSynthetic line three.";
            Settle(window);
            var collapsed = NativeBounds(window);
            var before = window.PlacementResizeCount;
            ShowComposer(window);
            var expanded = NativeBounds(window);
            Assert.True(expanded.Height > collapsed.Height);
            Assert.True(window.PlacementResizeCount > before);
            AssertBounds(window, Anchor());
            var afterExpansion = window.PlacementResizeCount;

            Composer(window).Visibility = Visibility.Collapsed;
            Scroller(window).MaxHeight = 112;
            Settle(window);
            Assert.True(NativeBounds(window).Height < expanded.Height);
            Assert.True(window.PlacementResizeCount > afterExpansion);
            AssertBounds(window, Anchor());
            Assert.True(window.IsVisible);
            Assert.True((bool)Field(window, "wanted")!);

            var stable = Counters(window);
            window.ApplyAnchor(Anchor(dx: 40, dy: 30));
            Settle(window);
            Assert.Equal(stable.Placement + 1, window.PlacementCount);
            Assert.Equal(stable.Resize, window.PlacementResizeCount);
            Assert.Equal(stable.Style, window.PlacementStyleCount);
            AssertDormantBrowser(window);
        });
    }

    [Fact]
    public void Width_changes_resize_once_stable_then_later_translation_is_move_only()
    {
        WithWindow(window =>
        {
            var before = window.PlacementResizeCount;
            window.Width = 384;
            Settle(window);
            Assert.True(window.PlacementResizeCount > before);
            Assert.Equal(384, NativeBounds(window).Width);
            AssertBounds(window, Anchor());
            var stable = Counters(window);
            window.ApplyAnchor(Anchor(dx: 60, dy: -40));
            Settle(window);
            Assert.Equal(stable.Placement + 1, window.PlacementCount);
            Assert.Equal(stable.Resize, window.PlacementResizeCount);
            AssertBounds(window, Anchor(dx: 60, dy: -40));
            AssertDormantBrowser(window);
        });
    }

    [Fact]
    public void Hidden_wanted_bubble_uses_latest_anchor_on_restore_and_keeps_the_draft()
    {
        WithWindow(window =>
        {
            ShowComposer(window);
            Input(window).Text = "Synthetic draft retained while the owner is hidden.";
            Input(window).Select(5, 9);
            var revision = Field(window, "draftRevision");
            window.ApplyAnchor(Anchor(visible: false));
            var calls = Counters(window);
            for (var i = 1; i <= 100; i++) window.ApplyAnchor(Anchor(dx: i, dy: i, visible: false));
            Assert.Equal(calls, Counters(window));
            Assert.False(window.IsVisible);
            Assert.True((bool)Field(window, "wanted")!);
            window.ApplyAnchor(Anchor(dx: 100, dy: 100));
            Settle(window);
            Assert.True(window.IsVisible);
            AssertBounds(window, Anchor(dx: 100, dy: 100));
            Assert.Equal("Synthetic draft retained while the owner is hidden.", Input(window).Text);
            Assert.Equal(5, Input(window).SelectionStart);
            Assert.Equal(9, Input(window).SelectionLength);
            Assert.Equal(revision, Field(window, "draftRevision"));
            AssertDormantBrowser(window);
        });
    }

    [Fact]
    public void Moving_visible_or_hidden_anchors_never_presents_a_bubble_that_was_not_wanted()
    {
        WithWindow(window =>
        {
            var calls = Counters(window);
            for (var i = 1; i <= 100; i++)
            {
                window.ApplyAnchor(Anchor(dx: i, visible: false));
                window.ApplyAnchor(Anchor(dx: i, visible: true));
            }
            Assert.Equal(calls, Counters(window));
            Assert.False(window.IsVisible);
            Assert.False((bool)Field(window, "wanted")!);
            AssertDormantBrowser(window);
        }, present: false);
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    [InlineData(480)]
    public void Dpi_geometry_scales_size_and_keeps_the_bubble_inside_synthetic_work_area(double dpi)
    {
        var anchor = Anchor() with { Dpi = dpi };
        var rect = DotChatWindow.CalculateBounds(anchor, 320, 260);
        Assert.Equal((int)Math.Ceiling(320 * dpi / 96), rect.Width);
        Assert.Equal((int)Math.Ceiling(260 * dpi / 96), rect.Height);
        Assert.Equal(anchor.X, rect.Left + rect.Width / 2);
        Assert.True(rect.Left >= anchor.Left + 8 && rect.Right <= anchor.Right - 8);
        Assert.True(rect.Top >= anchor.Top + 8 && rect.Bottom <= anchor.WorkBottom - 8);
        Assert.Equal(anchor.Y - 4, rect.Bottom);
    }

    [Fact]
    public void One_thousand_translations_have_no_restyle_or_resize_and_can_write_local_performance_evidence()
    {
        WithWindow(window =>
        {
            var calls = Counters(window);
            var brush = Glass(window).Background;
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            for (var i = 1; i <= 1000; i++) window.ApplyAnchor(Anchor(dx: i % 250 * 2, dy: i / 250 * 3));
            watch.Stop();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Settle(window);
            Assert.Equal(calls.Placement + 1000, window.PlacementCount);
            Assert.Equal(calls.Resize, window.PlacementResizeCount);
            Assert.Equal(calls.Style, window.PlacementStyleCount);
            Assert.Same(brush, Glass(window).Background);
            AssertBounds(window, Anchor(dy: 12));
            AssertDormantBrowser(window);
            if (Environment.GetEnvironmentVariable("CQT_PLACEMENT_PROOF_DIR") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"placement-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
                File.WriteAllText(path, JsonSerializer.Serialize(new
                {
                    schema = 1, synthetic = true, iterations = 1000,
                    elapsedMilliseconds = watch.Elapsed.TotalMilliseconds, allocatedBytesOnStaThread = allocated,
                    nativePlacementDelta = window.PlacementCount - calls.Placement,
                    nativeResizeDelta = window.PlacementResizeCount - calls.Resize,
                    styleDelta = window.PlacementStyleCount - calls.Style,
                    noBrowserCreated = window.Browser.CoreWebView2 is null,
                    noActivation = !window.IsActive, noKeyboardFocus = !Input(window).IsKeyboardFocused,
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        });
    }

    private static DotAnchor Anchor(int dx = 0, int dy = 0, bool visible = true) =>
        new(-30000 + dx, -30000 + dy, -29944 + dy, -50000, -50000, -10000, -10000, 96, visible, 0);

    private static TextBox Input(DotChatWindow window) => (TextBox)window.FindName("Input");
    private static Border Glass(DotChatWindow window) => (Border)window.FindName("GlassBorder");
    private static StackPanel Composer(DotChatWindow window) => (StackPanel)window.FindName("Composer");
    private static ScrollViewer Scroller(DotChatWindow window) => (ScrollViewer)window.FindName("MessageScroller");
    private static DispatcherTimer Timer(DotChatWindow window) => (DispatcherTimer)Field(window, "autoReplyTimer")!;
    private static (int Placement, int Resize, int Style) Counters(DotChatWindow window) =>
        (window.PlacementCount, window.PlacementResizeCount, window.PlacementStyleCount);

    private static void SeedPendingAttempt(DotChatWindow window)
    {
        Set(window, "pendingText", "Synthetic older already-dispatched attempt.");
        Set(window, "pendingSince", DateTime.UtcNow);
        Set(window, "pendingDraftRevision", (long)Field(window, "draftRevision")! - 1);
        Set(window, "pendingBaselineComplete", true);
        Set(window, "needsReview", true);
        Set(window, "attemptId", "synthetic-placement-attempt");
        Set(window, "attemptState", "unknown");
        ((HashSet<string>)Field(window, "beforeSend")!).Add("synthetic-baseline-id");
    }

    private static string Snapshot(DotChatWindow window) => JsonSerializer.Serialize(new
    {
        text = Input(window).Text, selection = Input(window).SelectionStart, selectedLength = Input(window).SelectionLength,
        message = ((TextBlock)window.FindName("MessageText")).Text,
        status = ((TextBlock)window.FindName("StatusText")).Text,
        composer = Composer(window).Visibility, scrollerHeight = Scroller(window).MaxHeight,
        pending = Field(window, "pendingText"), pendingSince = Field(window, "pendingSince"),
        pendingRevision = Field(window, "pendingDraftRevision"), draftRevision = Field(window, "draftRevision"),
        beforeIds = ((HashSet<string>)Field(window, "beforeSend")!).OrderBy(id => id).ToArray(),
        baseline = Field(window, "pendingBaselineComplete"), attemptId = Field(window, "attemptId"),
        attemptState = Field(window, "attemptState"), review = Field(window, "needsReview"), busy = Field(window, "busy"),
        composing = Field(window, "composing"), generation = Field(window, "compositionGeneration"), imeEnter = Field(window, "imeEnterDown"),
        deadline = Field(window, "autoReplyDeadline"), awaiting = Field(window, "autoReplyAwaitingPresentation"),
        timerEnabled = Timer(window).IsEnabled, wanted = Field(window, "wanted"), ready = Field(window, "ready"),
        pageVisible = ((Window)Field(window, "pageWindow")!).IsVisible,
    });

    private static void ShowComposer(DotChatWindow window)
    {
        Composer(window).Visibility = Visibility.Visible;
        Scroller(window).MaxHeight = 160;
        Settle(window);
    }

    private static void Settle(DotChatWindow window)
    {
        for (var i = 0; i < 3; i++)
        {
            window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        }
        Invoke(window, "Reposition");
    }

    private static System.Drawing.Rectangle NativeBounds(DotChatWindow window)
    {
        Assert.True(NativeMethods.GetWindowRect(new WindowInteropHelper(window).Handle, out var rect));
        return new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    private static void AssertBounds(DotChatWindow window, DotAnchor anchor)
    {
        var rect = NativeBounds(window);
        Assert.Equal(DotChatWindow.CalculateBounds(anchor, window.Width, Math.Max(window.ActualHeight, 80)), rect);
        Assert.True(rect.Left >= anchor.Left + 8 && rect.Right <= anchor.Right - 8);
        Assert.True(rect.Top >= anchor.Top + 8 && rect.Bottom <= anchor.WorkBottom - 8);
    }

    private static void AssertDormantBrowser(DotChatWindow window)
    {
        Assert.False(window.IsActive);
        Assert.False(Input(window).IsKeyboardFocused);
        Assert.False(((Window)Field(window, "pageWindow")!).IsVisible);
        Assert.Null(window.Browser.CoreWebView2);
        Assert.False((bool)Field(window, "started")!);
        Assert.False((bool)Field(window, "closed")!);
        Assert.False(((CancellationTokenSource)Field(window, "lifetime")!).IsCancellationRequested);
    }

    private static object? Field(DotChatWindow window, string name) =>
        typeof(DotChatWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
    private static void Set(DotChatWindow window, string name, object? value) =>
        typeof(DotChatWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    private static void Invoke(DotChatWindow window, string name) =>
        typeof(DotChatWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);

    private static void WithWindow(Action<DotChatWindow> test, bool present = true)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            DotChatWindow? window = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                window = new DotChatWindow
                {
                    Opacity = 0, ShowActivated = false, ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000,
                };
                window.ApplyAnchor(Anchor());
                if (present) { Invoke(window, "Present"); Settle(window); }
                test(window);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                window?.Stop();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Synthetic offscreen placement check timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
