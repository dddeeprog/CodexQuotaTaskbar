using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Host.Resets;
using CodexQuotaTaskbar.Host.Tests.TestSupport;
using CodexQuotaTaskbar.Host.UI;
using CodexQuotaTaskbar.Host.Shortcuts;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class ResetFeedWindowTests
{
    [Fact]
    public void Centered_details_shrink_the_scroll_area_and_recover_when_space_returns()
    {
        RunInSta(() =>
        {
            var details = new QuotaPopoverWindow();
            try
            {
                details.Show();
                details.UpdateLayout();
                var originalHeight = details.ActualHeight;
                var targetHeight = Math.Min(280, originalHeight - 30);
                details.FitAvailableHeight(targetHeight);
                Assert.InRange(details.ActualHeight, 1, targetHeight + 1);
                var scroll = Assert.IsType<ScrollViewer>(details.FindName("DetailsScroller"));
                Assert.True(scroll.ActualHeight < 400);
                // Scrolled-out content remains IsVisible in WPF; only fixed controls must stay in view.
                foreach (var button in Descendants<Button>(details).Where(b => b.IsVisible && b.ActualHeight > 0 && !scroll.IsAncestorOf(b)))
                {
                    var point = button.TranslatePoint(new System.Windows.Point(), details);
                    Assert.InRange(point.Y + button.ActualHeight, 0, details.ActualHeight + 1);
                }
                details.FitAvailableHeight(552);
                Assert.InRange(details.ActualHeight, originalHeight - 1, originalHeight + 1);
            }
            finally { details.Close(); }
        });
    }

    [Theory]
    [InlineData(108)]
    [InlineData(144)]
    public void Account_summary_preserves_values_without_metadata_or_reserved_footer_space(int dpi)
    {
        RunInSta(() =>
        {
            var window = new QuotaPopoverWindow();
            try
            {
                var checkedAt = DateTimeOffset.Parse("2026-10-08T19:16:00+08:00");
                var available = SubscriptionExpirationSnapshot.Available(
                    checkedAt.AddMonths(1), checkedAt, SubscriptionExpirationSource.ChatGptWeb);
                SubscriptionExpirationSnapshot[] states = [
                    available, available.AsStale(),
                    SubscriptionExpirationSnapshot.NotProvided(checkedAt, SubscriptionExpirationSource.ChatGptSubscription),
                    SubscriptionExpirationSnapshot.Unavailable(checkedAt, SubscriptionExpirationSource.ChatGptSubscription),
                    SubscriptionExpirationSnapshot.Disabled(),
                ];
                foreach (var state in states)
                {
                    window.Apply(CreateQuota().WithSubscriptionExpiration(state));
                    Layout(window, dpi);
                    Assert.Null(window.FindName("ExpiryMetaText"));
                    Assert.Null(window.FindName("StatusText"));
                    Assert.Null(window.FindName("QuotaHeading"));
                    var summary = Named<Border>(window, "AccountSummaryBorder");
                    var grid = Assert.IsType<Grid>(summary.Child);
                    Assert.Equal(3, grid.RowDefinitions.Count);
                    var text = Descendants<TextBlock>(summary).ToArray();
                    Assert.Equal(6, text.Length);
                    Assert.Equal("PRO 5X", Named<TextBlock>(window, "PlanText").Text);
                    Assert.Equal(QuotaPopoverWindow.FormatMembershipExpiration(state), Named<TextBlock>(window, "ExpiryText").Text);
                    Assert.Equal("120", Named<TextBlock>(window, "ExtraCreditsText").Text);
                    Assert.DoesNotContain(text, item => item.Text.Contains("来源：", StringComparison.Ordinal)
                        || item.Text.Contains("更新于", StringComparison.Ordinal)
                        || item.Text.Contains("缓存", StringComparison.Ordinal));
                    Assert.Equal(2, grid.ColumnDefinitions.Count);
                    Assert.Equal(new Thickness(0), summary.BorderThickness);
                    Assert.Equal(0, Assert.IsType<SolidColorBrush>(summary.Background).Color.A);
                    var expiry = Named<TextBlock>(window, "ExpiryText");
                    Assert.True(((Grid)expiry.Parent).ColumnDefinitions[1].ActualWidth > 180);
                    Assert.True(grid.ActualHeight < 100);
                    foreach (var row in Named<StackPanel>(window, "RowsPanel").Children.OfType<Border>())
                    {
                        Assert.Equal(new Thickness(0), row.BorderThickness);
                        Assert.Equal(0, Assert.IsType<SolidColorBrush>(row.Background).Color.A);
                    }
                    Assert.DoesNotContain(Descendants<TextBlock>((DependencyObject)window.Content), item => item.Text == "USAGE");
                    Assert.Equal(32, Named<Button>(window, "RefreshButton").Height);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData("regular", 108, "常规")]
    [InlineData("banked", 144, "备用")]
    [InlineData("loading", 108, "正在读取")]
    [InlineData("stale", 144, "缓存")]
    [InlineData("unavailable", 108, "暂不可用")]
    [InlineData("empty", 144, "暂无公告")]
    public void Compact_island_keeps_quotas_independent_of_the_historical_reset_feed(string state, int dpi, string expected)
    {
        RunInSta(() =>
        {
            var window = new QuotaCapsuleWindow("reset-feed-test");
            try
            {
                window.Apply(CreateQuota());
                window.ApplyResets(CreateFeed(state));
                var content = Layout(window, dpi, window.Height);

                Assert.Equal(80, window.Height);
                var glass = Named<Border>(window, "GlassBorder");
                Assert.Equal(48, glass.ActualHeight, 2);
                Assert.Equal("5 小时", Named<TextBlock>(window, "FirstLabel").Text);
                Assert.Equal("7 天", Named<TextBlock>(window, "SecondLabel").Text);
                Assert.Equal("72%", Named<TextBlock>(window, "FirstValue").Text);
                Assert.Equal("41%", Named<TextBlock>(window, "SecondValue").Text);
                Assert.Equal(Visibility.Visible, Named<Grid>(window, "SecondRow").Visibility);

                Assert.Null(window.FindName("ResetSummaryText"));
                Assert.Null(window.FindName("ResetSourceLink"));
                Assert.False(window.IsUserDragging);
                Assert.NotEmpty(expected);
                SaveProof(content, $"island-{state}-{dpi}", dpi);
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
    public void Details_show_icon_shortcuts_with_tooltips_navigation_delete_and_an_accessible_add_tile(int dpi)
    {
        RunInSta(() =>
        {
            var window = new QuotaPopoverWindow();
            try
            {
                var shortcuts = CreateShortcuts("available", 24);
                window.Apply(CreateQuota());
                window.ApplyShortcuts(shortcuts);
                window.Opacity = 1;
                var content = Layout(window, dpi);
                var grid = Named<WrapPanel>(window, "ShortcutsGrid");
                var add = Named<Button>(window, "AddShortcutButton");
                Assert.Same(add, grid.Children[grid.Children.Count - 1]);
                var buttons = grid.Children.OfType<Button>().Where(button => !ReferenceEquals(button, add)).ToArray();
                Assert.Equal(shortcuts.Items.Count, buttons.Length);
                var opened = new List<Uri>();
                var deleted = new List<string>();
                window.OpenWebShortcutRequested += opened.Add;
                window.DeleteWebShortcutRequested += deleted.Add;
                var rowTops = new HashSet<double>();
                for (var index = 0; index < buttons.Length; index++)
                {
                    var button = buttons[index];
                    var shortcut = shortcuts.Items[index];
                    Assert.Equal(shortcut.Id, button.Tag);
                    Assert.IsType<Border>(button.Content);
                    var tooltip = Assert.IsType<string>(button.ToolTip);
                    Assert.Contains(shortcut.Name, tooltip);
                    Assert.Contains(shortcut.TargetUri.IdnHost, tooltip);
                    Assert.DoesNotContain(Descendants<TextBlock>(button), text => text.Text == shortcut.Name
                        || text.Text.Contains(shortcut.TargetUri.IdnHost, StringComparison.Ordinal));
                    var bounds = button.TransformToAncestor(grid).TransformBounds(new Rect(button.RenderSize));
                    Assert.True(bounds.Left >= 0 && bounds.Right <= grid.ActualWidth + 0.5,
                        $"Shortcut {index} overflowed horizontally at {dpi} DPI.");
                    rowTops.Add(Math.Round(bounds.Top, 2));
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var menu = Assert.IsType<ContextMenu>(button.ContextMenu);
                    var menuItems = menu.Items.OfType<MenuItem>().ToArray();
                    Assert.Equal(new object[] { "更换图标…", "恢复自动图标", "删除" }, menuItems.Select(item => item.Header));
                    Assert.Equal(4, menu.Items.Count);
                    Assert.IsType<Separator>(menu.Items[2]);
                    Assert.All(menuItems, item => Assert.Equal(shortcut.Id, item.Tag));
                    var remove = Assert.Single(menuItems, item => Equals(item.Header, "删除"));
                    remove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                }
                Assert.True(rowTops.Count > 1);
                Assert.Equal(shortcuts.Items.Select(item => item.TargetUri), opened);
                Assert.Equal(shortcuts.Items.Select(item => item.Id), deleted);
                Assert.Equal(CodexResetFeedClient.SiteUri, opened[0]);
                Assert.Contains("添加", Assert.IsType<string>(add.ToolTip));
                Assert.Null(window.FindName("ResetSiteButton"));
                var rows = Named<StackPanel>(window, "RowsPanel");
                Assert.Equal(2, rows.Children.Count);
                AssertSuccessfulShortcutsSection(window);
                AssertInterfaceFont(Named<TextBlock>(window, "ShortcutsSectionTitle"));

                var scroll = Assert.Single(Descendants<ScrollViewer>(content));
                Assert.Equal(ScrollBarVisibility.Hidden, scroll.VerticalScrollBarVisibility);
                Assert.Equal(Visibility.Collapsed, scroll.ComputedVerticalScrollBarVisibility);
                Assert.True(scroll.ActualHeight <= 400);
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
                var addBounds = add.TransformToAncestor(scroll).TransformBounds(new Rect(add.RenderSize));
                Assert.True(addBounds.Top >= 0 && addBounds.Bottom <= scroll.ActualHeight + 0.5);
                SaveProof(content, $"details-bottom-{dpi}", dpi);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData("loading", 108, "正在读取")]
    [InlineData("loading", 144, "正在读取")]
    [InlineData("unavailable", 108, "暂不可用")]
    [InlineData("unavailable", 144, "暂不可用")]
    [InlineData("retained", 108, "未能更新")]
    [InlineData("retained", 144, "未能更新")]
    public void Details_keep_shortcuts_on_load_failures_and_recover_without_a_reserved_status_row(string state, int dpi, string expected)
    {
        RunInSta(() =>
        {
            var window = new QuotaPopoverWindow();
            try
            {
                window.Apply(CreateQuota());
                var status = Named<TextBlock>(window, "ShortcutsStatusText");
                var section = Named<StackPanel>(window, "ShortcutsSection");
                var grid = Named<WrapPanel>(window, "ShortcutsGrid");
                // Repeat to cover failure and recovery after a successful display.
                for (var transition = 0; transition < 2; transition++)
                {
                    var shortcuts = CreateShortcuts(state);
                    window.ApplyShortcuts(shortcuts);
                    Layout(window, dpi);
                    Assert.Equal(Visibility.Visible, status.Visibility);
                    Assert.Contains(expected, status.Text, StringComparison.Ordinal);
                    Assert.True(status.DesiredSize.Height > 0);
                    Assert.True(grid.TransformToAncestor(section).Transform(new Point()).Y > 0);
                    AssertInterfaceFont(status);
                    Assert.Equal(shortcuts.Items.Count + 1, grid.Children.Count);

                    window.ApplyShortcuts(CreateShortcuts("available"));
                    var content = Layout(window, dpi);
                    AssertSuccessfulShortcutsSection(window);
                    Assert.Equal(4, grid.Children.Count);
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
    [InlineData(108)]
    [InlineData(144)]
    public void Empty_shortcuts_keep_only_the_add_icon_without_restoring_a_deleted_default(int dpi)
    {
        RunInSta(() =>
        {
            var window = new QuotaPopoverWindow();
            try
            {
                window.Apply(CreateQuota());
                window.ApplyShortcuts(new WebShortcutsSnapshot([]));
                Layout(window, dpi);
                var grid = Named<WrapPanel>(window, "ShortcutsGrid");
                Assert.Same(Named<Button>(window, "AddShortcutButton"), Assert.Single(grid.Children.Cast<UIElement>()));
                AssertSuccessfulShortcutsSection(window);
            }
            finally { window.Close(); }
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

    private static WebShortcutsSnapshot CreateShortcuts(string state, int count = 3)
    {
        if (state == "loading") return new([], null, true);
        if (state == "unavailable") return new([], "offline");
        var items = Enumerable.Range(0, count).Select(index => index == 0
            ? new WebShortcut("reset", "重置公告", CodexResetFeedClient.SiteUri)
            : new WebShortcut($"test-{index}", $"测试网站 {index}", new Uri($"https://site-{index}.example/"))).ToArray();
        return new(items, state == "retained" ? "offline" : null);
    }

    private static FrameworkElement Layout(Window window, int dpi, double? fixedHeight = null)
    {
        var content = (FrameworkElement)window.Content;
        // Initialize WPF's system-DPI cache before synthetic scales are inserted.
        // Otherwise the first HWND created by a parallel test can insert the system
        // scale at index zero and invalidate an unshown visual's synthetic-DPI index.
        // This source has no WS_VISIBLE style, so it is never shown or activated.
        using var dpiSource = new HwndSource(new HwndSourceParameters("ResetFeedDpiFixture")
        {
            Width = 0, Height = 0, WindowStyle = 0,
        });
        // An unshown Window has not attached its content to a presentation source.
        VisualTreeHelper.SetRootDpi(content, new DpiScale(dpi / 96d, dpi / 96d));
        content.Measure(new Size(window.Width, fixedHeight ?? window.MaxHeight));
        content.Arrange(new Rect(0, 0, window.Width, fixedHeight ?? content.DesiredSize.Height));
        content.UpdateLayout();
        Assert.Equal(dpi / 96d, VisualTreeHelper.GetDpi(content).PixelsPerDip);
        return content;
    }

    [Theory]
    [InlineData(108)]
    [InlineData(144)]
    public void Details_group_account_quota_and_shortcuts_with_clear_boundaries(int dpi)
    {
        RunInSta(() =>
        {
            var window = new QuotaPopoverWindow();
            try
            {
                window.Apply(CreateQuota());
                window.ApplyShortcuts(CreateShortcuts("available"));
                var content = Layout(window, dpi);
                var scroll = Assert.Single(Descendants<ScrollViewer>(content));
                var account = Named<StackPanel>(window, "AccountSection");
                var quota = Named<StackPanel>(window, "QuotaSection");
                var shortcuts = Named<StackPanel>(window, "ShortcutsSection");
                var shortcutsGroup = Named<StackPanel>(window, "AnnouncementsSection");
                Assert.Same(account, Named<Border>(window, "AccountSummaryBorder").Parent);
                Assert.Same(account, Named<Button>(window, "SubscriptionLoginButton").Parent);
                Assert.Same(quota, Named<StackPanel>(window, "RowsPanel").Parent);
                Assert.Same(shortcuts, Named<WrapPanel>(window, "ShortcutsGrid").Parent);
                Assert.Same(shortcutsGroup, shortcuts.Parent);
                var previousBottom = 0d;
                foreach (var (group, name, label) in new[]
                {
                    (account, "AccountSectionTitle", "账户与订阅"),
                    (quota, "QuotaSectionTitle", "额度"),
                    (shortcutsGroup, "ShortcutsSectionTitle", "常用网站"),
                })
                {
                    var title = Named<TextBlock>(window, name);
                    Assert.Equal(label, title.Text);
                    Assert.Equal(OverlayTypography.Interface, title.FontFamily);
                    Assert.Same(group, title.Parent);
                    var bounds = group.TransformToAncestor(scroll).TransformBounds(new Rect(group.RenderSize));
                    Assert.True(bounds.Top >= previousBottom);
                    previousBottom = bounds.Bottom;
                    var titleBounds = title.TransformToAncestor(scroll).TransformBounds(new Rect(title.RenderSize));
                    Assert.True(titleBounds.Bottom <= scroll.ViewportHeight, label + " should be discoverable on initial open.");
                }
                foreach (var name in new[] { "AccountSectionDivider", "QuotaSectionDivider" })
                    Assert.Equal(new Thickness(0, 0, 0, 1), Named<Border>(window, name).BorderThickness);
                Assert.True(content.ActualHeight <= window.MaxHeight);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(96)]
    [InlineData(108)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void Section_dividers_render_visible_pixels_with_layout_rounding(int dpi)
    {
        RunInSta(() =>
        {
            var window = new QuotaPopoverWindow();
            try
            {
                foreach (var name in new[] { "AccountSectionDivider", "QuotaSectionDivider" })
                {
                    var source = Named<Border>(window, name);
                    // Render just the rule so text/background cannot hide a missing stroke.
                    var rule = new Border
                    {
                        BorderThickness = source.BorderThickness,
                        BorderBrush = source.BorderBrush,
                        UseLayoutRounding = true,
                        SnapsToDevicePixels = source.SnapsToDevicePixels,
                    };
                    VisualTreeHelper.SetRootDpi(rule, new DpiScale(dpi / 96d, dpi / 96d));
                    rule.Measure(new Size(200, 8));
                    rule.Arrange(new Rect(0, 0, 200, 8));
                    rule.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(200 * dpi / 96d),
                        (int)Math.Ceiling(8 * dpi / 96d), dpi, dpi, PixelFormats.Pbgra32);
                    bitmap.Render(rule);
                    var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                    bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                    var maxAlpha = Enumerable.Range(0, bitmap.PixelHeight)
                        .Max(y => pixels[(y * bitmap.PixelWidth + bitmap.PixelWidth / 2) * 4 + 3]);
                    Assert.True(maxAlpha >= 64, $"{name} at {dpi} DPI has no visible rule: alpha={maxAlpha}.");
                }
            }
            finally { window.Close(); }
        });
    }

    private static T Named<T>(Window window, string name) where T : FrameworkElement =>
        Assert.IsType<T>(window.FindName(name));

    private static void AssertSuccessfulShortcutsSection(QuotaPopoverWindow window)
    {
        var section = Named<StackPanel>(window, "ShortcutsSection");
        var status = Named<TextBlock>(window, "ShortcutsStatusText");
        Assert.Equal(Visibility.Collapsed, status.Visibility);
        Assert.Equal(0, status.DesiredSize.Height);
        Assert.DoesNotContain(Descendants<TextBlock>(section), text =>
            text.Text == "公共重置公告"
            || text.Text.Contains("公告不代表个人额度已到账", StringComparison.Ordinal)
            || text.Text.Contains("备用重置需在 Codex 中手动使用", StringComparison.Ordinal)
            || text.Text.Contains("更新于", StringComparison.Ordinal)
            || text.Text.Contains("每15分钟检查", StringComparison.Ordinal));
    }

    private static void AssertInterfaceFont(TextBlock text)
    {
        Assert.Equal(OverlayTypography.Interface, text.FontFamily);
        // Some announcement text is below the scroll viewport and has no drawing yet.
        // Actual system-font glyphs and clipping are verified in UnifiedTypographyTests.
        Assert.Equal(TextFormattingMode.Ideal, TextOptions.GetTextFormattingMode(text));
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
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling((content.ActualWidth + content.Margin.Left + content.Margin.Right) * dpi / 96d),
            (int)Math.Ceiling((content.ActualHeight + content.Margin.Top + content.Margin.Bottom) * dpi / 96d), dpi, dpi, PixelFormats.Pbgra32);
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
