using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.Resets;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.UI;
using CodexQuotaTaskbar.Host.Tray;

namespace CodexQuotaTaskbar.Host.Tests.UI;

// Replay the same managed transitions called by the window's routed handlers.
// Capture is injected; no Show, HWND, GetCursorPos, real mouse, messages or account reads.
[Collection("Native UI isolation")]
public sealed class IslandPointerRoutingTests
{
    [Theory]
    [InlineData(1, false, 1, 0, 0)]
    [InlineData(2, false, 0, 1, 0)]
    [InlineData(3, false, 1, 0, 0)]
    [InlineData(4, false, 0, 0, 1)]
    [InlineData(1, true, 0, 0, 0)]
    [InlineData(2, true, 0, 0, 0)]
    [InlineData(3, true, 0, 0, 0)]
    public void Region_routes_to_exactly_one_action(int hit, bool right, int primary, int dot, int reset) => Sta(window =>
    {
        var counts = new int[4];
        window.PrimaryInvoked += (_, _) => counts[0]++;
        window.CompanionInvoked += (_, _) => counts[1]++;
        window.OpenResetSourceRequested += uri =>
        {
            counts[2]++;
            Assert.Equal(CodexResetFeedClient.SiteUri, uri);
            Assert.True(App.IsAllowedResetSource(uri));
        };
        window.ContextInvoked += (_, _) => counts[3]++;
        var button = right ? MouseButton.Right : MouseButton.Left;
        var region = (IslandHitRegion)hit;
        Assert.True(window.BeginPointerGesture(button, region, 100, 0, 0, () => true));
        Assert.True(window.EndPointerGesture(button, region, 110));
        Assert.False(window.EndPointerGesture(button, region, 110)); // Duplicate delivery is inert.
        Assert.Equal(new[] { primary, dot, reset, right ? 1 : 0 }, counts);
        Assert.False(window.HasPointerGesture);
        Assert.False(App.IsAllowedResetSource(new Uri("https://example.invalid/")));
        Assert.False(App.IsAllowedResetSource(new Uri("https://x.com/fixture")));
    });

    [Fact]
    public void Hit_priority_does_not_expand_ball_into_cat_quota_sessions_or_dot_editor()
    {
        Assert.Equal(IslandHitRegion.Ball, QuotaCapsuleWindow.ResolveHitRegion(false, true, true, false, true, false));
        Assert.Equal(IslandHitRegion.Bongo, QuotaCapsuleWindow.ResolveHitRegion(false, false, true, false, false, false));
        Assert.Equal(IslandHitRegion.Quota, QuotaCapsuleWindow.ResolveHitRegion(false, false, false, false, true, false));
        Assert.Equal(IslandHitRegion.None, QuotaCapsuleWindow.ResolveHitRegion(false, false, false, false, true, true));
        Assert.Equal(IslandHitRegion.None, QuotaCapsuleWindow.ResolveHitRegion(false, false, false, false, false, false));
        Assert.Equal(IslandHitRegion.None, QuotaCapsuleWindow.ResolveHitRegion(true, true, true, true, true, false));
    }

    [Fact]
    public void Header_and_footer_define_transparent_surfaces_for_blank_regions() => Sta(window =>
    {
        var header = (Grid)window.FindName("QuotaHeader");
        var footer = (Border)window.FindName("ResetScheduleRow");
        Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(header.Background).Color);
        Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(footer.Background).Color);
        // This verifies the XAML hit-surface contract only. Hidden WPF controls
        // cannot establish native hit delivery; geometry/remote delivery remains a live-check limitation.
        Assert.True(header.IsHitTestVisible);
        Assert.Null(header.Clip);
    });

    [Fact]
    public void Element_capture_hover_loss_does_not_change_point_classification_or_swallow_click() => Sta(window =>
    {
        var dot = 0; var primary = 0;
        window.CompanionInvoked += (_, _) => dot++;
        window.PrimaryInvoked += (_, _) => primary++;
        var ball = new Rect(224, 0, 56, 56);
        var cat = new Rect(16, 16, 248, 100);
        var quota = new Rect(16, 116, 248, 48);
        var stamp = 100;
        foreach (var point in new[] { new Point(240, 20), new Point(80, 50), new Point(80, 130) })
        {
            // No IsMouseOver input: simulates all child hover flags false under Window capture.
            var down = QuotaCapsuleWindow.RegionAtPoint(point, Rect.Empty, ball, cat, Rect.Empty, quota, Rect.Empty, Rect.Empty);
            window.BeginPointerGesture(MouseButton.Left, down, stamp++, 0, 0, () => true);
            var up = QuotaCapsuleWindow.RegionAtPoint(point, Rect.Empty, ball, cat, Rect.Empty, quota, Rect.Empty, Rect.Empty);
            window.EndPointerGesture(MouseButton.Left, up, stamp++);
        }
        Assert.Equal(1, dot); Assert.Equal(2, primary);
        Assert.Equal(IslandHitRegion.None, QuotaCapsuleWindow.RegionAtPoint(new Point(281, 20),
            Rect.Empty, ball, cat, Rect.Empty, quota, Rect.Empty, Rect.Empty));
    });

    [Fact]
    public void Controller_rejected_reentry_cannot_detach_existing_island_binding() => Sta(window =>
    {
        using var controller = new TrayController(AppSettings.Default with { ShowTrayIcon = false },
            restrictedDemo: true, setAutostart: _ => throw new Exception("No autostart mutation"));
        var field = typeof(TrayController).GetField("menuIsland", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var menu = (TrayMenuWindow)typeof(TrayController).GetField("menu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(controller)!;
        field.SetValue(controller, window);
        Assert.True(controller.RunContextRequest(() => controller.ShowContextMenu(window)));
        Assert.Same(window, field.GetValue(controller));
        Assert.True(menu.RunPopupRequest(() => controller.ShowContextMenu(window)));
        Assert.Same(window, field.GetValue(controller));
        Assert.Equal(IntPtr.Zero, new WindowInteropHelper(menu).Handle);
    });

    [Fact]
    public void Left_to_right_and_fast_replays_are_one_action_per_gesture_even_with_reentrant_up() => Sta(window =>
    {
        var sequence = new List<string>();
        window.PrimaryInvoked += (_, _) => sequence.Add("left");
        window.ContextInvoked += (_, _) =>
        {
            sequence.Add("right");
            Assert.False(window.EndPointerGesture(MouseButton.Right, IslandHitRegion.Quota, 99));
        };
        for (var i = 0; i < 12; i++)
        {
            var button = i % 2 == 0 ? MouseButton.Left : MouseButton.Right;
            var stamp = 100 + i;
            window.BeginPointerGesture(button, IslandHitRegion.Quota, stamp, 0, 0, () => true);
            window.BeginPointerGesture(button, IslandHitRegion.Quota, stamp, 0, 0, () => throw new Exception("Duplicate captured twice"));
            window.EndPointerGesture(button, IslandHitRegion.Quota, stamp + 1);
            window.EndPointerGesture(button, IslandHitRegion.Quota, stamp + 1);
        }
        Assert.Equal(Enumerable.Range(0, 12).Select(i => i % 2 == 0 ? "left" : "right"), sequence);
    });

    [Theory]
    [InlineData("missing-up")]
    [InlineData("lost-capture")]
    [InlineData("failed-capture")]
    [InlineData("drag")]
    [InlineData("hide")]
    [InlineData("release-elsewhere")]
    [InlineData("move-throws")]
    public void Interrupted_or_dragged_gesture_cannot_click_and_next_gesture_recovers(string scenario) => Sta(window =>
    {
        var primary = 0; var dot = 0; var moves = 0;
        window.PrimaryInvoked += (_, _) => primary++;
        window.CompanionInvoked += (_, _) => dot++;
        window.UserMoveRequested += _ => { moves++; if (scenario == "move-throws") throw new InvalidOperationException("synthetic"); };
        var begun = window.BeginPointerGesture(MouseButton.Left, IslandHitRegion.Ball, 100, 10, 10, () => scenario != "failed-capture");
        Assert.Equal(scenario != "failed-capture", begun);
        switch (scenario)
        {
            case "missing-up": window.MovePointerGesture(false, 10, 10); break;
            case "lost-capture": window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 101) { RoutedEvent = Mouse.LostMouseCaptureEvent }); break;
            case "hide": window.CancelPointerGesture("hidden"); break;
            case "drag": Assert.True(window.MovePointerGesture(true, 14, 10)); break;
            case "move-throws": Assert.Throws<InvalidOperationException>(() => window.MovePointerGesture(true, 15, 10)); break;
        }
        window.EndPointerGesture(MouseButton.Left, scenario == "release-elsewhere" ? IslandHitRegion.Bongo : IslandHitRegion.Ball, 102);
        Assert.Equal(0, dot); Assert.Equal(0, primary);
        Assert.False(window.HasPointerGesture); Assert.False(window.IsUserDragging);
        window.BeginPointerGesture(MouseButton.Left, IslandHitRegion.Bongo, 200, 10, 10, () => true);
        Assert.False(window.MovePointerGesture(true, 12, 11));
        window.EndPointerGesture(MouseButton.Left, IslandHitRegion.Bongo, 201);
        Assert.Equal(1, primary);
        Assert.Equal(scenario is "drag" or "move-throws" ? 1 : 0, moves);
        Assert.True(window.InputDiagnostics.Count <= 64);
    });

    [Fact]
    public void Menu_open_transaction_rejects_nested_show_without_time_delay_and_recovers_after_error() => Sta(_ =>
    {
        var menu = new TrayMenuWindow(AppSettings.Default, activateOnShow: false);
        try
        {
            var calls = 0;
            Assert.True(menu.RunPopupRequest(() =>
            {
                calls++;
                Assert.False(menu.RunPopupRequest(() => calls++));
            }));
            Assert.Equal(1, calls);
            Assert.Throws<InvalidOperationException>(() => menu.RunPopupRequest(() => throw new InvalidOperationException("fixture")));
            Assert.True(menu.RunPopupRequest(() => calls++));
            Assert.Equal(2, calls);
            Assert.Equal(IntPtr.Zero, new WindowInteropHelper(menu).Handle);
        }
        finally { menu.Close(); }
    });

    [Fact]
    public void Consumed_notice_hides_actual_row_and_a_new_notice_can_return_after_green() => Sta(window =>
    {
        var now = DateTimeOffset.UtcNow;
        var tracker = new QuotaResetTracker();
        var scope = QuotaResetTracker.ScopeForSource("FIXTURE")!;
        var notice = new ResetSchedule("test", ResetKind.Regular, now.AddMinutes(-1), now.AddHours(3), CodexResetFeedClient.SiteUri);
        var snapshot = new ResetScheduleSnapshot(notice, now, false, null);
        window.SetResetTracker(tracker);
        tracker.SetNotice(notice);
        window.ApplyResetSchedule(snapshot, now);
        var baseline = window.DesiredHeight - QuotaCapsuleWindow.ResetScheduleRowHeight;
        CodexQuotaTaskbar.Core.Quota.QuotaSnapshot Sample(DateTimeOffset time, double remaining) =>
            CodexQuotaTaskbar.Core.Quota.QuotaSnapshot.Available([new(CodexQuotaTaskbar.Core.Quota.QuotaWindowKind.Primary, remaining, 300, now.AddHours(3))], time);
        tracker.Observe(Sample(now, 20), scope, now);
        var until = tracker.Observe(Sample(now.AddSeconds(1), 21), scope, now.AddSeconds(1));
        window.ApplyConfirmedReset(until);
        window.RefreshResetSchedule(now.AddSeconds(1));
        Assert.Equal(baseline, window.DesiredHeight);
        Assert.False(((Border)window.FindName("ResetScheduleRow")).IsHitTestVisible);
        var expired = until!.Value;
        window.ApplyResetSchedule(snapshot with { LastUpdatedAt = expired }, expired);
        Assert.Equal(baseline, window.DesiredHeight);
        window.ApplyResetSchedule(snapshot with { Schedule = notice with { Id = "new", AnnouncedAt = now.AddMinutes(2) }, LastUpdatedAt = expired }, expired);
        Assert.Equal(baseline + QuotaCapsuleWindow.ResetScheduleRowHeight, window.DesiredHeight);
    });

    private static void Sta(Action<QuotaCapsuleWindow> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var window = new QuotaCapsuleWindow("offline-input");
            try { action(window); Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Handle); }
            catch (Exception e) { failure = e; }
            finally { window.Close(); dispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
