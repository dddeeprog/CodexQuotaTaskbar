using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Host.Resets;
using CodexQuotaTaskbar.Host.Tests.TestSupport;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class ResetFeedWindowTests
{
    [Theory]
    [InlineData("regular", 108, "常规")]
    [InlineData("banked", 144, "备用")]
    [InlineData("loading", 108, "正在读取")]
    [InlineData("stale", 144, "缓存")]
    [InlineData("unavailable", 108, "暂不可用")]
    [InlineData("empty", 144, "暂无公告")]
    public void Island_keeps_both_quota_rows_and_fits_reset_summary(string state, int dpi, string expected)
    {
        RunInSta(() =>
        {
            var window = new QuotaCapsuleWindow("reset-feed-test");
            try
            {
                window.Apply(CreateQuota());
                window.ApplyResets(CreateFeed(state));
                var content = Layout(window, dpi, window.Height);

                Assert.Equal(112, window.Height);
                var glass = Named<Border>(window, "GlassBorder");
                Assert.Equal(68, glass.ActualHeight, 2);
                Assert.Equal("5 小时", Named<TextBlock>(window, "FirstLabel").Text);
                Assert.Equal("7 天", Named<TextBlock>(window, "SecondLabel").Text);
                Assert.Equal("72%", Named<TextBlock>(window, "FirstValue").Text);
                Assert.Equal("41%", Named<TextBlock>(window, "SecondValue").Text);
                Assert.Equal(Visibility.Visible, Named<Grid>(window, "SecondRow").Visibility);

                var source = Named<TextBlock>(window, "ResetSourceText");
                AssertPackagedFont(source);
                Assert.Equal(8.5, source.FontSize);
                var sourceLink = Assert.IsType<Hyperlink>(window.FindName("ResetSourceLink"));
                Assert.Equal("Codex Resets", new TextRange(sourceLink.ContentStart, sourceLink.ContentEnd).Text);
                Uri? openedSource = null;
                var detailsRequested = false;
                window.OpenResetSourceRequested += uri => openedSource = uri;
                window.PrimaryInvoked += (_, _) => detailsRequested = true;
                sourceLink.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
                Assert.Equal(CodexResetFeedClient.SiteUri, openedSource);
                Assert.False(detailsRequested);
                Assert.False(window.IsUserDragging);
                SaveProof(content, $"island-{state}-{dpi}", dpi);

                var summary = Named<TextBlock>(window, "ResetSummaryText");
                Assert.Contains(expected, summary.Text, StringComparison.Ordinal);
                Assert.Equal(9.5, summary.FontSize);
                AssertPackagedFont(summary);
                var fullText = new FormattedText(summary.Text, CultureInfo.CurrentUICulture,
                    summary.FlowDirection, new Typeface(summary.FontFamily, summary.FontStyle,
                        summary.FontWeight, summary.FontStretch), summary.FontSize, summary.Foreground, dpi / 96d);
                Assert.True(fullText.WidthIncludingTrailingWhitespace <= summary.ActualWidth + 0.5,
                    $"Summary was trimmed: {summary.Text}; required {fullText.WidthIncludingTrailingWhitespace:0.##} DIP, available {summary.ActualWidth:0.##} DIP.");
                Assert.True(fullText.Height <= summary.ActualHeight + 0.5);
                var bounds = summary.TransformToAncestor(glass).TransformBounds(new Rect(summary.RenderSize));
                Assert.True(bounds.Top >= 0 && bounds.Bottom <= glass.ActualHeight);
                Assert.True(bounds.Left >= 0 && bounds.Right <= glass.ActualWidth);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(108)]
    [InlineData(144)]
    public void Details_show_three_announcements_without_post_links_and_scroll_to_source_credit(int dpi)
    {
        RunInSta(() =>
        {
            var window = new QuotaPopoverWindow();
            try
            {
                var feed = CreateFeed("banked");
                window.Apply(CreateQuota());
                window.ApplyResets(feed);
                window.Opacity = 1;
                var content = Layout(window, dpi);
                var announcements = Named<StackPanel>(window, "ResetAnnouncementsPanel");
                Assert.Equal(3, announcements.Children.Count);
                for (var index = 0; index < announcements.Children.Count; index++)
                {
                    var entry = Assert.IsType<StackPanel>(announcements.Children[index]);
                    var body = Assert.IsType<TextBlock>(entry.Children[1]);
                    Assert.Equal(ResetFeedPresentation.AnnouncementText(feed.Announcements[index]), body.Text);
                    Assert.DoesNotContain("以下为原文", body.Text);
                    Assert.Equal(TextWrapping.Wrap, body.TextWrapping);
                    Assert.Equal(TextTrimming.None, body.TextTrimming);
                    var measured = new FormattedText(body.Text, CultureInfo.CurrentUICulture,
                        body.FlowDirection, new Typeface(body.FontFamily, body.FontStyle,
                            body.FontWeight, body.FontStretch), body.FontSize, body.Foreground, dpi / 96d)
                    {
                        MaxTextWidth = body.ActualWidth,
                    };
                    Assert.True(measured.Height <= body.ActualHeight + 0.5,
                        $"Chinese announcement {index} was vertically clipped at {dpi} DPI.");
                    Assert.True(measured.WidthIncludingTrailingWhitespace <= body.ActualWidth + 0.5,
                        $"Chinese announcement {index} overflowed horizontally at {dpi} DPI.");
                    var bodyBounds = body.TransformToAncestor(announcements).TransformBounds(new Rect(body.RenderSize));
                    Assert.True(bodyBounds.Left >= 0 && bodyBounds.Right <= announcements.ActualWidth + 0.5);
                }
                var rows = Named<StackPanel>(window, "RowsPanel");
                Assert.Equal(2, rows.Children.Count);
                var section = Named<StackPanel>(window, "ResetSection");
                AssertSuccessfulSectionStartsWithAnnouncements(window);
                foreach (var text in Descendants<TextBlock>(section))
                {
                    AssertPackagedFont(text);
                }

                var navigated = new List<Uri>();
                window.OpenResetSourceRequested += navigated.Add;
                Assert.Empty(Descendants<Button>(announcements));
                Assert.DoesNotContain(Descendants<TextBlock>(announcements), text =>
                    text.Text.Contains("https://", StringComparison.Ordinal));
                var site = Named<Button>(window, "ResetSiteButton");
                Assert.Equal("来源：Codex Resets ↗", site.Content);
                site.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(CodexResetFeedClient.SiteUri, Assert.Single(navigated));

                var scroll = Assert.Single(Descendants<ScrollViewer>(content));
                Assert.Equal(ScrollBarVisibility.Hidden, scroll.VerticalScrollBarVisibility);
                Assert.Equal(Visibility.Collapsed, scroll.ComputedVerticalScrollBarVisibility);
                Assert.True(scroll.ActualHeight <= 340);
                Assert.True(scroll.ScrollableHeight > 0);
                Assert.True(scroll.ExtentHeight > scroll.ViewportHeight);
                Assert.True(content.ActualHeight <= window.MaxHeight);
                var footer = Named<Button>(window, "RefreshButton");
                Assert.True(footer.TransformToAncestor(content).TransformBounds(new Rect(footer.RenderSize)).Bottom <= content.ActualHeight);
                SaveProof(content, $"details-top-{dpi}", dpi);

                var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                {
                    RoutedEvent = Mouse.MouseWheelEvent,
                };
                rows.RaiseEvent(wheel);
                content.UpdateLayout();
                Assert.True(wheel.Handled);
                Assert.True(scroll.VerticalOffset > 0);

                scroll.ScrollToEnd();
                content.UpdateLayout();
                Assert.InRange(scroll.VerticalOffset, scroll.ScrollableHeight - 0.5, scroll.ScrollableHeight + 0.5);
                var sourceBounds = site.TransformToAncestor(scroll).TransformBounds(new Rect(site.RenderSize));
                Assert.True(sourceBounds.Top >= 0 && sourceBounds.Bottom <= scroll.ActualHeight + 0.5);
                SaveProof(content, $"details-bottom-{dpi}", dpi);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData("loading", 108, "正在获取")]
    [InlineData("loading", 144, "正在获取")]
    [InlineData("empty", 108, "暂无重置公告")]
    [InlineData("empty", 144, "暂无重置公告")]
    [InlineData("unavailable", 108, "重试")]
    [InlineData("unavailable", 144, "重试")]
    [InlineData("stale", 108, "上次获取")]
    [InlineData("stale", 144, "上次获取")]
    public void Details_status_only_occupies_space_when_needed_and_collapses_after_recovery(string state, int dpi, string expected)
    {
        RunInSta(() =>
        {
            var window = new QuotaPopoverWindow();
            try
            {
                window.Apply(CreateQuota());
                var status = Named<TextBlock>(window, "ResetFeedStatusText");
                var section = Named<StackPanel>(window, "ResetSection");
                var announcements = Named<StackPanel>(window, "ResetAnnouncementsPanel");
                // Repeat so a later failure/loading state can reappear after being collapsed.
                for (var transition = 0; transition < 2; transition++)
                {
                    window.ApplyResets(CreateFeed(state));
                    Layout(window, dpi);
                    Assert.Equal(Visibility.Visible, status.Visibility);
                    Assert.Contains(expected, status.Text, StringComparison.Ordinal);
                    Assert.True(status.DesiredSize.Height > 0);
                    Assert.True(announcements.TransformToAncestor(section).Transform(new Point()).Y > 0);
                    AssertPackagedFont(status);

                    window.ApplyResets(CreateFeed("banked"));
                    var content = Layout(window, dpi);
                    AssertSuccessfulSectionStartsWithAnnouncements(window);
                    Assert.Equal(3, announcements.Children.Count);
                    var scroll = Assert.Single(Descendants<ScrollViewer>(content));
                    Assert.Equal(ScrollBarVisibility.Hidden, scroll.VerticalScrollBarVisibility);
                    Assert.Equal(Visibility.Collapsed, scroll.ComputedVerticalScrollBarVisibility);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData("https://codex-resets.com/zh-CN", true)]
    [InlineData("https://x.com/thsottiaux/status/123", false)]
    [InlineData("https://twitter.com/thsottiaux/status/123", false)]
    [InlineData("http://codex-resets.com/zh-CN", false)]
    [InlineData("https://codex-resets.com/", false)]
    [InlineData("https://codex-resets.com/zh-CN#post", false)]
    [InlineData("https://codex-resets.com/zh-CN?next=https://x.com", false)]
    [InlineData("https://user@codex-resets.com/zh-CN", false)]
    [InlineData("https://codex-resets.com:8443/zh-CN", false)]
    [InlineData("https://codex-resets.com.evil.example/zh-CN", false)]
    [InlineData("/zh-CN", false)]
    public void Reset_source_navigation_only_allows_fixed_chinese_site(string address, bool allowed)
    {
        Assert.Equal(allowed, App.IsAllowedResetSource(new Uri(address, UriKind.RelativeOrAbsolute)));
    }

    private static QuotaSnapshot CreateQuota()
    {
        var now = DateTimeOffset.UtcNow;
        return QuotaSnapshot.Available([
            new(QuotaWindowKind.Primary, 72, 300, now.AddHours(3)),
            new(QuotaWindowKind.Secondary, 41, 10080, now.AddDays(5)),
        ], now, "PRO 5X", extraCredits: new(true, false, 120));
    }

    private static ResetFeedSnapshot CreateFeed(string state)
    {
        var now = DateTimeOffset.UtcNow;
        if (state == "loading") return ResetFeedSnapshot.Loading;
        if (state == "unavailable") return ResetFeedSnapshot.Unavailable();
        if (state == "empty") return new([], now, false, null);
        var announcements = Enumerable.Range(0, 5).Select(index =>
        {
            var example = ResetFeedPresentationTests.LocalizedExamples[index % 3];
            return new ResetAnnouncement(
                $"test-{index}", state == "regular" ? ResetKind.Regular : ResetKind.Banked,
                state == "stale" ? now.AddDays(1) : now.AddHours(-2 - index),
                example.Original, new Uri($"https://x.com/thsottiaux/status/{example.Id}"), index == 1)
            {
                ChineseText = example.Chinese,
            };
        }).ToArray();
        return new(announcements, now, state == "stale", state == "stale" ? "offline" : null);
    }

    private static FrameworkElement Layout(Window window, int dpi, double? fixedHeight = null)
    {
        var content = (FrameworkElement)window.Content;
        // An unshown Window has not attached its content to a presentation source.
        VisualTreeHelper.SetRootDpi(content, new DpiScale(dpi / 96d, dpi / 96d));
        content.Measure(new Size(window.Width, fixedHeight ?? window.MaxHeight));
        content.Arrange(new Rect(0, 0, window.Width, fixedHeight ?? content.DesiredSize.Height));
        content.UpdateLayout();
        Assert.Equal(dpi / 96d, VisualTreeHelper.GetDpi(content).PixelsPerDip);
        return content;
    }

    private static T Named<T>(Window window, string name) where T : FrameworkElement =>
        Assert.IsType<T>(window.FindName(name));

    private static void AssertSuccessfulSectionStartsWithAnnouncements(QuotaPopoverWindow window)
    {
        var section = Named<StackPanel>(window, "ResetSection");
        var status = Named<TextBlock>(window, "ResetFeedStatusText");
        var announcements = Named<StackPanel>(window, "ResetAnnouncementsPanel");
        Assert.Equal(string.Empty, status.Text);
        Assert.Equal(Visibility.Collapsed, status.Visibility);
        Assert.Equal(0, status.DesiredSize.Height);
        Assert.Equal(0, status.DesiredSize.Width);
        Assert.Equal(0, announcements.TransformToAncestor(section).Transform(new Point()).Y, 3);
        Assert.DoesNotContain(Descendants<TextBlock>(section), text =>
            text.Text == "公共重置公告"
            || text.Text.Contains("公告不代表个人额度已到账", StringComparison.Ordinal)
            || text.Text.Contains("备用重置需在 Codex 中手动使用", StringComparison.Ordinal)
            || text.Text.Contains("更新于", StringComparison.Ordinal)
            || text.Text.Contains("每15分钟检查", StringComparison.Ordinal));
    }

    private static void AssertPackagedFont(TextBlock text)
    {
        Assert.Equal(OverlayTypography.Text, text.FontFamily);
        var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
        Assert.True(typeface.TryGetGlyphTypeface(out var glyph));
        Assert.Equal("pack", glyph.FontUri.Scheme);
        Assert.Contains("Noto Sans SC", glyph.FamilyNames.Values);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void SaveProof(FrameworkElement content, string name, int dpi)
    {
        // Opt in for local visual review without making ordinary tests write artifacts.
        if (Environment.GetEnvironmentVariable("CODEX_QUOTA_UI_PROOFS") != "1") return;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi / 96d),
            (int)Math.Ceiling(content.ActualHeight * dpi / 96d), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.Combine(RepositoryPaths.Root, "artifacts", "reset-feed-proof");
        Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, $"{name}.png"));
        encoder.Save(stream);
    }

    private static void RunInSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Reset UI verification did not finish in time.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
