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

    [Fact]
    public void Visible_text_contains_only_approximate_remaining_and_local_time()
    {
        var nextDay = ResetScheduleRowPresentation.Create(Fresh(), Now.AddMinutes(-1), China);
        Assert.True(nextDay.IsVisible);
        Assert.Equal("约 1 小时后重置", nextDay.Countdown);
        Assert.Equal(" · 明日 01:00", nextDay.LocalTime);
        Assert.Contains("第三方", nextDay.Tooltip, StringComparison.Ordinal);
        Assert.Contains("并非本账号", nextDay.Tooltip, StringComparison.Ordinal);
        Assert.Contains(Announcement.ToString(), nextDay.Tooltip, StringComparison.Ordinal);
        var today = ResetScheduleRowPresentation.Create(Fresh(), Now, TimeZoneInfo.Utc);
        Assert.Equal(" · 本地 17:00", today.LocalTime);
    }

    [Theory]
    [InlineData("2026-07-23T17:00:00Z", "13:00")]
    [InlineData("2026-01-23T17:00:00Z", "12:00")]
    public void Local_time_observes_dst_at_the_scheduled_instant(string target, string localTime)
    {
        var deadline = DateTimeOffset.Parse(target);
        var clock = deadline.AddHours(-1);
        var view = ResetScheduleRowPresentation.Create(Fresh(clock, deadline), clock, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
        Assert.Equal(" · 本地 " + localTime, view.LocalTime);
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
                Assert.Equal(132 + 26, window.DesiredHeight);
                Assert.Equal(200 + 26, window.ReservedHeight);
                window.ApplySessions(CodexSessionsSnapshot.Empty);
                Assert.Equal(80 + 26, window.DesiredHeight);
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
            Assert.Equal(TextTrimming.CharacterEllipsis, countdown.TextTrimming);
            var formatted = new FormattedText(time.Text, CultureInfo.GetCultureInfo("zh-CN"), time.FlowDirection,
                new Typeface(time.FontFamily, time.FontStyle, time.FontWeight, time.FontStretch), time.FontSize, time.Foreground, dpi / 96d);
            Assert.True(time.ActualWidth + 0.5 >= formatted.WidthIncludingTrailingWhitespace);
            Assert.InRange(rowBounds.Right, 0, width);
            Assert.Equal(26, row.ActualHeight);
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
        Assert.Equal(ResetFooterKind.None, window.ResetFooter);
        for (var repeat = 0; repeat < 8; repeat++)
        {
            ball.SetResetHintHovered(true);
            Assert.Equal(ResetFooterKind.Status, window.ResetFooter);
            Assert.Equal(height + 26, window.DesiredHeight);
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
        Assert.Equal(ResetFooterKind.None, window.ResetFooter);
        window.ApplyResetQueryState(new(4, ResetQueryPhase.Idle));
        var later = until!.Value.AddSeconds(1);
        window.ApplyResetSchedule(snapshot with { LastUpdatedAt = later }, later);
        window.SetResetStatusHintHovered(true, later);
        Assert.Equal(ResetFooterKind.None, window.ResetFooter);
        Assert.Equal(until, Private<DateTimeOffset?>(window, "confirmedResetUntil"));
        window.InvokeResetFooter(); Assert.Single(opened);
    });

    [Fact]
    public void Dot_presentation_and_pointer_gestures_dismiss_only_the_optional_status() => WithStatusBall((window, ball) =>
    {
        window.ApplyResetQueryState(new(1, ResetQueryPhase.Failed));
        ball.SetResetHintHovered(true);
        Assert.Equal(ResetFooterKind.Status, window.ResetFooter);
        window.SetDotMarker(2, canAcknowledge: true);
        var marker = window.DotMarkerButton;
        var dot = new TextBlock { Text = "合成 Dot 消息" };
        window.ShowDotTopExtension(dot, 30);
        Assert.Equal(ResetFooterKind.None, window.ResetFooter);
        window.SetResetStatusHintHovered(true);
        Assert.Equal(ResetFooterKind.None, window.ResetFooter);
        Assert.Same(dot, window.DotTopContent); Assert.Same(marker, window.DotMarkerButton);
        window.HideDotTopExtension();
        Assert.Equal(ResetFooterKind.Status, window.ResetFooter);
        var ballClicks = 0; window.CompanionInvoked += (_, _) => ballClicks++;
        Assert.True(window.BeginPointerGesture(System.Windows.Input.MouseButton.Left, IslandHitRegion.Ball, 100, 0, 0, () => true));
        Assert.Equal(ResetFooterKind.None, window.ResetFooter);
        window.EndPointerGesture(System.Windows.Input.MouseButton.Left, IslandHitRegion.Ball, 110);
        Assert.Equal(1, ballClicks); Assert.False(window.HasPointerGesture);
        Assert.Equal(ResetFooterKind.Status, window.ResetFooter); // No leave/re-enter needed.
        ball.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Assert.Equal(ResetFooterKind.None, window.ResetFooter);
        Assert.False(Private<System.Windows.Threading.DispatcherTimer>(ball, "timer").IsEnabled);
        Assert.False(Private<bool>(window, "extensionRenderAttached"));
    });

    [Theory]
    [InlineData(96, true)] [InlineData(108, true)] [InlineData(144, true)]
    [InlineData(96, false)] [InlineData(108, false)] [InlineData(144, false)]
    public void Special_footer_text_fits_and_space_recovery_restores_retained_hover(int dpi, bool green) => WithStatusBall((window, ball) =>
    {
        using var fixture = DpiFixture();
        if (green) window.ApplyConfirmedReset(DateTimeOffset.UtcNow.AddHours(1));
        else window.ApplyResetQueryState(new(1, ResetQueryPhase.Failed));
        var baseline = window.DesiredHeight;
        ball.SetResetHintHovered(true);
        Layout(window, dpi);
        var title = Named<TextBlock>(window, "ResetScheduleCountdown");
        var detail = Named<TextBlock>(window, "ResetScheduleLocalTime");
        foreach (var text in new[] { title, detail })
        {
            var measured = new FormattedText(text.Text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize,
                Brushes.White, dpi / 96d);
            Assert.True(measured.WidthIncludingTrailingWhitespace <= text.ActualWidth + 1, text.Text);
            Assert.True(measured.Height <= text.ActualHeight + 1, text.Text);
        }
        var parent = (FrameworkElement)title.Parent;
        Assert.True(title.TransformToAncestor(parent).TransformBounds(new Rect(title.RenderSize)).Right
            <= detail.TransformToAncestor(parent).TransformBounds(new Rect(detail.RenderSize)).Left + 1);
        window.SetResetSchedulePresentationAllowed(false);
        Assert.Equal(baseline, window.DesiredHeight);
        window.SetResetSchedulePresentationAllowed(true);
        Assert.Equal(ResetFooterKind.Status, window.ResetFooter);
        Assert.Equal(baseline + 26, window.DesiredHeight);
        window.SetResetSchedulePresentationAllowed(false);
        ball.SetResetHintHovered(false);
        window.SetResetSchedulePresentationAllowed(true);
        Assert.Equal(ResetFooterKind.None, window.ResetFooter); // Leaving during suppression is not retained.
        window.SetCompanion(false, true);
        Assert.Equal(ResetFooterKind.None, window.ResetFooter);
        Assert.Equal(Visibility.Collapsed, Named<Border>(window, "ResetScheduleRow").Visibility);
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
        window.SetResetSchedulePresentationAllowed(false); Assert.Equal(ResetFooterKind.None, window.ResetFooter);
        window.SetResetSchedulePresentationAllowed(true); Assert.Equal(ResetFooterKind.Status, window.ResetFooter);
        window.ShowDotTopExtension(new TextBlock { Text = "合成消息" }, 30); Assert.Equal(ResetFooterKind.None, window.ResetFooter);
        window.HideDotTopExtension(); Assert.Equal(ResetFooterKind.Status, window.ResetFooter);
        window.UpdateResetStatusPointer(ballPoint);
        var moved = 0; window.UserMoveRequested += _ => moved++; // Synthetic move sink: no native window motion.
        Assert.True(window.BeginPointerGesture(System.Windows.Input.MouseButton.Left, IslandHitRegion.Ball, 411, 0, 0, () => true));
        Assert.Equal(ResetFooterKind.None, window.ResetFooter);
        Assert.True(window.MovePointerGesture(true, 12, 12)); Assert.Equal(1, moved); Assert.True(window.IsUserDragging);
        window.CancelPointerGesture("synthetic-drag-end"); Assert.Equal(ResetFooterKind.Status, window.ResetFooter);
        window.ShowDotTopExtension(new TextBlock { Text = "合成消息" }, 30);
        window.UpdateResetStatusPointer(new Point(-100, -100)); window.HideDotTopExtension();
        Assert.Equal(ResetFooterKind.None, window.ResetFooter);
        window.UpdateResetStatusPointer(ballPoint);
        Named<Grid>(window, "Shell").Children.Remove(ball); // Actual tree detachment, not only a routed Unloaded event.
        window.RefreshResetSchedule(DateTimeOffset.UtcNow);
        Assert.Equal(ResetFooterKind.None, window.ResetFooter); Assert.True(window.StatusBallBoundsDip.IsEmpty);
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
