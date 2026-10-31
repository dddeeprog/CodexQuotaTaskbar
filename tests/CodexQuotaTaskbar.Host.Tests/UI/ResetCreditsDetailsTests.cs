using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexQuotaTaskbar.Core.Provider;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class ResetCreditsDetailsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-01T00:00:00Z", CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("null", "未知")]
    [InlineData("{\"availableCount\":0,\"credits\":[]}", "0 张可用")]
    [InlineData("{\"availableCount\":3,\"credits\":null}", "3 张可用")]
    public void Details_distinguish_missing_zero_and_count_only(string credits, string expected)
    {
        Sta(() =>
        {
            var window = new QuotaPopoverWindow();
            try
            {
                window.Apply(AppServerRateLimitsParser.Parse(QuotaJson(credits), Now));
                var count = Assert.IsType<TextBlock>(window.FindName("ResetCreditsCountText"));
                Assert.Equal(expected, count.Text);
                Assert.Equal(nint.Zero, new System.Windows.Interop.WindowInteropHelper(window).Handle);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Each_card_has_its_own_action_and_local_expiry_even_when_dates_match()
    {
        Sta(() =>
        {
            var details = new QuotaPopoverWindow();
            try
            {
                details.Apply(AppServerRateLimitsParser.Parse(QuotaJson(Summary(5,
                    Card("a", "2026-12-31T15:59:00Z"), Card("b", "2026-12-31T15:59:00Z"),
                    Card("c", null), Card("d", "2026-11-22T15:59:00Z"),
                    Card("used", "2026-12-31T15:59:00Z", "redeemed"),
                    Card("expired", "2026-10-31T23:59:00Z"), Card("unknown", null, "unknown"))), Now));
                details.RenderResetCredits(Now, TimeZoneInfo.CreateCustomTimeZone("fixture+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8"));
                Assert.Equal("5 张（读取时）", Named<TextBlock>(details, "ResetCreditsCountText").Text);
                Assert.Equal(new[] { "11月22日周日 23:59 到期", "12月31日周四 23:59 到期", "12月31日周四 23:59 到期", "未设置到期时间", "1 张 · 有效期未提供" },
                    CardTexts(details).Select(text => text.Text));
                Assert.Equal(5, CardButtons(details).Count());
                Assert.Contains("数量需刷新确认", Named<TextBlock>(details, "ResetCreditsStatusText").Text);
                Assert.Contains("状态不明", Named<TextBlock>(details, "ResetCreditsStatusText").Text);
            }
            finally { details.Close(); }
        });
    }

    [Fact]
    public void Cached_count_is_labeled_and_replacement_account_drops_previous_cards()
    {
        Sta(() =>
        {
            var details = new QuotaPopoverWindow();
            try
            {
                var snapshot = AppServerRateLimitsParser.Parse(QuotaJson(Summary(3, Card("a", null))), Now);
                details.Apply(snapshot with { ResetCredits = snapshot.ResetCredits with { IsStale = true } });
                Assert.Equal("3 张（缓存）", Named<TextBlock>(details, "ResetCreditsCountText").Text);
                Assert.Contains("显示上次读取", Named<TextBlock>(details, "ResetCreditsStatusText").Text);
                details.Apply(QuotaSnapshot.Unavailable("账号已变化，正在刷新…"));
                Assert.Equal("未知", Named<TextBlock>(details, "ResetCreditsCountText").Text);
                Assert.Empty(Named<StackPanel>(details, "ResetCreditsRows").Children);
            }
            finally { details.Close(); }
        });
    }

    [Theory]
    [InlineData(true, "上次读取时没有可用重置卡")]
    [InlineData(false, "当前没有可用重置卡")]
    public void Zero_uses_the_read_time_when_cached(bool stale, string expected)
    {
        Sta(() =>
        {
            var details = new QuotaPopoverWindow();
            try
            {
                var snapshot = AppServerRateLimitsParser.Parse(QuotaJson(Summary(0)), Now);
                details.Apply(snapshot with { ResetCredits = snapshot.ResetCredits with { IsStale = stale } });
                Assert.Contains(expected, Named<TextBlock>(details, "ResetCreditsStatusText").Text);
                if (!stale)
                {
                    Assert.Empty(Named<StackPanel>(details, "ResetCreditsRows").Children);
                    Assert.Equal(Visibility.Collapsed, Named<TextBlock>(details, "ResetCreditsCountText").Visibility);
                    Assert.Equal(Visibility.Collapsed, Named<StackPanel>(details, "ResetCreditUseActions").Visibility);
                }
                Assert.Null(details.FindName("ResetCreditsSourceText"));
                Assert.Null(details.FindName("CheckResetCreditUseButton"));
            }
            finally { details.Close(); }
        });
    }

    [Fact]
    public void Opening_after_a_returned_expiry_keeps_the_server_count_as_historical()
    {
        Sta(() =>
        {
            var details = new QuotaPopoverWindow();
            try
            {
                details.Apply(AppServerRateLimitsParser.Parse(QuotaJson(Summary(2,
                    Card("expired", "2026-11-01T00:01:00Z"), Card("future", "2026-12-31T15:59:00Z"))), Now));
                details.RenderResetCredits(Now.AddMinutes(2), TimeZoneInfo.Utc);
                Assert.Equal("2 张（读取时）", Named<TextBlock>(details, "ResetCreditsCountText").Text);
                Assert.Contains("数量需刷新确认", Named<TextBlock>(details, "ResetCreditsStatusText").Text);
                Assert.Equal(2, Named<StackPanel>(details, "ResetCreditsRows").Children.Count);
            }
            finally { details.Close(); }
        });
    }

    [Theory]
    [InlineData("2027-01-01T00:15:00Z", "12月31日周四 19:15 到期", "-05:00")]
    [InlineData("2027-07-01T00:15:00Z", "6月30日周三 20:15 到期", "-04:00")]
    [InlineData("2026-11-01T05:30:00Z", "11月1日周日 01:30 到期", "-04:00")]
    [InlineData("2026-11-01T06:30:00Z", "11月1日周日 01:30 到期", "-05:00")]
    public void Expiry_uses_the_selected_zone_across_year_and_dst(string expiry, string expected, string offset)
    {
        Sta(() =>
        {
            var details = new QuotaPopoverWindow();
            try
            {
                details.Apply(AppServerRateLimitsParser.Parse(QuotaJson(Summary(1, Card("fixture", expiry))), Now));
                details.RenderResetCredits(Now, TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
                var row = Assert.Single(CardTexts(details));
                Assert.Equal(expected, row.Text);
                Assert.Contains(offset, Assert.IsType<string>(row.ToolTip));
            }
            finally { details.Close(); }
        });
    }

    [Fact]
    public void Missing_expiry_is_not_the_same_as_an_explicitly_unset_expiry()
    {
        Sta(() =>
        {
            var details = new QuotaPopoverWindow();
            try
            {
                details.Apply(AppServerRateLimitsParser.Parse(QuotaJson(Summary(2, Card("explicit-null", null),
                    new { id = "missing", resetType = "codexRateLimits", status = "available", grantedAt = 1793400000 })), Now));
                details.RenderResetCredits(Now);
                Assert.Equal(new[] { "未设置到期时间", "有效期未提供" }, CardTexts(details).Select(t => t.Text));
            }
            finally { details.Close(); }
        });
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void Offscreen_details_scroll_card_groups_without_covering_footer(int dpi)
    {
        Sta(() =>
        {
            foreach (var state in new[] { "success", "zero", "unknown", "unavailable", "count-only", "stale", "long" })
            {
                var details = new QuotaPopoverWindow();
                try
                {
                    var summary = state switch
                    {
                        "unknown" => "null",
                        "zero" => Summary(0),
                        "count-only" => "{\"availableCount\":3,\"credits\":null}",
                        "long" => Summary(30, Enumerable.Range(0, 30).Select(i => Card("test-" + i,
                            Now.AddDays(30 + i).ToString("O", CultureInfo.InvariantCulture))).ToArray()),
                        _ => Summary(3, Card("a", "2026-12-31T15:59:00Z"), Card("b", "2026-12-31T15:59:00Z"), Card("c", null)),
                    };
                    var snapshot = AppServerRateLimitsParser.Parse(QuotaJson(summary), Now, "Plus");
                    if (state == "stale") snapshot = snapshot with { ResetCredits = snapshot.ResetCredits with { IsStale = true } };
                    if (state == "unavailable") snapshot = QuotaSnapshot.Unavailable("暂时无法读取 Codex 额度");
                    details.Apply(snapshot);
                    details.RenderResetCredits(Now, TimeZoneInfo.CreateCustomTimeZone("fixture+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8"));
                    // Apply() normally themes after rendering. Our deterministic time
                    // re-render must complete that same production appearance path.
                    details.SetMaterialTransparency(0);
                    if (state == "unavailable")
                    {
                        Assert.Equal("未知", Named<TextBlock>(details, "ResetCreditsCountText").Text);
                        Assert.Equal("暂时无法读取重置卡信息", Named<TextBlock>(details, "ResetCreditsStatusText").Text);
                    }
                    var root = (FrameworkElement)details.Content;
                    using var source = new HwndSource(new HwndSourceParameters("ResetCreditsOffscreenDpi") { Width = 0, Height = 0, WindowStyle = 0 });
                    VisualTreeHelper.SetRootDpi(root, new DpiScale(dpi / 96d, dpi / 96d));
                    root.Measure(new Size(360, 552));
                    root.Arrange(new Rect(0, 0, 360, Math.Min(552, root.DesiredSize.Height)));
                    root.UpdateLayout();
                    var scroll = Named<ScrollViewer>(details, "DetailsScroller");
                    var refresh = Named<Button>(details, "RefreshButton");
                    var refreshBounds = refresh.TransformToAncestor(root).TransformBounds(new Rect(refresh.RenderSize));
                    var scrollBounds = scroll.TransformToAncestor(root).TransformBounds(new Rect(scroll.RenderSize));
                    Assert.True(scrollBounds.Bottom <= refreshBounds.Top);
                    Assert.True(refreshBounds.Bottom <= root.ActualHeight + 0.01);
                    Assert.Equal(nint.Zero, new WindowInteropHelper(details).Handle);
                    Assert.False(details.IsVisible);
                    Export(root, dpi, state, "top");
                    scroll.ScrollToEnd(); root.UpdateLayout();
                    if (state == "long") Assert.True(scroll.ScrollableHeight > 0);
                    foreach (var text in CardTexts(details))
                    {
                        Assert.True(text.DesiredSize.Width <= scroll.ViewportWidth + 0.1);
                        Assert.True(text.ActualHeight >= text.FontSize);
                        var row = Assert.IsType<Grid>(text.Parent);
                        var button = Assert.Single(row.Children.OfType<Button>());
                        Assert.Equal("使用重置卡", button.Content);
                        Assert.True(text.TranslatePoint(new Point(text.ActualWidth, 0), row).X <= button.TranslatePoint(new Point(), row).X + 0.1);
                        Assert.True(button.ActualWidth >= 70);
                    }
                    Export(root, dpi, state, "bottom");
                    var output = PreviewDirectory();
                    if (output is not null)
                        File.WriteAllText(Path.Combine(output, $"details-{dpi}-{state}.json"), JsonSerializer.Serialize(new
                        {
                            dpi, state, synthetic = true, visible = details.IsVisible, hwnd = 0,
                            count = Named<TextBlock>(details, "ResetCreditsCountText").Text,
                            groups = CardTexts(details).Select(t => t.Text).ToArray(),
                            scrollHeight = scroll.ViewportHeight, scrollExtent = scroll.ExtentHeight,
                            footerTop = refreshBounds.Top, scrollBottom = scrollBounds.Bottom, footerBottom = refreshBounds.Bottom,
                            height = root.ActualHeight, width = root.ActualWidth,
                            canvasHeight = root.ActualHeight + root.Margin.Top + root.Margin.Bottom, canvasWidth = 360,
                        }, new JsonSerializerOptions { WriteIndented = true }));
                }
                finally { details.Close(); }
            }
        });
    }

    private static T Named<T>(Window window, string name) where T : class => Assert.IsType<T>(window.FindName(name));
    private static IEnumerable<TextBlock> CardTexts(Window window) => Named<StackPanel>(window, "ResetCreditsRows").Children
        .OfType<Grid>().SelectMany(row => row.Children.OfType<TextBlock>());
    private static IEnumerable<Button> CardButtons(Window window) => Named<StackPanel>(window, "ResetCreditsRows").Children
        .OfType<Grid>().SelectMany(row => row.Children.OfType<Button>());

    private static string Summary(int count, params object[] rows) => JsonSerializer.Serialize(new { availableCount = count, credits = rows });

    private static object Card(string id, string? expires, string status = "available") => new
    {
        id, resetType = "codexRateLimits", status, grantedAt = Now.AddDays(-1).ToUnixTimeSeconds(),
        expiresAt = expires is null ? (long?)null : DateTimeOffset.Parse(expires, CultureInfo.InvariantCulture).ToUnixTimeSeconds(),
        title = (string?)null, description = (string?)null,
    };

    private static string? PreviewDirectory()
    {
        var output = Environment.GetEnvironmentVariable("DIANDAO_RESET_CREDITS_PREVIEW");
        if (string.IsNullOrEmpty(output)) return null;
        var full = Path.GetFullPath(output);
        var allowed = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/reliability/reset-details/previews/final"));
        var layout = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/reliability/reset-card-layout/previews"));
        if (!new[] { allowed, layout }.Any(path => string.Equals(full.TrimEnd(Path.DirectorySeparatorChar),
            path.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Preview output must remain in this task's reserved directory.");
        Directory.CreateDirectory(full);
        return full;
    }

    private static void Export(FrameworkElement root, int dpi, string state, string position)
    {
        var output = PreviewDirectory();
        if (output is null) return;
        var path = Path.Combine(output, $"details-{dpi}-{state}-{position}.png");
        if (File.Exists(path)) throw new IOException("Existing preview evidence must be retained.");
        // Render(root) includes its arranged Margin offset. Preserve the full
        // 360-DIP outer canvas, including both margins and the shadow.
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(360 * dpi / 96d),
            (int)Math.Ceiling((root.ActualHeight + root.Margin.Top + root.Margin.Bottom) * dpi / 96d), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static string QuotaJson(string credits) => "{\"rateLimits\":{\"primary\":{\"usedPercent\":25,\"windowDurationMins\":300,\"resetsAt\":1793606400},\"secondary\":{\"usedPercent\":50,\"windowDurationMins\":10080}},\"rateLimitResetCredits\":" + credits + "}";

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
