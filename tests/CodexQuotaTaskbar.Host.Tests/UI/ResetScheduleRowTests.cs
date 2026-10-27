using System.ComponentModel;
using System.Globalization;
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
