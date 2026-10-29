using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Host.Resets;
using CodexQuotaTaskbar.Host.Tests.TestSupport;
using CodexQuotaTaskbar.Host.UI;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class ResetScheduleRowTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-23T16:00:00Z");
    private static readonly TimeZoneInfo China = TimeZoneInfo.CreateCustomTimeZone("RowTest+08", TimeSpan.FromHours(8), "UTC+08", "UTC+08");
    private static readonly Uri Announcement = new("https://x.com/thsottiaux/status/2113492502355562875");

    [Theory]
    [InlineData(96, false, false)] [InlineData(144, false, false)] [InlineData(192, false, false)]
    [InlineData(96, true, false)] [InlineData(144, true, false)] [InlineData(192, true, false)]
    [InlineData(96, false, true)] [InlineData(144, false, true)] [InlineData(192, false, true)]
    [InlineData(96, true, true)] [InlineData(144, true, true)] [InlineData(192, true, true)]
    public void Single_line_status_removes_unused_rows_and_height_in_actual_full_and_compact_layout(int dpi, bool green, bool bongo) => WithStatusBall((window, ball) =>
    {
        using var fixture = DpiFixture();
        var now = DateTimeOffset.UtcNow;
        if (bongo)
        {
            window.ConfigureBongoSurface(true, 160);
            window.PresentBongoFrame(new CodexQuotaTaskbar.Host.Bongo.BongoFrame(1, 1, [255, 255, 255, 255], 4));
        }
        window.ApplySessions(Sessions(100)); window.SetDotMarker(100, canAcknowledge: true);
        var baseline = window.DesiredHeight;
        var until = now.AddHours(1);
        if (green) window.ApplyConfirmedReset(until);
        else window.ApplyResetQueryState(new(1, ResetQueryPhase.Failed));
        Layout(window, dpi);
        var title = Named<TextBlock>(window, "ResetScheduleCountdown");
        var detail = Named<TextBlock>(window, "ResetScheduleLocalTime");
        var row = Named<Border>(window, "ResetScheduleRow");
        var expected = green ? "额度已回升" : "重置预告查询失败";
        Assert.Equal(expected, title.Text); Assert.Empty(detail.Text);
        Assert.Equal(Visibility.Collapsed, detail.Visibility);
        Assert.Equal(0, Assert.IsType<Grid>(detail.Parent).RowDefinitions[1].Height.Value);
        Assert.Equal(26, row.ActualHeight);
        Assert.Equal(baseline + 26, window.DesiredHeight);
        Assert.Equal(expected, System.Windows.Automation.AutomationProperties.GetName(row));
        window.SetResetScheduleAvailableHeight(0); var root = Layout(window, dpi);
        var compact = Named<StackPanel>(window, "CompactResetNotice");
        var compactDetail = Named<TextBlock>(window, "CompactResetLocalTime");
        Assert.Equal(Visibility.Visible, compact.Visibility); Assert.Equal(14, compact.ActualHeight);
        Assert.Empty(compactDetail.Text); Assert.Equal(Visibility.Collapsed, compactDetail.Visibility);
        Assert.Equal(baseline, window.DesiredHeight); Assert.Equal(baseline, window.ReservedHeight);
        Assert.Equal(0, window.AppliedCompactResetExtraHeight);
        Assert.Equal("空间受限 · " + expected, System.Windows.Automation.AutomationProperties.GetName(compact));
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        Assert.True(Bounds(Named<Grid>(window, "QuotaHeader")).Bottom <= Bounds(compact).Top + .5);
        Assert.True(window.DotMarkerVisible); if (bongo) Assert.True(window.BongoHasFrame);
        var generation = window.ExtensionMotionGeneration;
        for (var repeat = 0; repeat < 3; repeat++)
        {
            window.RefreshResetSchedule(now); window.SetResetScheduleAvailableHeight(0);
            Assert.Equal(generation, window.ExtensionMotionGeneration);
            Assert.Equal(green ? until : (DateTimeOffset?)null, Private<DateTimeOffset?>(window, "confirmedResetUntil"));
        }
    });

    [Theory]
    [InlineData(1d)] [InlineData(1.5d)] [InlineData(2d)]
    public void State_aware_height_budget_rechecks_gold_status_and_green_expiry_without_another_space_change(double scale) => Sta(() =>
    {
        using var coordinator = new CodexQuotaTaskbar.Host.Overlay.OverlayCoordinator(false, unifiedPanel: true, observePointer: false);
        Private<System.Windows.Threading.DispatcherTimer>(coordinator, "timer").Stop();
        var update = coordinator.GetType().GetMethod("UpdateFooterAvailability", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var reservation = coordinator.GetType().GetMethod("FooterReservation", BindingFlags.Static | BindingFlags.NonPublic)!;
        var now = DateTimeOffset.UtcNow;
        foreach (var available in new[] { 0d, 3d, 4d, 25d, 26d, 41d, 42d })
        {
            var window = new QuotaCapsuleWindow("synthetic-state-height-budget");
            try
            {
                var baseline = window.CoreBodyDesiredHeight;
                window.ShowDotTopExtension(new TextBlock { Text = "合成 Dot 消息" }, 36);
                var work = new CodexQuotaTaskbar.Core.Overlay.ScreenRect(0, 0, 1000,
                    (int)Math.Ceiling((baseline + 37 + available) * scale + 8));
                void Reconcile() => update.Invoke(coordinator, [window, work, scale, 0]);
                void Check(bool gold)
                {
                    var increment = gold ? available >= 42 ? 42d : available >= 4 ? 4d : 0d : available >= 26 ? 26d : 0d;
                    Assert.Equal(baseline + 37 + increment, window.DesiredHeight);
                    Assert.Equal(baseline + increment, window.CoreBodyReservedHeight + (double)reservation.Invoke(null, [window])!);
                    var generation = window.ExtensionMotionGeneration;
                    for (var repeat = 0; repeat < 3; repeat++) Reconcile();
                    Assert.Equal(generation, window.ExtensionMotionGeneration);
                    Assert.Equal(baseline + 37 + increment, window.DesiredHeight);
                    Assert.True(window.DesiredHeight * scale + 8 <= work.Height + .01);
                }
                Reconcile();
                window.ApplyResetSchedule(Fresh(now, now.AddHours(2)) with { IsLatchedSchedule = true }, now, China); Check(true);
                window.ApplyResetQueryState(new(1, ResetQueryPhase.Failed)); Check(false);
                window.ApplyResetQueryState(new(2, ResetQueryPhase.Idle));
                var until = now.AddHours(1); window.ApplyConfirmedReset(until); Check(false);
                window.RefreshResetSchedule(until, China); Check(true);
                Assert.Equal(until, Private<DateTimeOffset?>(window, "confirmedResetUntil"));
            }
            finally { window.Close(); }
        }
    });

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void Active_reset_footer_is_automatic_and_leaving_does_not_replay_its_extension(int state) => WithStatusBall((window, ball) =>
    {
        var now = DateTimeOffset.UtcNow;
        if (state == 0) window.ApplyResetQueryState(new(1, ResetQueryPhase.Failed));
        if (state == 1) window.ApplyConfirmedReset(now.AddHours(1));
        if (state == 2) window.ApplyResetSchedule(Fresh(now, now.AddHours(2)), now, China);
        Assert.Equal(state == 2 ? ResetFooterKind.Schedule : ResetFooterKind.Status, window.ResetFooter);
        Assert.Equal(Visibility.Visible, Named<Border>(window, "ResetScheduleRow").Visibility);
        var height = window.DesiredHeight;
        var generation = window.ExtensionMotionGeneration;
        for (var repeat = 0; repeat < 4; repeat++)
        {
            window.UpdateResetStatusPointer(new Point(-100, -100));
            ball.SetResetHintHovered(false);
            if (state == 0) window.ApplyResetQueryState(new(2 + repeat, ResetQueryPhase.Checking) { HasUnresolvedFailure = true });
            if (state == 1) window.ApplyConfirmedReset(now.AddHours(1));
            window.RefreshResetSchedule(now, China);
            Assert.Equal(state == 2 ? ResetFooterKind.Schedule : ResetFooterKind.Status, window.ResetFooter);
            Assert.Equal(height, window.DesiredHeight);
            Assert.Equal(generation, window.ExtensionMotionGeneration);
        }
    });

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void Automatic_status_coexists_with_dot_and_a_pointer_gesture(int state) => WithStatusBall((window, ball) =>
    {
        var now = DateTimeOffset.UtcNow;
        if (state == 1) window.ApplyConfirmedReset(now.AddHours(1));
        else if (state == 2) window.ApplyResetSchedule(Fresh(now, now.AddHours(2)), now);
        else window.ApplyResetQueryState(new(1, ResetQueryPhase.Failed));
        var expected = state == 2 ? ResetFooterKind.Schedule : ResetFooterKind.Status;
        var dot = new TextBlock { Text = "合成 Dot 消息" };
        window.SetDotMarker(2, canAcknowledge: true);
        var marker = window.DotMarkerButton;
        window.ShowDotTopExtension(dot, 30);
        Assert.Equal(expected, window.ResetFooter);
        Assert.Same(dot, window.DotTopContent);
        Assert.Same(marker, window.DotMarkerButton);
        Assert.True(window.BeginPointerGesture(System.Windows.Input.MouseButton.Left, IslandHitRegion.Ball, 100, 0, 0, () => true));
        Assert.Equal(expected, window.ResetFooter);
        window.CancelPointerGesture("synthetic-auto-footer");
        window.HideDotTopExtension();
        Assert.Equal(expected, window.ResetFooter);
    });

    [Theory]
    [InlineData(1d)] [InlineData(1.5d)] [InlineData(2d)]
    public void Actual_space_budget_boundaries_are_stable_including_a_visible_dot(double scale) => WithStatusBall((window, ball) =>
    {
        using var coordinator = new CodexQuotaTaskbar.Host.Overlay.OverlayCoordinator(false, unifiedPanel: true, observePointer: false);
        Private<System.Windows.Threading.DispatcherTimer>(coordinator, "timer").Stop();
        var update = coordinator.GetType().GetMethod("UpdateFooterAvailability", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var reservation = coordinator.GetType().GetMethod("FooterReservation", BindingFlags.Static | BindingFlags.NonPublic)!;
        var now = DateTimeOffset.UtcNow;
        window.ApplySessions(Sessions(3));
        var baseline = window.CoreBodyDesiredHeight;
        window.ApplyConfirmedReset(now.AddHours(1));
        var until = Private<DateTimeOffset?>(window, "confirmedResetUntil");
        window.ShowDotTopExtension(new TextBlock { Text = "合成消息" }, 30);
        var top = window.DotTopExtensionHeight;
        foreach (var available in new[] { 42d, 41d, 26d, 25d, 4d, 3d, 4d, 42d, 3d })
        {
            var work = new CodexQuotaTaskbar.Core.Overlay.ScreenRect(0, 0, 1000,
                (int)Math.Ceiling((baseline + top + available) * scale + 8));
            void Reconcile() => update.Invoke(coordinator, [window, work, scale, 0]);
            Reconcile();
            var increment = available >= 26 ? 26d : 0d;
            Assert.Equal(baseline + top + increment, window.DesiredHeight);
            Assert.Equal(baseline + increment, window.CoreBodyReservedHeight + (double)reservation.Invoke(null, [window])!);
            Assert.Equal(window.DesiredHeight, window.ReservedHeight);
            Assert.Equal(window.DesiredHeight, window.Height);
            Assert.True(window.DesiredHeight * scale + 8 <= work.Height + .01);
            Assert.Equal(ResetFooterKind.Status, window.ResetFooter);
            Assert.Equal(available < 26 ? Visibility.Visible : Visibility.Collapsed,
                Named<StackPanel>(window, "CompactResetNotice").Visibility);
            var generation = window.ExtensionMotionGeneration;
            var layout = window.ExtensionMotionLayoutCount;
            for (var repeat = 0; repeat < 12; repeat++) Reconcile();
            Assert.Equal(generation, window.ExtensionMotionGeneration);
            Assert.Equal(layout, window.ExtensionMotionLayoutCount);
            Assert.Equal(until, Private<DateTimeOffset?>(window, "confirmedResetUntil"));
            Assert.False(window.ResetScheduleAnimating);
        }
    });

    [Theory]
    [InlineData(96, 0)] [InlineData(144, 0)] [InlineData(192, 0)]
    [InlineData(96, 1)] [InlineData(144, 1)] [InlineData(192, 1)]
    [InlineData(96, 2)] [InlineData(144, 2)] [InlineData(192, 2)]
    public void Actual_dot_admission_keeps_original_minimum_and_is_independent_of_opening_order(int dpi, int state) => Sta(() =>
    {
        using var coordinator = new CodexQuotaTaskbar.Host.Overlay.OverlayCoordinator(false, unifiedPanel: true, observePointer: false);
        Private<System.Windows.Threading.DispatcherTimer>(coordinator, "timer").Stop();
        var availableTop = coordinator.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "AvailableDotTopHeight" && method.GetParameters().Length == 5);
        var now = DateTimeOffset.UtcNow;
        var scale = dpi / 96d;
        foreach (var remaining in new[] { 0d, 3d, 4d, 41d, 42d })
        {
            (double Height, Visibility Compact, Visibility Full)? previous = null;
            foreach (var alreadyOpen in new[] { false, true })
            {
                var window = new QuotaCapsuleWindow("synthetic-dot-admission");
                try
                {
                    var baseline = window.CoreBodyReservedHeight;
                    if (state == 0) window.ApplyResetQueryState(new(1, ResetQueryPhase.Failed));
                    if (state == 1) window.ApplyConfirmedReset(now.AddHours(1));
                    if (state == 2) window.ApplyResetSchedule(Fresh(now, now.AddHours(2)), now, China);
                    var until = Private<DateTimeOffset?>(window, "confirmedResetUntil");
                    var content = new TextBlock { Text = "合成 Dot 消息" };
                    if (alreadyOpen) window.ShowDotTopExtension(content, 36);
                    var work = new CodexQuotaTaskbar.Core.Overlay.ScreenRect(0, 0, 1000,
                        (int)Math.Ceiling((baseline + 37 + remaining) * scale + 8));
                    double Available() => (double)availableTop.Invoke(coordinator, [window, work, scale, 37d, 37d])!;
                    Assert.True(Available() >= 36, $"Dot needs its original 36 DIP content plus one border; remaining={remaining}, existing={alreadyOpen}");
                    if (!alreadyOpen) window.ShowDotTopExtension(content, 36);
                    Available();
                    var increment = state == 2 ? remaining >= 42 ? 42d : remaining >= 4 ? 4d : 0d : remaining >= 26 ? 26d : 0d;
                    Assert.Equal(baseline + 37 + increment, window.DesiredHeight);
                    Assert.Equal(window.DesiredHeight, window.ReservedHeight);
                    Assert.True(window.DesiredHeight * scale + 8 <= work.Height + .01);
                    var view = (window.DesiredHeight, Named<StackPanel>(window, "CompactResetNotice").Visibility,
                        Named<Border>(window, "ResetScheduleRow").Visibility);
                    if (previous is { } expected) Assert.Equal(expected, view);
                    previous = view;
                    var generation = window.ExtensionMotionGeneration;
                    for (var repeat = 0; repeat < 3; repeat++)
                    {
                        Available(); window.RefreshResetSchedule(now, China);
                        Assert.Same(content, window.DotTopContent);
                        Assert.Equal(generation, window.ExtensionMotionGeneration);
                        Assert.Equal(until, Private<DateTimeOffset?>(window, "confirmedResetUntil"));
                    }
                    Assert.False(window.IsVisible); Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Handle);
                }
                finally { window.Close(); }
            }
        }
    });

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void Actual_bongo_surface_keeps_compact_notice_quota_counts_and_dot_marker_in_the_same_tree(int state) => WithStatusBall((window, ball) =>
    {
        using var fixture = DpiFixture();
        var now = DateTimeOffset.UtcNow;
        window.ConfigureBongoSurface(true, 160);
        window.PresentBongoFrame(new CodexQuotaTaskbar.Host.Bongo.BongoFrame(1, 1, [255, 255, 255, 255], 4));
        Assert.True(window.BongoHasFrame);
        window.Apply(QuotaSnapshot.Available([new(QuotaWindowKind.Primary, 100, 300, now.AddHours(5)),
            new(QuotaWindowKind.Secondary, 100, 10080, now.AddDays(7))], now));
        window.ApplySessions(Sessions(100)); window.SetDotMarker(100, canAcknowledge: true);
        var baseline = window.CoreBodyDesiredHeight;
        if (state == 0) window.ApplyResetQueryState(new(1, ResetQueryPhase.Failed));
        if (state == 1) window.ApplyConfirmedReset(now.AddHours(1));
        if (state == 2) window.ApplyResetSchedule(Fresh(now, now.AddHours(1)) with { IsLatchedSchedule = true }, now, China);
        window.ShowDotTopExtension(new TextBlock { Text = "合成猫共存消息" }, 36);
        window.SetResetScheduleAvailableHeight(4);
        var root = Layout(window, 192);
        var compact = Named<StackPanel>(window, "CompactResetNotice");
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        Assert.Equal(Visibility.Visible, compact.Visibility);
        Assert.Equal(baseline + 37 + (state == 2 ? 4 : 0), window.DesiredHeight);
        Assert.True(Bounds(Named<Grid>(window, "QuotaHeader")).Bottom <= Bounds(compact).Top + .5);
        Assert.True(Bounds(compact).Bottom <= root.ActualHeight);
        Assert.Equal("100%", Named<TextBlock>(window, "FirstValue").Text);
        Assert.Equal("100%", Named<TextBlock>(window, "SecondValue").Text);
        Assert.True(window.DotMarkerVisible); Assert.True(window.BongoHasFrame);
        Assert.True(window.DotMarkerIsCatHead); Assert.Same(window, Window.GetWindow(compact));
        Assert.False(window.IsVisible); Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Handle);
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Visible_gold_full_or_compact_routes_a_managed_click_to_the_same_fixed_source(bool compact) => WithStatusBall((window, ball) =>
    {
        using var fixture = DpiFixture();
        var now = DateTimeOffset.UtcNow;
        window.ApplyResetSchedule(Fresh(now, now.AddHours(1)), now, China);
        window.ApplySessions(Sessions(3)); window.SetDotMarker(2, canAcknowledge: true);
        window.ShowDotTopExtension(new TextBlock { Text = "合成独立 Dot 消息" }, 36);
        window.SetResetScheduleAvailableHeight(compact ? 4 : 42);
        Layout(window, 144);
        var row = compact ? (FrameworkElement)Named<StackPanel>(window, "CompactResetNotice") : Named<Border>(window, "ResetScheduleRow");
        var point = row.TransformToAncestor(Named<Grid>(window, "Shell")).Transform(new Point(row.ActualWidth / 2, row.ActualHeight / 2));
        var opened = new List<Uri>(); window.OpenResetSourceRequested += opened.Add;
        var quota = 0; var companion = 0; var dot = 0;
        window.PrimaryInvoked += (_, _) => quota++; window.CompanionInvoked += (_, _) => companion++;
        window.DotMarkerInvoked += (_, _) => dot++;
        var down = window.PreparePointerDown(point, System.Windows.Input.MouseButton.Left);
        Assert.Equal(IslandHitRegion.ResetNotice, down);
        Assert.True(window.BeginPointerGesture(System.Windows.Input.MouseButton.Left, down, 801, 0, 0, () => true));
        Assert.True(window.EndPointerGesture(System.Windows.Input.MouseButton.Left, window.PointerRegion(point), 802));
        Assert.Equal(CodexResetFeedClient.SiteUri, Assert.Single(opened));
        Assert.Equal(0, quota); Assert.Equal(0, companion); Assert.Equal(0, dot);
        Assert.True(window.DotTopExtensionVisible); Assert.True(window.DotMarkerVisible);
    });

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void Compact_gold_gesture_cannot_open_after_failure_confirmation_or_space_loss(int replacement) => WithStatusBall((window, ball) =>
    {
        using var fixture = DpiFixture();
        var now = DateTimeOffset.UtcNow;
        window.ApplyResetSchedule(Fresh(now, now.AddHours(1)), now, China);
        window.SetResetScheduleAvailableHeight(4); Layout(window, 192);
        var compact = Named<StackPanel>(window, "CompactResetNotice");
        var point = compact.TransformToAncestor(Named<Grid>(window, "Shell")).Transform(new Point(compact.ActualWidth / 2, compact.ActualHeight / 2));
        var opened = new List<Uri>(); window.OpenResetSourceRequested += opened.Add;
        var quota = 0; window.PrimaryInvoked += (_, _) => quota++;
        var down = window.PreparePointerDown(point, System.Windows.Input.MouseButton.Left);
        Assert.Equal(IslandHitRegion.ResetNotice, down);
        Assert.True(window.BeginPointerGesture(System.Windows.Input.MouseButton.Left, down, 901, 0, 0, () => true));
        if (replacement == 0) window.ApplyResetQueryState(new(1, ResetQueryPhase.Failed));
        if (replacement == 1) window.ApplyConfirmedReset(now.AddHours(1));
        if (replacement == 2) window.SetResetScheduleAvailableHeight(3);
        Layout(window, 192);
        var release = window.PointerRegion(point);
        Assert.NotEqual(IslandHitRegion.ResetNotice, release);
        if (replacement != 2) Assert.Equal(IslandHitRegion.None, release);
        Assert.True(window.EndPointerGesture(System.Windows.Input.MouseButton.Left, release, 902));
        Assert.Empty(opened); Assert.Equal(0, quota);
        // A retained/stale release classification is also rechecked at dispatch.
        Assert.True(window.BeginPointerGesture(System.Windows.Input.MouseButton.Left, down, 903, 0, 0, () => true));
        Assert.True(window.EndPointerGesture(System.Windows.Input.MouseButton.Left, down, 904));
        Assert.Empty(opened); Assert.Equal(0, quota);
    });

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void Classic_layout_keeps_its_existing_rows_and_has_no_automatic_footer(int state) => Sta(() =>
    {
        var window = new QuotaCapsuleWindow("classic-reset-boundary", false);
        try
        {
            var baseline = window.DesiredHeight;
            var header = Named<Grid>(window, "QuotaHeader");
            var rows = header.RowDefinitions.Select(row => row.Height).ToArray();
            var now = DateTimeOffset.UtcNow;
            if (state == 0) window.ApplyResetQueryState(new(1, ResetQueryPhase.Failed));
            if (state == 1) window.ApplyConfirmedReset(now.AddHours(1));
            if (state == 2) window.ApplyResetSchedule(Fresh(now, now.AddHours(1)), now);
            window.SetResetSchedulePresentationAllowed(false);
            Assert.Equal(ResetFooterKind.None, window.ResetFooter);
            Assert.Equal(baseline, window.DesiredHeight);
            Assert.Equal(rows, header.RowDefinitions.Select(row => row.Height));
            Assert.Equal(Visibility.Collapsed, Named<StackPanel>(window, "CompactResetNotice").Visibility);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Locked_schedule_exposes_absolute_local_time_without_a_tooltip()
    {
        var view = ResetScheduleRowPresentation.Create(Fresh() with { IsLatchedSchedule = true }, Now, China);
        Assert.Contains("2026-10-24 01:00", view.LocalTime, StringComparison.Ordinal);
        Assert.Contains("首次锁定", view.Tooltip, StringComparison.Ordinal);
        var due = ResetScheduleRowPresentation.Create(Fresh() with { IsLatchedSchedule = true }, Now.AddHours(2), China);
        Assert.Equal("预计时间已到，等待额度恢复", due.Countdown);
        Assert.Equal(view.LocalTime, due.LocalTime);
    }

    [Fact]
    public void Visible_text_contains_only_approximate_remaining_and_local_time()
    {
        var nextDay = ResetScheduleRowPresentation.Create(Fresh(), Now.AddMinutes(-1), China);
        Assert.True(nextDay.IsVisible);
        Assert.Equal("约 1 小时后重置", nextDay.Countdown);
        Assert.Equal("本地 2026-10-24 01:00", nextDay.LocalTime);
        Assert.Contains("第三方", nextDay.Tooltip, StringComparison.Ordinal);
        Assert.Contains("并非本账号", nextDay.Tooltip, StringComparison.Ordinal);
        Assert.Contains(Announcement.ToString(), nextDay.Tooltip, StringComparison.Ordinal);
        var today = ResetScheduleRowPresentation.Create(Fresh(), Now, TimeZoneInfo.Utc);
        Assert.Equal("本地 2026-10-23 17:00", today.LocalTime);
    }

    [Theory]
    [InlineData("2026-07-23T17:00:00Z", "13:00")]
    [InlineData("2026-01-23T17:00:00Z", "12:00")]
    public void Local_time_observes_dst_at_the_scheduled_instant(string target, string localTime)
    {
        var deadline = DateTimeOffset.Parse(target);
        var clock = deadline.AddHours(-1);
        var view = ResetScheduleRowPresentation.Create(Fresh(clock, deadline), clock, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
        Assert.Equal("本地 " + target[..10] + " " + localTime, view.LocalTime);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Unknown_or_uncertain_data_hides_the_entire_row(int scenario)
    {
        var snapshot = scenario switch
        {
            0 => ResetScheduleSnapshot.Loading,
            1 => new(null, Now, false, null),
            2 => Fresh() with { Schedule = Fresh().Schedule! with { ScheduledFor = null } },
            3 => Fresh() with { IsStale = true },
            4 => Fresh() with { ErrorMessage = "offline" },
            5 => Fresh() with { LastUpdatedAt = Now.AddMinutes(-3) },
            6 => Fresh() with { LastUpdatedAt = null },
            _ => Fresh(Now, Now),
        };
        var view = ResetScheduleRowPresentation.Create(snapshot, Now, China);
        Assert.False(view.IsVisible);
        Assert.Empty(view.Countdown);
        Assert.Empty(view.LocalTime);
    }

    [Fact]
    public void Revalidation_uses_checked_time_and_does_not_reexpire_the_cached_generation()
    {
        Assert.True(ResetScheduleRowPresentation.Create(Fresh() with { SourceGeneratedAt = Now.AddDays(-1) }, Now, China).IsVisible);
        Assert.True(ResetScheduleRowPresentation.Create(Fresh(), Now.AddMinutes(3).AddTicks(-1), China).IsVisible);
        Assert.False(ResetScheduleRowPresentation.Create(Fresh(), Now.AddMinutes(3), China).IsVisible);
        Assert.True(ResetScheduleRowPresentation.Create(Fresh() with { LastUpdatedAt = Now.AddMinutes(3) }, Now.AddMinutes(3), China).IsVisible);
    }

    [Fact]
    public void Countdown_is_minute_grained_and_stops_at_the_deadline()
    {
        var snapshot = Fresh(Now, Now.AddMinutes(59));
        var minute = ResetScheduleRowPresentation.Create(snapshot, Now, China);
        Assert.Equal("约 59 分钟后重置", minute.Countdown);
        for (var seconds = 1; seconds < 60; seconds++)
            Assert.Equal(minute, ResetScheduleRowPresentation.Create(snapshot, Now.AddSeconds(seconds), China));
        Assert.Equal("约 58 分钟后重置", ResetScheduleRowPresentation.Create(snapshot, Now.AddMinutes(1), China).Countdown);
        var final = Fresh(Now, Now.AddMilliseconds(1));
        Assert.Equal("约 1 分钟后重置", ResetScheduleRowPresentation.Create(final, Now, China).Countdown);
        Assert.False(ResetScheduleRowPresentation.Create(final, Now.AddMilliseconds(1), China).IsVisible);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void Footer_adds_only_its_own_height_and_restores_original_height_without_rebuilding_tasks(int count) => Sta(() =>
    {
        var window = new QuotaCapsuleWindow("schedule-row-test");
        try
        {
            window.ApplySessions(Sessions(count));
            var list = Named<StackPanel>(window, "PanelSessionsList");
            var buttons = list.Children.Cast<UIElement>().ToArray();
            var baseHeight = window.DesiredHeight;
            var reserved = window.ReservedHeight;
            var changes = 0;
            window.PresentationChanged += (_, _) => changes++;
            window.ApplyResetSchedule(Fresh(), Now, China);
            Assert.Equal(baseHeight + QuotaCapsuleWindow.ResetScheduleRowHeight, window.DesiredHeight);
            Assert.Equal(reserved + QuotaCapsuleWindow.ResetScheduleRowHeight, window.ReservedHeight);
            Assert.Equal(window.DesiredHeight, window.Height);
            Assert.Equal(1, changes);
            window.ApplyResetSchedule(Fresh(), Now, China);
            for (var index = 0; index < 120; index++) window.RefreshResetSchedule(Now.AddSeconds(index), China);
            Assert.Equal(1, changes);
            Assert.Equal(buttons, list.Children.Cast<UIElement>());
            Assert.All(list.Children.Cast<UIElement>(), row => Assert.IsType<System.Windows.Controls.Button>(row));
            window.ApplyResetSchedule(new(null, Now.AddMinutes(2), false, null), Now.AddMinutes(2), China);
            Assert.Equal(baseHeight, window.DesiredHeight);
            Assert.Equal(reserved, window.ReservedHeight);
            Assert.Equal(baseHeight, window.Height);
            Assert.Equal(Visibility.Collapsed, Named<Border>(window, "ResetScheduleRow").Visibility);
            Assert.Equal(2, changes);
            Assert.Equal(buttons, list.Children.Cast<UIElement>());
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(96)]
    [InlineData(108)]
    [InlineData(144)]
    public void Footer_divider_moves_up_four_dip_without_moving_other_layout_and_hides_with_the_row(int dpi) => Sta(() =>
    {
        using var fixture = DpiFixture();
        var window = new QuotaCapsuleWindow("schedule-divider-test");
        try
        {
            window.ApplySessions(Sessions(3));
            var baselineHeight = window.DesiredHeight;
            var region = Named<Grid>(window, "PanelSessionsRegion");
            var taskDivider = Assert.IsType<Border>(Assert.Single(region.Children.Cast<UIElement>()));
            var footer = Named<Border>(window, "ResetScheduleRow");
            var divider = Named<Border>(window, "ResetScheduleDivider");
            Assert.Equal(Assert.IsType<SolidColorBrush>(taskDivider.BorderBrush).Color,
                Assert.IsType<SolidColorBrush>(divider.BorderBrush).Color);
            Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(footer.BorderBrush).Color);
            Assert.Equal(new Thickness(0, 1, 0, 0), divider.BorderThickness);
            Assert.Equal(taskDivider.BorderThickness, divider.BorderThickness);
            Assert.Equal(region.Margin.Left, footer.Margin.Left);
            Assert.Equal(region.Margin.Right, footer.Margin.Right);
            Assert.Same(footer, Assert.IsType<Grid>(divider.Parent).Parent);

            foreach (var hidden in new[]
            {
                new ResetScheduleSnapshot(null, Now, false, null),
                Fresh() with { IsStale = true },
                Fresh(Now, Now),
            })
            {
                window.ApplyResetSchedule(Fresh(), Now, China);
                var content = Layout(window, dpi);
                Rect Bounds(FrameworkElement element) => element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));
                var taskBounds = Bounds(taskDivider);
                var footerBounds = Bounds(footer);
                var dividerBounds = Bounds(divider);
                Assert.Equal(Visibility.Visible, footer.Visibility);
                Assert.Equal(taskBounds.Left, dividerBounds.Left, 6);
                Assert.Equal(taskBounds.Right, dividerBounds.Right, 6);
                Assert.Equal(footerBounds.Top - 4, dividerBounds.Top, 6);
                Assert.Equal(1, dividerBounds.Height, 6);
                Assert.Equal(baselineHeight + QuotaCapsuleWindow.ResetScheduleRowHeight, window.DesiredHeight);

                var shifted = divider.RenderTransform;
                Assert.Equal(-4, shifted.Value.OffsetY, 6);
                var countdown = Named<TextBlock>(window, "ResetScheduleCountdown");
                var localTime = Named<TextBlock>(window, "ResetScheduleLocalTime");
                var tasks = Named<StackPanel>(window, "PanelSessionsList");
                var header = Named<Grid>(window, "QuotaHeader");
                divider.RenderTransform = Transform.Identity;
                Layout(window, dpi);
                var unshiftedDividerBounds = Bounds(divider);
                var unchanged = new[] { Bounds(countdown), Bounds(localTime), Bounds(tasks), Bounds(header), Bounds(footer) };
                var unchangedSize = new Size(window.Width, window.Height);
                var unchangedReservedHeight = window.ReservedHeight;
                divider.RenderTransform = shifted;
                Layout(window, dpi);
                Assert.Equal(unshiftedDividerBounds.Top - 4, Bounds(divider).Top, 6);
                Assert.Equal(unshiftedDividerBounds.Left, Bounds(divider).Left, 6);
                Assert.Equal(unshiftedDividerBounds.Right, Bounds(divider).Right, 6);
                Assert.Equal(unchanged, new[] { Bounds(countdown), Bounds(localTime), Bounds(tasks), Bounds(header), Bounds(footer) });
                Assert.Equal(unchangedSize, new Size(window.Width, window.Height));
                Assert.Equal(unchangedReservedHeight, window.ReservedHeight);
                var lastTask = Assert.IsType<System.Windows.Controls.Button>(tasks.Children[tasks.Children.Count - 1]);
                Assert.True(Bounds(lastTask).Bottom <= Bounds(divider).Top, "Raised divider must stay below the task row.");

                window.ApplyResetSchedule(hidden, Now, China);
                Layout(window, dpi);
                Assert.Equal(Visibility.Collapsed, footer.Visibility);
                Assert.Equal(0, footer.DesiredSize.Height);
                Assert.Equal(baselineHeight, window.DesiredHeight);
                Assert.Equal(baselineHeight, window.Height);
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Minute_updates_do_not_reassign_unchanged_text_or_emit_layout_events() => Sta(() =>
    {
        var window = new QuotaCapsuleWindow("schedule-clock-test");
        var text = Named<TextBlock>(window, "ResetScheduleCountdown");
        var descriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))!;
        var textChanges = 0;
        var layoutChanges = 0;
        EventHandler changed = (_, _) => textChanges++;
        try
        {
            window.ApplyResetSchedule(Fresh(Now, Now.AddMinutes(59)), Now, China);
            window.PresentationChanged += (_, _) => layoutChanges++;
            descriptor.AddValueChanged(text, changed);
            for (var index = 0; index < 80; index++) window.RefreshResetSchedule(Now.AddMilliseconds(index * 750), China);
            Assert.Equal(0, textChanges);
            window.RefreshResetSchedule(Now.AddMinutes(1), China);
            Assert.Equal(1, textChanges);
            Assert.Equal(0, layoutChanges);
            window.RefreshResetSchedule(Now.AddMinutes(3), China);
            Assert.Equal(1, layoutChanges); // Freshness expiry removes the complete footer once.
        }
        finally { descriptor.RemoveValueChanged(text, changed); window.Close(); }
    });

    [Fact]
    public void Session_collapse_and_empty_transition_keep_footer_separate_and_classic_is_unchanged() => Sta(() =>
    {
        foreach (var unified in new[] { false, true })
        {
            var window = new QuotaCapsuleWindow("schedule-modes-test", unified);
            try
            {
                window.ApplySessions(Sessions(5));
                window.ApplyResetSchedule(Fresh(), Now, China);
                if (!unified)
                {
                    Assert.Equal(100, window.Height); // Unknown/single quota occupies one compact row.
                    Assert.Equal(100, window.DesiredHeight);
                    Assert.Equal(Visibility.Collapsed, Named<Border>(window, "ResetScheduleRow").Visibility);
                    continue;
                }
                window.TogglePanelSessions();
                Assert.Equal(132 + QuotaCapsuleWindow.ResetScheduleRowHeight, window.DesiredHeight);
                Assert.Equal(200 + QuotaCapsuleWindow.ResetScheduleRowHeight, window.ReservedHeight);
                window.ApplySessions(CodexSessionsSnapshot.Empty);
                Assert.Equal(80 + QuotaCapsuleWindow.ResetScheduleRowHeight, window.DesiredHeight);
                window.ApplyResetSchedule(new(null, Now, false, null), Now, China);
                Assert.Equal(80, window.DesiredHeight);
            }
            finally { window.Close(); }
        }
    });

    [Fact]
    public void Tooltip_credit_raises_only_the_existing_fixed_site_event_without_navigating() => Sta(() =>
    {
        var window = new QuotaCapsuleWindow("schedule-credit-test");
        try
        {
            var opened = new List<Uri>();
            window.OpenResetSourceRequested += opened.Add;
            window.ApplyResetSchedule(Fresh(), Now, China);
            var tooltip = Assert.IsType<ToolTip>(Named<Border>(window, "ResetScheduleRow").ToolTip);
            Assert.False(tooltip.Focusable);
            var source = Assert.Single(Assert.IsType<TextBlock>(tooltip.Content).Inlines.OfType<System.Windows.Documents.Hyperlink>());
            Assert.Equal(CodexResetFeedClient.SiteUri, source.NavigateUri);
            source.RaiseEvent(new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent));
            Assert.Equal(CodexResetFeedClient.SiteUri, Assert.Single(opened));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(96, 280)]
    [InlineData(108, 280)]
    [InlineData(144, 280)]
    [InlineData(144, 230)]
    public void Row_stays_below_tasks_and_preserves_the_local_time_at_narrow_width(int dpi, int width) => Sta(() =>
    {
        using var fixture = DpiFixture();
        var window = new QuotaCapsuleWindow("schedule-dpi-test") { Width = width };
        try
        {
            window.ApplySessions(Sessions(3));
            var content = Layout(window, dpi);
            var header = Named<Grid>(window, "QuotaHeader");
            var headerBefore = header.TransformToAncestor(content).TransformBounds(new Rect(header.RenderSize));
            window.ApplyResetSchedule(Fresh(), Now, China);
            Layout(window, dpi);
            var row = Named<Border>(window, "ResetScheduleRow");
            var list = Named<StackPanel>(window, "PanelSessionsList");
            var taskBounds = list.TransformToAncestor(content).TransformBounds(new Rect(list.RenderSize));
            var rowBounds = row.TransformToAncestor(content).TransformBounds(new Rect(row.RenderSize));
            Assert.True(rowBounds.Top >= taskBounds.Bottom);
            Assert.Equal(headerBefore, header.TransformToAncestor(content).TransformBounds(new Rect(header.RenderSize)));
            var time = Named<TextBlock>(window, "ResetScheduleLocalTime");
            var countdown = Named<TextBlock>(window, "ResetScheduleCountdown");
            Assert.Equal(TextWrapping.NoWrap, countdown.TextWrapping);
            Assert.Equal(TextTrimming.None, countdown.TextTrimming);
            var formatted = new FormattedText(time.Text, CultureInfo.GetCultureInfo("zh-CN"), time.FlowDirection,
                new Typeface(time.FontFamily, time.FontStyle, time.FontWeight, time.FontStretch), time.FontSize, time.Foreground, dpi / 96d);
            Assert.True(time.ActualWidth + 0.5 >= formatted.WidthIncludingTrailingWhitespace);
            Assert.InRange(rowBounds.Right, 0, width);
            Assert.Equal(QuotaCapsuleWindow.ResetScheduleRowHeight, row.ActualHeight);
            window.ApplyResetSchedule(new(null, Now, false, null), Now, China);
            Layout(window, dpi);
            Assert.Equal(200, content.ActualHeight);
            Assert.Equal(0, row.DesiredSize.Height);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Optional_preview_renders_the_complete_native_panel_without_showing_a_window() => Sta(() =>
    {
        var configured = Environment.GetEnvironmentVariable("CQT_RESET_ROW_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(configured)) return;
        var directory = Path.GetFullPath(configured);
        Assert.Equal(Path.GetFullPath(Path.Combine(RepositoryPaths.Root, "..", "reset-schedule-row-preview")), directory, ignoreCase: true);
        Directory.CreateDirectory(directory);
        using var fixture = DpiFixture();
        foreach (var dpi in new[] { 96, 108, 144 })
        foreach (var scheduled in new[] { false, true })
        foreach (var width in dpi == 144 ? new[] { 280, 230 } : new[] { 280 })
        {
            var window = new QuotaCapsuleWindow("schedule-offscreen-preview") { Width = width };
            // Use the actual native vector character, static/offscreen, without starting its cursor sampler or local diagnostics.
            using var character = new CompanionBallVisual
            {
                Width = 56, Height = 56, HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
            };
            try
            {
                character.SetState("02", false);
                Named<Grid>(window, "Shell").Children.Add(character);
                System.Windows.Controls.Panel.SetZIndex(character, 5);
                Named<Grid>(window, "QuotaHeader").Margin = new Thickness(14, 0, 46, 0);
                window.Apply(QuotaSnapshot.Available([
                    new(QuotaWindowKind.Primary, 26, 300, Now.AddHours(3)),
                    new(QuotaWindowKind.Secondary, 26, 10080, Now.AddDays(5)),
                ], Now));
                window.ApplySessions(Sessions(3));
                // Companion readiness normally hides the old badge. This fixture never loads an HWND.
                Named<Border>(window, "SessionBadge").Visibility = Visibility.Collapsed;
                window.ApplyResetSchedule(scheduled ? Fresh() : new(null, Now, false, null), Now, China);
                var content = Layout(window, dpi);
                Assert.False(window.IsVisible);
                Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Handle);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.Width * dpi / 96d),
                    (int)Math.Ceiling(window.DesiredHeight * dpi / 96d), dpi, dpi, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(directory, $"{(scheduled ? "scheduled" : "none")}-{dpi}{(width == 230 ? "-narrow" : string.Empty)}.png"));
                encoder.Save(output);
            }
            finally { window.Close(); }
        }
    });

    [Fact]
    public void Normal_and_checking_base_hover_have_no_description_or_tooltip() => WithStatusBall((window, ball) =>
    {
        var height = window.DesiredHeight;
        var generation = window.ExtensionMotionGeneration;
        foreach (var phase in new[] { ResetQueryPhase.Idle, ResetQueryPhase.Checking })
        {
            window.ApplyResetQueryState(new((long)phase + 1, phase));
            ball.SetResetHintHovered(true);
            Assert.Equal(ResetFooterKind.None, window.ResetFooter);
            Assert.Equal(height, window.DesiredHeight);
            Assert.Equal(Visibility.Collapsed, Named<Border>(window, "ResetScheduleRow").Visibility);
            Assert.Null(Named<Border>(window, "ResetScheduleRow").ToolTip);
            Assert.False(ToolTipService.GetIsEnabled(ball));
            ball.SetResetHintHovered(false);
        }
        Assert.Equal(generation, window.ExtensionMotionGeneration);
    });

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Special_description_reuses_footer_and_leaves_without_navigation_or_new_clocks(bool green) => WithStatusBall((window, ball) =>
    {
        var until = DateTimeOffset.UtcNow.AddHours(1);
        if (green) window.ApplyConfirmedReset(until);
        else window.ApplyResetQueryState(new(1, ResetQueryPhase.Failed));
        var height = window.DesiredHeight;
        var row = Named<Border>(window, "ResetScheduleRow");
        var opened = new List<Uri>(); window.OpenResetSourceRequested += opened.Add;
        Assert.Equal(ResetFooterKind.Status, window.ResetFooter);
        for (var repeat = 0; repeat < 8; repeat++)
        {
            ball.SetResetHintHovered(true);
            Assert.Equal(ResetFooterKind.Status, window.ResetFooter);
            Assert.Equal(height, window.DesiredHeight);
            Assert.Same(row, Named<Border>(window, "ResetScheduleRow"));
            Assert.Equal(green ? "额度已回升" : "重置预告查询失败", Named<TextBlock>(window, "ResetScheduleCountdown").Text);
            Assert.False(row.IsHitTestVisible); Assert.Null(row.ToolTip);
            var generation = window.ExtensionMotionGeneration;
            ball.SetResetHintHovered(true);
            Assert.Equal(generation, window.ExtensionMotionGeneration);
            window.InvokeResetFooter();
            ball.SetResetHintHovered(false);
            Assert.Equal(height, window.DesiredHeight);
        }
        Assert.Empty(opened);
        Assert.Equal(green ? until : (DateTimeOffset?)null, Private<DateTimeOffset?>(window, "confirmedResetUntil"));
        Assert.False(Private<System.Windows.Threading.DispatcherTimer>(ball, "timer").IsEnabled);
        Assert.False(window.ResetScheduleAnimating); // No visible window: no native animation clock.
        Assert.False(Private<bool>(window, "extensionRenderAttached"));
    });

    [Fact]
    public void Schedule_status_replacement_disables_stale_links_and_never_revives_consumed_notice() => WithStatusBall((window, ball) =>
    {
        // Keep both accepted samples in the past: future recovery deadlines are
        // intentionally rejected by the existing one-hour replay guard.
        var now = DateTimeOffset.UtcNow.AddSeconds(-2);
        var snapshot = Fresh(now, now.AddHours(3));
        var tracker = new QuotaResetTracker(); // In-memory only.
        window.SetResetTracker(tracker); tracker.SetNotice(snapshot.Schedule);
        var scope = QuotaResetTracker.ScopeForSource("SYNTHETIC-FOOTER-TEST")!;
        QuotaSnapshot Sample(double remaining, DateTimeOffset time) => QuotaSnapshot.Available(
            [new(QuotaWindowKind.Primary, remaining, 300, now.AddHours(5))], time);
        tracker.Observe(Sample(20, now), scope, now);
        window.ApplyResetSchedule(snapshot, now);
        var row = Named<Border>(window, "ResetScheduleRow");
        var tip = Assert.IsType<ToolTip>(row.ToolTip);
        var link = Assert.Single(Assert.IsType<TextBlock>(tip.Content).Inlines.OfType<System.Windows.Documents.Hyperlink>());
        var opened = new List<Uri>(); window.OpenResetSourceRequested += opened.Add;
        ball.SetResetHintHovered(true);
        Assert.Equal(ResetFooterKind.Schedule, window.ResetFooter); Assert.Same(tip, row.ToolTip);
        window.InvokeResetFooter(); Assert.Equal(CodexResetFeedClient.SiteUri, Assert.Single(opened));
        window.ApplyResetQueryState(new(1, ResetQueryPhase.Failed));
        Assert.Equal(ResetFooterKind.Status, window.ResetFooter); Assert.Null(row.ToolTip);
        link.RaiseEvent(new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent));
        window.InvokeResetFooter(); Assert.Single(opened);
        window.ApplyResetQueryState(new(2, ResetQueryPhase.Idle));
        Assert.Equal(ResetFooterKind.Schedule, window.ResetFooter);
        var until = tracker.Observe(Sample(40, now.AddSeconds(1)), scope, now.AddSeconds(1));
        Assert.NotNull(until); Assert.True(tracker.IsNoticeConsumed(snapshot.Schedule));
        window.ApplyConfirmedReset(until);
        window.ApplyResetQueryState(new(3, ResetQueryPhase.Failed)); // Green keeps priority over a query error.
        Assert.Equal("额度已回升", Named<TextBlock>(window, "ResetScheduleCountdown").Text);
        Assert.Equal(ResetFooterKind.Status, window.ResetFooter);
        link.RaiseEvent(new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent));
        Assert.Single(opened);
        ball.SetResetHintHovered(false);
        Assert.Equal(ResetFooterKind.Status, window.ResetFooter);
        window.ApplyResetQueryState(new(4, ResetQueryPhase.Idle));
        var later = until!.Value.AddSeconds(1);
        window.ApplyResetSchedule(snapshot with { LastUpdatedAt = later }, later);
        window.SetResetStatusHintHovered(true, later);
        Assert.Equal(ResetFooterKind.None, window.ResetFooter);
        Assert.Equal(until, Private<DateTimeOffset?>(window, "confirmedResetUntil"));
        window.InvokeResetFooter(); Assert.Single(opened);
    });

    [Fact]
    public void Dot_presentation_and_pointer_gestures_preserve_automatic_status() => WithStatusBall((window, ball) =>
    {
        window.ApplyResetQueryState(new(1, ResetQueryPhase.Failed));
        ball.SetResetHintHovered(true);
        Assert.Equal(ResetFooterKind.Status, window.ResetFooter);
        window.SetDotMarker(2, canAcknowledge: true);
        var marker = window.DotMarkerButton;
        var dot = new TextBlock { Text = "合成 Dot 消息" };
        window.ShowDotTopExtension(dot, 30);
        Assert.Equal(ResetFooterKind.Status, window.ResetFooter);
        window.SetResetStatusHintHovered(true);
        Assert.Equal(ResetFooterKind.Status, window.ResetFooter);
        Assert.Same(dot, window.DotTopContent); Assert.Same(marker, window.DotMarkerButton);
        window.HideDotTopExtension();
        Assert.Equal(ResetFooterKind.Status, window.ResetFooter);
        var ballClicks = 0; window.CompanionInvoked += (_, _) => ballClicks++;
        Assert.True(window.BeginPointerGesture(System.Windows.Input.MouseButton.Left, IslandHitRegion.Ball, 100, 0, 0, () => true));
        Assert.Equal(ResetFooterKind.Status, window.ResetFooter);
        window.EndPointerGesture(System.Windows.Input.MouseButton.Left, IslandHitRegion.Ball, 110);
        Assert.Equal(1, ballClicks); Assert.False(window.HasPointerGesture);
        Assert.Equal(ResetFooterKind.Status, window.ResetFooter); // No leave/re-enter needed.
        ball.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Assert.Equal(ResetFooterKind.Status, window.ResetFooter);
        Assert.False(Private<System.Windows.Threading.DispatcherTimer>(ball, "timer").IsEnabled);
        Assert.False(Private<bool>(window, "extensionRenderAttached"));
    });

    [Theory]
    [InlineData(96, 0)] [InlineData(144, 0)] [InlineData(192, 0)]
    [InlineData(96, 1)] [InlineData(144, 1)] [InlineData(192, 1)]
    [InlineData(96, 2)] [InlineData(144, 2)] [InlineData(192, 2)]
    [InlineData(96, 3)] [InlineData(144, 3)] [InlineData(192, 3)]
    public void Automatic_footer_and_compact_fallback_are_complete_without_overlapping_quota_or_counts(int dpi, int state) => WithStatusBall((window, ball) =>
    {
        using var fixture = DpiFixture();
        window.Width = 230;
        var now = DateTimeOffset.UtcNow;
        window.Apply(QuotaSnapshot.Available([new(QuotaWindowKind.Primary, 100, 300, now.AddHours(5)),
            new(QuotaWindowKind.Secondary, 100, 10080, now.AddDays(7))], now));
        window.ApplySessions(Sessions(100));
        window.SetDotMarker(100, canAcknowledge: true);
        var baseline = window.DesiredHeight;
        if (state == 0) window.ApplyResetQueryState(new(1, ResetQueryPhase.Failed));
        if (state == 1) window.ApplyConfirmedReset(now.AddHours(1));
        if (state >= 2) window.ApplyResetSchedule(Fresh(now, state == 3 ? now.AddMinutes(-1) : now.AddHours(2)) with { IsLatchedSchedule = true }, now, China);
        var expected = state >= 2 ? ResetFooterKind.Schedule : ResetFooterKind.Status;
        var until = Private<DateTimeOffset?>(window, "confirmedResetUntil");
        void Fits(TextBlock text)
        {
            var measured = new FormattedText(text.Text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.White, dpi / 96d);
            Assert.True(measured.WidthIncludingTrailingWhitespace <= text.ActualWidth + .5, text.Text + " width");
            Assert.True(measured.Height <= text.ActualHeight + .5, text.Name + " height " + measured.Height + " actual " + text.ActualHeight);
        }
        Layout(window, dpi);
        Fits(Named<TextBlock>(window, "ResetScheduleCountdown"));
        if (state >= 2) Fits(Named<TextBlock>(window, "ResetScheduleLocalTime"));
        Assert.Equal(baseline + (state >= 2 ? 42 : 26), window.DesiredHeight);
        var generation = window.ExtensionMotionGeneration;
        window.SetResetSchedulePresentationAllowed(false);
        var root = Layout(window, dpi);
        Assert.Equal(baseline + (state >= 2 ? 4 : 0), window.DesiredHeight);
        Assert.Equal(expected, window.ResetFooter);
        var compact = Named<StackPanel>(window, "CompactResetNotice");
        Assert.Equal(Visibility.Visible, compact.Visibility);
        Fits(Named<TextBlock>(window, "CompactResetCountdown"));
        if (state >= 2) Fits(Named<TextBlock>(window, "CompactResetLocalTime"));
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        var bounds = Bounds(compact);
        foreach (var element in new FrameworkElement[] { Named<Grid>(window, "FirstRow"), Named<Grid>(window, "SecondRow"), Named<System.Windows.Controls.Button>(window, "SessionToggle") })
            Assert.True(Bounds(element).Bottom <= bounds.Top + .5, element.Name + " must remain above compact notice");
        foreach (var name in new[] { "FirstLabel", "FirstValue", "SecondLabel", "SecondValue" }) Fits(Named<TextBlock>(window, name));
        foreach (var name in new[] { "FirstBar", "SecondBar" }) Assert.True(Named<ProgressBar>(window, name).ActualHeight > 0);
        window.UpdateResetStatusPointer(new Point(-100, -100)); ball.SetResetHintHovered(false);
        window.SetResetSchedulePresentationAllowed(false);
        Assert.Equal(expected, window.ResetFooter);
        window.SetResetSchedulePresentationAllowed(true);
        Assert.Equal(expected, window.ResetFooter);
        Assert.Equal(baseline + (state >= 2 ? 42 : 26), window.DesiredHeight);
        Assert.Equal(until, Private<DateTimeOffset?>(window, "confirmedResetUntil"));
        var recoveredGeneration = window.ExtensionMotionGeneration;
        window.SetResetSchedulePresentationAllowed(true);
        Assert.Equal(recoveredGeneration, window.ExtensionMotionGeneration);
        Assert.True(recoveredGeneration > generation);
        window.SetCompanion(false, true);
        Assert.Equal(expected, window.ResetFooter);
        Assert.Equal(Visibility.Visible, Named<Border>(window, "ResetScheduleRow").Visibility);
    });
    [Fact]
    public void Schedule_mouse_up_after_query_failure_cannot_navigate() => WithStatusBall((window, ball) =>
    {
        window.ApplyResetSchedule(Fresh(DateTimeOffset.UtcNow));
        var opened = 0; window.OpenResetSourceRequested += _ => opened++;
        Assert.True(window.BeginPointerGesture(System.Windows.Input.MouseButton.Left, IslandHitRegion.ResetNotice,
            100, 0, 0, () => true));
        window.ApplyResetQueryState(new(1, ResetQueryPhase.Failed));
        window.EndPointerGesture(System.Windows.Input.MouseButton.Left, IslandHitRegion.ResetNotice, 110);
        Assert.Equal(0, opened); Assert.False(window.HasPointerGesture);
    });

    [Fact]
    public void Hover_corridor_is_retained_only_after_ball_entry_and_never_expands_click_targets()
    {
        var ball = new Rect(220, -2, 56, 56); var card = new Rect(16, 16, 248, 130);
        foreach (var dpi in new[] { 96d, 144d, 192d })
        foreach (var origin in new[] { new Point(-1920, -300), new Point(300, 200) })
        {
            var scale = dpi / 96;
            foreach (var local in new[] { new Point(221, 1), new Point(247, 25), new Point(274, 50) })
            {
                var screen = new Point(origin.X + local.X * scale, origin.Y + local.Y * scale);
                var roundtrip = new Point((screen.X - origin.X) / scale, (screen.Y - origin.Y) / scale);
                Assert.True(QuotaCapsuleWindow.IsResetStatusHoverPoint(roundtrip, ball, card, false));
            }
            Assert.False(QuotaCapsuleWindow.IsResetStatusHoverPoint(new Point(100, 100), ball, card, false));
            Assert.True(QuotaCapsuleWindow.IsResetStatusHoverPoint(new Point(100, 100), ball, card, true));
            Assert.False(QuotaCapsuleWindow.IsResetStatusHoverPoint(new Point(15, 100), ball, card, true));
            Assert.False(QuotaCapsuleWindow.IsResetStatusHoverPoint(new Point(250, 147), ball, card, true));
        }
        Assert.False(QuotaCapsuleWindow.IsResetStatusHoverPoint(new Point(), Rect.Empty, card, true));
    }

    [Theory]
    [InlineData(96, false)] [InlineData(144, false)] [InlineData(192, false)]
    [InlineData(96, true)] [InlineData(144, true)] [InlineData(192, true)]
    public void Lifted_whole_ball_hover_hit_regions_and_stationary_recovery_use_transformed_geometry(int dpi, bool coexist) => WithStatusBall((window, ball) =>
    {
        using var fixture = DpiFixture();
        if (coexist)
        {
            window.ConfigureBongoSurface(true, 160);
            window.PresentBongoFrame(new CodexQuotaTaskbar.Host.Bongo.BongoFrame(1, 1, [255, 255, 255, 255], 4));
        }
        window.Apply(QuotaSnapshot.Available([new(QuotaWindowKind.Primary, 62, 300, DateTimeOffset.UtcNow.AddHours(5))], DateTimeOffset.UtcNow));
        var transform = ball.RenderTransform;
        ball.RenderTransform = Transform.Identity; var root = Layout(window, dpi); var before = window.StatusBallBoundsDip;
        var size = new Size(window.Width, window.DesiredHeight);
        RenderTargetBitmap Snapshot()
        {
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * dpi / 96), (int)Math.Ceiling(size.Height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(root); return bitmap;
        }
        var beforeImage = Snapshot();
        ball.RenderTransform = transform; Layout(window, dpi); var lifted = window.StatusBallBoundsDip;
        Assert.Equal(before.Width, lifted.Width, 6); Assert.Equal(before.Height, lifted.Height, 6);
        Assert.Equal(before.Left, lifted.Left, 6);
        Assert.Equal(2, before.Top - lifted.Top, 6); // Render translation is in parent DIPs, after the 52/56 layout scale.
        Assert.Equal(size.Height, window.DesiredHeight); // No owner/window resize for the lift.
        var preview = Environment.GetEnvironmentVariable("CQT_WHOLE_BALL_PREVIEW_DIR");
        if (dpi == 96 && !string.IsNullOrWhiteSpace(preview))
        {
            Assert.Equal(Path.GetFullPath(Path.Combine(RepositoryPaths.Root, "artifacts/interaction-wake-preview/whole-ball-hover-media")), Path.GetFullPath(preview), ignoreCase: true);
            Directory.CreateDirectory(preview);
            var afterImage = Snapshot(); var sheet = new DrawingVisual();
            using (var dc = sheet.RenderOpen())
            {
                dc.DrawRectangle(Brushes.DimGray, null, new Rect(0, 0, size.Width * 2, size.Height + 24));
                dc.DrawImage(beforeImage, new Rect(0, 24, size.Width, size.Height));
                dc.DrawImage(afterImage, new Rect(size.Width, 24, size.Width, size.Height));
                foreach (var pair in new[] { ("上移前", 14d), ("上移后 · 同一实际控件", size.Width + 14) })
                    dc.DrawText(new FormattedText(pair.Item1, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                        new Typeface(OverlayTypography.Text, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 13, Brushes.White, 1), new Point(pair.Item2, 2));
            }
            var bitmap = new RenderTargetBitmap((int)(size.Width * 4), (int)((size.Height + 24) * 2), 192, 192, PixelFormats.Pbgra32); bitmap.Render(sheet);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(preview, coexist ? "lift-52dip.png" : "lift-56dip.png"); Assert.False(File.Exists(path));
            using var file = File.Create(path); png.Save(file);
        }
        window.ApplyResetQueryState(new(1, ResetQueryPhase.Failed));
        foreach (var x in new[] { .1, .5, .9 })
        foreach (var y in new[] { .1, .5, .9 })
        {
            window.UpdateResetStatusPointer(new Point(-100, -100));
            Layout(window, dpi); lifted = window.StatusBallBoundsDip;
            var point = new Point(lifted.Left + lifted.Width * x, lifted.Top + lifted.Height * y);
            Assert.Equal(IslandHitRegion.Ball, window.PointerRegion(point));
            window.UpdateResetStatusPointer(point); Assert.Equal(ResetFooterKind.Status, window.ResetFooter);
        }
        Layout(window, dpi);
        var row = Named<Border>(window, "ResetScheduleRow");
        var footer = row.TransformToAncestor(Named<Grid>(window, "Shell")).TransformBounds(new Rect(row.RenderSize));
        var ballPoint = new Point(lifted.Left + lifted.Width / 2, lifted.Top + lifted.Height / 2);
        var footerPoint = new Point(footer.Left + footer.Width / 2, footer.Top + footer.Height / 2);
        for (var i = 1; i <= 30; i++)
        {
            window.UpdateResetStatusPointer(new Point(ballPoint.X + (footerPoint.X - ballPoint.X) * i / 30, ballPoint.Y + (footerPoint.Y - ballPoint.Y) * i / 30));
            Assert.Equal(ResetFooterKind.Status, window.ResetFooter);
        }
        Assert.False(row.IsHitTestVisible); Assert.NotEqual(IslandHitRegion.ResetNotice, window.PointerRegion(footerPoint));
        window.SetResetSchedulePresentationAllowed(false); Assert.Equal(ResetFooterKind.Status, window.ResetFooter);
        window.SetResetSchedulePresentationAllowed(true); Assert.Equal(ResetFooterKind.Status, window.ResetFooter);
        window.ShowDotTopExtension(new TextBlock { Text = "合成消息" }, 30); Assert.Equal(ResetFooterKind.Status, window.ResetFooter);
        window.HideDotTopExtension(); Assert.Equal(ResetFooterKind.Status, window.ResetFooter);
        window.UpdateResetStatusPointer(ballPoint);
        var moved = 0; window.UserMoveRequested += _ => moved++; // Synthetic move sink: no native window motion.
        Assert.True(window.BeginPointerGesture(System.Windows.Input.MouseButton.Left, IslandHitRegion.Ball, 411, 0, 0, () => true));
        Assert.Equal(ResetFooterKind.Status, window.ResetFooter);
        Assert.True(window.MovePointerGesture(true, 12, 12)); Assert.Equal(1, moved); Assert.True(window.IsUserDragging);
        window.CancelPointerGesture("synthetic-drag-end"); Assert.Equal(ResetFooterKind.Status, window.ResetFooter);
        window.ShowDotTopExtension(new TextBlock { Text = "合成消息" }, 30);
        window.UpdateResetStatusPointer(new Point(-100, -100)); window.HideDotTopExtension();
        Assert.Equal(ResetFooterKind.Status, window.ResetFooter);
        window.UpdateResetStatusPointer(ballPoint);
        Named<Grid>(window, "Shell").Children.Remove(ball); // Actual tree detachment, not only a routed Unloaded event.
        window.RefreshResetSchedule(DateTimeOffset.UtcNow);
        Assert.Equal(ResetFooterKind.Status, window.ResetFooter); Assert.True(window.StatusBallBoundsDip.IsEmpty);
    });

    [Fact]
    public void Optional_status_footer_preview_uses_actual_unshown_controls() => Sta(() =>
    {
        var wholeBall = Environment.GetEnvironmentVariable("CQT_WHOLE_BALL_PREVIEW_DIR");
        var configured = wholeBall ?? Environment.GetEnvironmentVariable("CQT_RESET_STATUS_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(configured)) return;
        var directory = Path.GetFullPath(configured);
        Assert.Equal(Path.GetFullPath(Path.Combine(RepositoryPaths.Root, "artifacts", "interaction-wake-preview", wholeBall is null ? "reset-status" : "whole-ball-hover-media")), directory, ignoreCase: true);
        Directory.CreateDirectory(directory);
        using var dpiFixture = DpiFixture();
        var sheet = new DrawingVisual();
        using (var draw = sheet.RenderOpen())
        {
            draw.DrawRectangle(new SolidColorBrush(Color.FromRgb(24, 26, 29)), null, new Rect(0, 0, 660, 400));
            var names = new[] { "普通 · 悬停也不出现说明", "金色 · 原重置提醒可点击", "绿色 · 悬停说明，不跳转", "红色 · 悬停说明，不跳转" };
            for (var index = 0; index < names.Length; index++)
            {
                var window = new QuotaCapsuleWindow("offline-status-preview");
                try
                {
                    var ball = ReadyStatusBall(window);
                    var now = DateTimeOffset.UtcNow;
                    window.Apply(QuotaSnapshot.Available([new(QuotaWindowKind.Primary, 62, 300, now.AddHours(5)),
                        new(QuotaWindowKind.Secondary, 48, 10080, now.AddDays(7))], now));
                    window.ApplySessions(Sessions(1));
                    if (index == 1) window.ApplyResetSchedule(Fresh(now, now.AddHours(1)), now);
                    if (index == 2) window.ApplyConfirmedReset(now.AddHours(1));
                    if (index == 3) window.ApplyResetQueryState(new(1, ResetQueryPhase.Failed));
                    Layout(window, 144);
                    var hover = window.StatusBallBoundsDip;
                    window.UpdateResetStatusPointer(new Point(hover.Left + hover.Width / 2, hover.Top + hover.Height / 2));
                    // Only visual sampling is overridden because an unshown owner correctly has no active ring.
                    ball.Visual!.SetState("35", false);
                    ball.SetResetState(new[] { ResetVisualState.None, ResetVisualState.Upcoming, ResetVisualState.Confirmed, ResetVisualState.Failed }[index]);
                    var root = Layout(window, 144);
                    var card = new RenderTargetBitmap(420, (int)Math.Ceiling(window.DesiredHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
                    card.Render(root);
                    var x = 20 + index % 2 * 320; var y = 16 + index / 2 * 190;
                    draw.DrawText(new FormattedText(names[index], CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                        new Typeface(OverlayTypography.Text, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal), 13, Brushes.White, 1), new Point(x + 8, y));
                    draw.DrawImage(card, new Rect(x, y + 30, 280, window.DesiredHeight));
                    Assert.False(window.IsVisible); Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Handle);
                }
                finally { window.Close(); }
            }
        }
        var bitmap = new RenderTargetBitmap(1320, 800, 192, 192, PixelFormats.Pbgra32); bitmap.Render(sheet);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var target = Path.Combine(directory, wholeBall is null ? "status-footer-preview.png" : "whole-ball-hover-preview.png");
        Assert.False(File.Exists(target)); using var file = File.Create(target); encoder.Save(file);
    });

    [Fact]
    public void Automatic_footer_preview_uses_only_unshown_controls() => Sta(() =>
    {
        var singleLine = Environment.GetEnvironmentVariable("CQT_RESET_SINGLE_LINE_PREVIEW_DIR");
        var reliability = Environment.GetEnvironmentVariable("CQT_RELIABILITY_POPUP_PREVIEW_DIR");
        var configured = reliability ?? singleLine ?? Environment.GetEnvironmentVariable("CQT_RESET_AUTO_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(configured)) return;
        var directory = Path.GetFullPath(configured);
        Assert.Equal(Path.GetFullPath(Path.Combine(RepositoryPaths.Root, reliability is not null ? "artifacts/reliability/popup" : singleLine is null ? "artifacts/reset-auto/previews" : "artifacts/reset-auto-single-line/previews")), directory, ignoreCase: true);
        Directory.CreateDirectory(directory);
        using var fixture = DpiFixture();
        foreach (var dpi in new[] { 96, 144, 192 })
        foreach (var compact in new[] { false, true })
        foreach (var state in new[] { 0, 1, 2, 3 })
        {
            var window = new QuotaCapsuleWindow("reset-auto-offscreen") { Width = 230 };
            try
            {
                var ball = ReadyStatusBall(window);
                var now = Now;
                window.Apply(QuotaSnapshot.Available([new(QuotaWindowKind.Primary, 100, 300, now.AddHours(5)),
                    new(QuotaWindowKind.Secondary, 100, 10080, now.AddDays(7))], now));
                window.ApplySessions(Sessions(100));
                window.SetDotMarker(100, canAcknowledge: true);
                if (state == 0) window.ApplyResetQueryState(new(1, ResetQueryPhase.Failed));
                if (state == 1) window.ApplyConfirmedReset(DateTimeOffset.UtcNow.AddHours(1));
                if (state >= 2) window.ApplyResetSchedule(Fresh(now, state == 3 ? now.AddMinutes(-1) : now.AddHours(1)) with { IsLatchedSchedule = true }, now, China);
                window.SetResetSchedulePresentationAllowed(!compact);
                ball.Visual!.SetState("35", false);
                ball.SetResetState(state == 0 ? ResetVisualState.Failed : state == 1 ? ResetVisualState.Confirmed : ResetVisualState.Upcoming);
                var root = Layout(window, dpi);
                Assert.False(window.IsVisible); Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Handle);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.Width * dpi / 96d),
                    (int)Math.Ceiling(window.DesiredHeight * dpi / 96d), dpi, dpi, PixelFormats.Pbgra32);
                bitmap.Render(root);
                if (reliability is not null)
                {
                    var sheet = new DrawingVisual();
                    using (var draw = sheet.RenderOpen())
                    {
                        draw.DrawRectangle(Brushes.Black, null, new Rect(0, 0, window.Width, window.DesiredHeight + 28));
                        draw.DrawText(new FormattedText($"SYNTHETIC · {dpi} DPI ({dpi / 96d:0.#}x) · 230 DIP",
                            CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 9, Brushes.White, dpi / 96d), new Point(4, 6));
                        draw.DrawImage(bitmap, new Rect(0, 28, window.Width, window.DesiredHeight));
                    }
                    bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.Width * dpi / 96d),
                        (int)Math.Ceiling((window.DesiredHeight + 28) * dpi / 96d), dpi, dpi, PixelFormats.Pbgra32);
                    bitmap.Render(sheet);
                }
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var path = Path.Combine(directory, $"{(compact ? "compact" : "full")}-{state}-{dpi}.png");
                Assert.False(File.Exists(path), "Preserve prior preview evidence.");
                using var output = File.Create(path); encoder.Save(output);
            }
            finally { window.Close(); }
        }
    });
    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)]
    [InlineData(2, true)]
    public void Owner_visibility_recovery_preserves_the_active_episode_float_phase(int state, bool space) => WithStatusBall((window, ball) =>
    {
        // Run the production visibility handler on an offline owner, with no Show/HWND/input.
        var ms = 0d;
        using var visual = new CompanionBallVisual(() => ms);
        var old = ball.Visual!; ball.Children.Remove(old); old.Dispose();
        typeof(CompanionBallControl).GetField("visual", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ball, visual);
        ball.Children.Insert(0, visual); visual.SetState("35", true);
        var frame = typeof(CompanionBallVisual).GetMethod("OnFrame", BindingFlags.Instance | BindingFlags.NonPublic)!;
        OwnerVisible(window, true);
        var now = DateTimeOffset.UtcNow;
        if (state == 2) window.ApplyResetSchedule(Fresh(now, now.AddHours(2)), now, China);
        else if (state == 1) window.ApplyConfirmedReset(now.AddHours(1));
        else window.ApplyResetQueryState(new(1, ResetQueryPhase.Failed));
        var expected = state == 2 ? ResetVisualState.Upcoming : state == 1 ? ResetVisualState.Confirmed : ResetVisualState.Failed;
        Assert.Equal(expected, ball.ResetState);
        ms = 800; frame.Invoke(visual, [null, EventArgs.Empty]);
        Assert.InRange(visual.FloatFrame.BodyY, -6.01, -5.99);
        if (space) window.SetResetScheduleAvailableHeight(0);
        else OwnerVisible(window, false);
        Assert.Equal(ResetVisualState.None, ball.ResetState);
        // Production's animation clock pauses while hidden; keep the injected clock fixed too.
        if (space) window.SetResetScheduleAvailableHeight(42);
        else OwnerVisible(window, true);
        ms = 1600; frame.Invoke(visual, [null, EventArgs.Empty]);
        Assert.Equal(-12, visual.FloatFrame.BodyY);
        Assert.Equal(expected, ball.ResetState);
        OwnerVisible(window, false);
    });

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void Owner_recovery_reprojects_expiry_before_restoring_color_and_footer(int state) => WithStatusBall((window, ball) =>
    {
        var now = DateTimeOffset.UtcNow;
        OwnerVisible(window, true, now);
        if (state == 0) window.ApplyConfirmedReset(now.AddHours(1));
        else window.ApplyResetSchedule(Fresh(now, now.AddMinutes(1)) with { IsLatchedSchedule = state == 2 }, now, China);
        OwnerVisible(window, false, now);
        OwnerVisible(window, true, now.AddHours(2));
        Assert.Equal(state == 2 ? ResetVisualState.Upcoming : ResetVisualState.None, ball.ResetState);
        Assert.Equal(state == 2 ? ResetFooterKind.Schedule : ResetFooterKind.None, window.ResetFooter);
        Assert.Equal(state == 2 ? "预计时间已到，等待额度恢复" : "", Named<TextBlock>(window, "ResetScheduleCountdown").Text);
        OwnerVisible(window, false, now.AddHours(2));
    });

    [Fact]
    public void Hidden_owner_accepts_state_changes_without_extending_confirmation_or_reviving_old_color() => WithStatusBall((window, ball) =>
    {
        var now = DateTimeOffset.UtcNow;
        OwnerVisible(window, true, now);
        window.ApplyResetQueryState(new(1, ResetQueryPhase.Failed));
        OwnerVisible(window, false, now);
        var until = now.AddHours(1);
        window.ApplyConfirmedReset(until);
        window.ApplyResetQueryState(new(2, ResetQueryPhase.Checking) { HasUnresolvedFailure = true });
        Assert.Equal(ResetVisualState.None, ball.ResetState);
        OwnerVisible(window, true, now.AddMinutes(59));
        Assert.Equal(ResetVisualState.Confirmed, ball.ResetState);
        Assert.Equal(until, Private<DateTimeOffset?>(window, "confirmedResetUntil"));
        OwnerVisible(window, false, now.AddMinutes(59));
        OwnerVisible(window, true, until);
        Assert.Equal(ResetVisualState.Failed, ball.ResetState);
        Assert.Equal(until, Private<DateTimeOffset?>(window, "confirmedResetUntil"));
        window.ApplyResetQueryState(new(3, ResetQueryPhase.Idle));
        window.RefreshResetSchedule(until);
        Assert.Equal(ResetVisualState.None, ball.ResetState);
        Assert.Equal(ResetFooterKind.None, window.ResetFooter);
        OwnerVisible(window, false, until);
    });

    [Fact]
    public void Actual_recovery_while_owner_is_hidden_consumes_locked_notice_before_any_visual_presentation() => WithStatusBall((window, ball) =>
    {
        var now = DateTimeOffset.UtcNow.AddSeconds(-2);
        var tracker = new QuotaResetTracker();
        var scope = QuotaResetTracker.ScopeForSource("SYNTHETIC-HIDDEN-RECOVERY")!;
        QuotaSnapshot Sample(double remaining, DateTimeOffset time) => QuotaSnapshot.Available(
            [new(QuotaWindowKind.Primary, remaining, 300, now.AddHours(5))], time);
        tracker.Observe(Sample(20, now), scope, now);
        var notice = Fresh(now, now.AddHours(3));
        tracker.SetSchedule(notice, now);
        window.SetResetTracker(tracker);
        window.ApplyResetSchedule(notice, now);
        Assert.False(window.IsVisible);
        var recovered = now.AddSeconds(1);
        var until = tracker.Observe(Sample(40, recovered), scope, recovered);
        Assert.Equal(recovered.AddHours(1), until);
        Assert.True(tracker.IsNoticeConsumed(notice.Schedule));
        window.ApplyConfirmedReset(until);
        Assert.Equal(ResetVisualState.None, ball.ResetState);
        OwnerVisible(window, true, recovered.AddMinutes(59));
        Assert.Equal(ResetVisualState.Confirmed, ball.ResetState);
        OwnerVisible(window, false, recovered.AddMinutes(59));
        OwnerVisible(window, true, until);
        Assert.Equal(ResetVisualState.None, ball.ResetState);
        Assert.Equal(ResetFooterKind.None, window.ResetFooter);
        Assert.Equal(until, Private<DateTimeOffset?>(window, "confirmedResetUntil"));
        Assert.True(tracker.IsNoticeConsumed(notice.Schedule));
        OwnerVisible(window, false, until);
    });

    private static void OwnerVisible(QuotaCapsuleWindow window, bool value, DateTimeOffset? now = null)
    {
        // WPF normally changes this cache and raises IsVisibleChanged from its
        // presentation source. Keep the fixture sourceless, then run the same handler.
        var writeFlag = typeof(UIElement).GetMethod("WriteFlag", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var flag = Enum.Parse(writeFlag.GetParameters()[0].ParameterType, "IsVisibleCache");
        writeFlag.Invoke(window, [flag, value]);
        var visibleKey = (DependencyPropertyKey)typeof(UIElement).GetField("IsVisiblePropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        window.SetValue(visibleKey, value);
        window.HandleResetOwnerVisibilityChanged(now);
    }

    private static CompanionBallControl ReadyStatusBall(QuotaCapsuleWindow window)
    {
        window.SetPointerObservationAllowed(false); window.SetCompanion(true, true);
        var ball = Private<CompanionBallControl>(window, "companion");
        ball.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        return ball;
    }
    private static T Private<T>(object owner, string field) => (T)owner.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static void WithStatusBall(Action<QuotaCapsuleWindow, CompanionBallControl> check) => Sta(() =>
    {
        var window = new QuotaCapsuleWindow("synthetic-status-footer");
        try { check(window, ReadyStatusBall(window)); Assert.False(window.IsVisible); Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Handle); }
        finally { window.Close(); }
    });

    private static ResetScheduleSnapshot Fresh(DateTimeOffset? updated = null, DateTimeOffset? deadline = null)
    {
        var now = updated ?? Now;
        return new(new("row-fixture", ResetKind.Regular, now.AddMinutes(-10), deadline ?? now.AddHours(1), Announcement), now, false, null);
    }

    private static CodexSessionsSnapshot Sessions(int count) => new(Enumerable.Range(0, count).Select(index =>
        new CodexSessionSnapshot($"preview-{index}", new[] { "整理示例项目", "检查界面布局", "准备演示文档" }[index % 3],
            index == 2 ? CodexSessionState.Review : CodexSessionState.Running, Now)).ToArray(), Now);

    private static T Named<T>(Window window, string name) where T : FrameworkElement => Assert.IsType<T>(window.FindName(name));
    private static HwndSource DpiFixture() => new(new HwndSourceParameters("ResetRowDpiFixture") { Width = 0, Height = 0, WindowStyle = 0 });
    private static FrameworkElement Layout(QuotaCapsuleWindow window, int dpi)
    {
        var content = (FrameworkElement)window.Content;
        VisualTreeHelper.SetRootDpi(content, new DpiScale(dpi / 96d, dpi / 96d));
        content.Measure(new Size(window.Width, window.DesiredHeight));
        content.Arrange(new Rect(new Point(), new Size(window.Width, window.DesiredHeight)));
        content.UpdateLayout();
        return content;
    }
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { failure = exception; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Offline row verification timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
