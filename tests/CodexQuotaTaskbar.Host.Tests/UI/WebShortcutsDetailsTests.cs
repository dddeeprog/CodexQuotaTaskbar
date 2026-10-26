using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Host.Shortcuts;
using CodexQuotaTaskbar.Host.Tests.TestSupport;
using CodexQuotaTaskbar.Host.UI;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using MenuItem = System.Windows.Controls.MenuItem;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using TextBox = System.Windows.Controls.TextBox;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class WebShortcutsDetailsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-23T18:00:00Z");

    [Fact]
    public void Details_replace_only_the_old_list_with_icon_only_websites_and_an_add_tile() => Sta(() =>
    {
        var window = new QuotaPopoverWindow();
        try
        {
            window.Apply(Quota());
            var rows = Named<StackPanel>(window, "RowsPanel").Children.Cast<UIElement>().ToArray();
            var sites = Examples();
            window.ApplyShortcuts(new(sites));
            Assert.Equal("常用网站", Named<TextBlock>(window, "ShortcutsSectionTitle").Text);
            Assert.Null(window.FindName("UpdatesSection"));
            Assert.Null(window.FindName("ResetAnnouncementsPanel"));
            Assert.Null(window.FindName("ResetSiteButton"));
            Assert.Equal(Visibility.Collapsed, Named<TextBlock>(window, "ShortcutsStatusText").Visibility);
            Assert.Equal(rows, Named<StackPanel>(window, "RowsPanel").Children.Cast<UIElement>());
            var grid = Named<WrapPanel>(window, "ShortcutsGrid");
            Assert.Equal(sites.Count + 1, grid.Children.Count);
            Assert.Same(Named<Button>(window, "AddShortcutButton"), grid.Children[^1]);
            foreach (var button in grid.Children.OfType<Button>().Where(button => button.Tag is string))
            {
                var site = sites.Single(site => site.Id == (string)button.Tag);
                Assert.Contains(site.Name, Assert.IsType<string>(button.ToolTip), StringComparison.Ordinal);
                Assert.Contains(site.TargetUri.IdnHost, Assert.IsType<string>(button.ToolTip), StringComparison.Ordinal);
                var icon = Assert.IsType<Border>(button.Content);
                var fallback = Assert.IsType<TextBlock>(icon.Child);
                Assert.Equal(1, fallback.Text.Length);
                Assert.DoesNotContain(site.Name, fallback.Text, StringComparison.Ordinal);
            }
            Assert.False(window.IsVisible);
            Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Handle);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Normal_click_opens_only_the_url_while_deletion_requires_the_context_menu_action() => Sta(() =>
    {
        var window = new QuotaPopoverWindow();
        try
        {
            var site = Examples()[0] with { TargetUri = new Uri("https://chatgpt.com/?mode=example#saved") };
            window.ApplyShortcuts(new([site]));
            var opened = new List<Uri>();
            var deleted = new List<string>();
            window.OpenWebShortcutRequested += opened.Add;
            window.DeleteWebShortcutRequested += deleted.Add;
            var button = Assert.IsType<Button>(Named<WrapPanel>(window, "ShortcutsGrid").Children[0]);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(site.TargetUri.AbsoluteUri, Assert.Single(opened).AbsoluteUri);
            Assert.Empty(deleted);
            var remove = button.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "删除"));
            Assert.Equal("删除", remove.Header);
            remove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(site.Id, Assert.Single(deleted));
            Assert.Single(opened);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Manual_icon_selection_is_explicit_cancel_safe_and_automatic_restore_is_only_available_for_manual_icons() => Sta(() =>
    {
        var window = new QuotaPopoverWindow();
        try
        {
            var site = Examples()[0];
            window.ApplyShortcuts(new([site]));
            var changed = new List<(string Id, string Path)>();
            var restored = new List<string>();
            window.ChangeWebShortcutIconRequested += (id, path) => changed.Add((id, path));
            window.RestoreAutomaticWebShortcutIconRequested += restored.Add;
            var button = Assert.IsType<Button>(Named<WrapPanel>(window, "ShortcutsGrid").Children[0]);
            Assert.False(button.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "恢复自动图标")).IsEnabled);
            Assert.False(window.ApplySelectedShortcutIcon(site.Id, null));
            Assert.False(window.ApplySelectedShortcutIcon(site.Id, @"C:\example\unsafe.svg"));
            Assert.Empty(changed);
            Assert.False(window.RestoreAutomaticIcon(site.Id));
            Assert.Empty(restored);
            Assert.True(window.ApplySelectedShortcutIcon(site.Id, @"C:\example\custom.png"));
            Assert.Equal((site.Id, @"C:\example\custom.png"), Assert.Single(changed));
            Assert.IsType<TextBlock>(Assert.IsType<Border>(button.Content).Child);
            window.ApplyShortcuts(new([site with { ManualIconKey = "local-example" }]));
            button = Assert.IsType<Button>(Named<WrapPanel>(window, "ShortcutsGrid").Children[0]);
            var restore = button.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "恢复自动图标"));
            Assert.True(restore.IsEnabled);
            restore.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(site.Id, Assert.Single(restored));
            Assert.False(window.IsVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Changing_manual_selection_or_target_drops_the_old_image_before_the_new_icon_arrives() => Sta(() =>
    {
        var window = new QuotaPopoverWindow();
        try
        {
            var site = Examples()[0];
            var source = SampleIcon(0);
            window.ApplyShortcuts(new([site]));
            window.ApplyShortcutIcon(site.Id, new(null, null) { Image = source });
            var grid = Named<WrapPanel>(window, "ShortcutsGrid");
            Assert.IsType<Image>(Assert.IsType<Border>(Assert.IsType<Button>(grid.Children[0]).Content).Child);
            var manual = site with { ManualIconKey = "manual-example" };
            window.ApplyShortcuts(new([manual]));
            Assert.IsType<TextBlock>(Assert.IsType<Border>(Assert.IsType<Button>(grid.Children[0]).Content).Child);
            window.ApplyShortcutIcon(site.Id, new(null, null) { Image = source });
            window.ApplyShortcuts(new([site]));
            Assert.IsType<TextBlock>(Assert.IsType<Border>(Assert.IsType<Button>(grid.Children[0]).Content).Child);
            window.ApplyShortcutIcon(site.Id, new(null, null) { Image = source });
            window.ApplyShortcuts(new([site with { TargetUri = new Uri("https://example.com/changed") }]));
            Assert.IsType<TextBlock>(Assert.IsType<Border>(Assert.IsType<Button>(grid.Children[0]).Content).Child);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void A_frozen_icon_replaces_the_placeholder_without_changing_the_grid_or_using_its_path() => Sta(() =>
    {
        var window = new QuotaPopoverWindow();
        try
        {
            var sites = Examples();
            window.ApplyShortcuts(new(sites));
            var grid = Named<WrapPanel>(window, "ShortcutsGrid");
            var button = Assert.IsType<Button>(grid.Children[0]);
            var source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 255, 255, 255, 255 }, 4);
            source.Freeze();
            window.ApplyShortcutIcon(sites[0].Id, new WebShortcutIcon("Fetched site title", "path-is-never-read-by-ui") { Image = source });
            var image = Assert.IsType<Image>(Assert.IsType<Border>(button.Content).Child);
            Assert.Same(source, image.Source);
            Assert.Equal(32, image.Width);
            Assert.Contains("Fetched site title", Assert.IsType<string>(button.ToolTip), StringComparison.Ordinal);
            Assert.Equal(sites.Count + 1, grid.Children.Count);
            window.ApplyShortcuts(new(sites));
            Assert.IsType<Image>(Assert.IsType<Border>(Assert.IsType<Button>(grid.Children[0]).Content).Child);
            window.ApplyShortcuts(new([]));
            window.ApplyShortcutIcon(sites[0].Id, new(null, null) { Image = source });
            Assert.Single(grid.Children);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Empty_loading_and_failed_lists_remain_distinct_and_keep_existing_tiles() => Sta(() =>
    {
        var window = new QuotaPopoverWindow();
        try
        {
            window.ApplyShortcuts(new([], IsLoading: true));
            Assert.Equal("正在读取网站…", Named<TextBlock>(window, "ShortcutsStatusText").Text);
            Assert.False(Named<Button>(window, "AddShortcutButton").IsEnabled);
            window.ApplyShortcuts(new([]));
            Assert.Single(Named<WrapPanel>(window, "ShortcutsGrid").Children);
            Assert.Equal(Visibility.Collapsed, Named<TextBlock>(window, "ShortcutsStatusText").Visibility);
            Assert.True(Named<Button>(window, "AddShortcutButton").IsEnabled);
            window.ApplyShortcuts(new(Examples(), "read failure"));
            Assert.Equal(4, Named<WrapPanel>(window, "ShortcutsGrid").Children.Count);
            Assert.Equal("网站列表未能更新", Named<TextBlock>(window, "ShortcutsStatusText").Text);
            Assert.Equal("read failure", Named<TextBlock>(window, "ShortcutsStatusText").ToolTip);
            window.ApplyShortcuts(new([], "invalid cache"));
            Assert.Equal("网站列表暂不可用", Named<TextBlock>(window, "ShortcutsStatusText").Text);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/private")]
    public void Unsafe_addresses_never_become_clickable_tiles(string address) => Sta(() =>
    {
        var window = new QuotaPopoverWindow();
        try
        {
            window.ApplyShortcuts(new([new("unsafe", "Unsafe", new Uri(address))]));
            Assert.Single(Named<WrapPanel>(window, "ShortcutsGrid").Children);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(96, 360)]
    [InlineData(144, 360)]
    [InlineData(144, 300)]
    public void Icon_grid_wraps_inside_the_existing_scroll_area_without_text_or_width_growth(int dpi, int width) => Sta(() =>
    {
        using var fixture = DpiFixture();
        var window = new QuotaPopoverWindow { Width = width };
        try
        {
            window.Apply(Quota());
            var sites = Enumerable.Range(0, 12).Select(i => new WebShortcut($"site-{i}", $"Example site {i}", new Uri($"https://example.com/{i}"))).ToArray();
            window.ApplyShortcuts(new(sites));
            var content = Layout(window, dpi);
            var scroll = Named<ScrollViewer>(window, "DetailsScroller");
            scroll.ScrollToBottom();
            content.UpdateLayout();
            var grid = Named<WrapPanel>(window, "ShortcutsGrid");
            var tiles = grid.Children.OfType<Button>().ToArray();
            Assert.Equal(13, tiles.Length);
            Assert.True(grid.ActualHeight > 58);
            foreach (var tile in tiles)
            {
                var bounds = tile.TransformToAncestor(grid).TransformBounds(new Rect(tile.RenderSize));
                Assert.True(bounds.Left >= 0 && bounds.Right <= grid.ActualWidth + 1);
                Assert.Equal(52, tile.ActualWidth);
                Assert.Equal(52, tile.ActualHeight);
            }
            Assert.True(content.ActualWidth + content.Margin.Left + content.Margin.Right <= width + 1);
            Assert.False(window.IsVisible);
            Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Handle);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Adding_validates_without_network_and_IME_composition_cannot_accept_the_dialog() => Sta(() =>
    {
        var dialog = new WebShortcutAddWindow();
        try
        {
            var input = Named<TextBox>(dialog, "UrlInput");
            input.Text = "javascript:alert(1)";
            Assert.False(dialog.TryAcceptInput());
            Assert.Null(dialog.AcceptedAddress);
            Assert.Equal(Visibility.Visible, Named<TextBlock>(dialog, "ErrorText").Visibility);
            input.Text = "github.com";
            var composition = new TextComposition(InputManager.Current, input, "github", TextCompositionAutoComplete.Off);
            input.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent });
            Assert.False(dialog.TryAcceptInput());
            input.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.True(dialog.TryAcceptInput());
            Assert.Equal("https://github.com/", dialog.AcceptedAddress);
            Assert.False(Named<Button>(dialog, "AcceptButton").IsDefault);
            Assert.True(Named<Button>(dialog, "CancelButton").IsCancel);
            Assert.False(dialog.IsVisible);
            Assert.Equal(IntPtr.Zero, new WindowInteropHelper(dialog).Handle);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void Adding_can_attach_a_local_icon_and_clearing_or_canceling_never_reuses_an_unconfirmed_path() => Sta(() =>
    {
        var dialog = new WebShortcutAddWindow();
        try
        {
            Named<TextBox>(dialog, "UrlInput").Text = "github.com";
            Assert.Null(dialog.SelectedIconPath);
            Assert.False(dialog.ApplyIconSelection(null));
            Assert.False(dialog.ApplyIconSelection(@"C:\example\file.exe"));
            Assert.Null(dialog.SelectedIconPath);
            Assert.True(dialog.ApplyIconSelection(@"C:\example\local-icon.JPG"));
            Assert.Equal("local-icon.JPG", Named<TextBlock>(dialog, "SelectedIconText").Text);
            Assert.False(dialog.ApplyIconSelection(null));
            Assert.Equal(@"C:\example\local-icon.JPG", dialog.SelectedIconPath);
            Assert.True(dialog.TryAcceptInput());
            Assert.Equal(new WebShortcutAddRequest("https://github.com/", @"C:\example\local-icon.JPG"), dialog.AcceptedRequest);
            dialog.ClearIconSelection();
            Assert.Null(dialog.SelectedIconPath);
            Assert.Null(dialog.AcceptedRequest);
            Assert.Equal(Visibility.Collapsed, Named<Button>(dialog, "ClearIconButton").Visibility);
            Assert.True(dialog.TryAcceptInput());
            Assert.Null(dialog.AcceptedRequest!.LocalIconPath);
            Assert.False(dialog.IsVisible);
            Assert.Equal(IntPtr.Zero, new WindowInteropHelper(dialog).Handle);
        }
        finally { dialog.Close(); }
    });

    [Theory]
    [InlineData(Key.Enter, false, false, false, true)]
    [InlineData(Key.Enter, true, false, false, false)]
    [InlineData(Key.Enter, false, true, false, false)]
    [InlineData(Key.Enter, false, false, true, false)]
    [InlineData(Key.ImeProcessed, false, false, false, false)]
    public void Only_an_unhandled_ordinary_enter_accepts_the_address(Key key, bool composing, bool imeEnter, bool repeat, bool expected) =>
        Assert.Equal(expected, WebShortcutAddWindow.ShouldAcceptKey(key, Key.Enter, composing, imeEnter, repeat));

    [Fact]
    public void Optional_preview_renders_only_explicit_example_sites_and_never_shows_a_window() => Sta(() =>
    {
        var configured = Environment.GetEnvironmentVariable("CQT_WEB_SHORTCUTS_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(configured)) return;
        var directory = Path.GetFullPath(configured);
        Assert.Equal(Path.GetFullPath(Path.Combine(RepositoryPaths.Root, "..", "web-shortcuts-preview")), directory, ignoreCase: true);
        Directory.CreateDirectory(directory);
        using var fixture = DpiFixture();
        var window = new QuotaPopoverWindow();
        try
        {
            window.Apply(Quota());
            var sites = Examples().Select((site, index) => index < 2 ? site with { ManualIconKey = index.ToString("x32") + ".png" } : site).ToArray();
            window.ApplyShortcuts(new(sites));
            // Local geometric examples, not downloaded website logos or real user selections.
            window.ApplyShortcutIcon(sites[0].Id, new("本地图标示例", null) { Image = SampleIcon(0) });
            window.ApplyShortcutIcon(sites[1].Id, new("本地图标示例", null) { Image = SampleIcon(1) });
            Named<TextBlock>(window, "ShortcutsSectionTitle").Text = "常用网站 · 图标示例";
            var tiles = Named<WrapPanel>(window, "ShortcutsGrid").Children.OfType<Button>().Where(button => button.Tag is string).ToArray();
            Assert.Equal(2, tiles.Count(button => ((Border)button.Content).Background is ImageBrush));
            Assert.IsType<TextBlock>(((Border)tiles[2].Content).Child);
            var content = Layout(window, 144);
            Named<ScrollViewer>(window, "DetailsScroller").ScrollToBottom();
            content.UpdateLayout();
            Assert.False(window.IsVisible);
            Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Handle);
            var imageWidth = content.ActualWidth + content.Margin.Left + content.Margin.Right;
            var imageHeight = content.ActualHeight + content.Margin.Top + content.Margin.Bottom;
            Assert.Equal(window.Width, imageWidth, 3);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(imageWidth * 1.5), (int)Math.Ceiling(imageHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(directory, "web-shortcuts-manual-144.png"));
            encoder.Save(output);
        }
        finally { window.Close(); }
    });

    private static BitmapSource SampleIcon(int index)
    {
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRoundedRectangle(new SolidColorBrush(index == 0 ? System.Windows.Media.Color.FromRgb(69, 139, 127)
                : System.Windows.Media.Color.FromRgb(111, 101, 179)), null, new Rect(1, 1, 62, 62), 14, 14);
            if (index == 0)
            {
                draw.DrawEllipse(System.Windows.Media.Brushes.White, null, new Point(32, 32), 19, 19);
                draw.DrawEllipse(System.Windows.Media.Brushes.DimGray, null, new Point(26, 31), 3, 5);
                draw.DrawEllipse(System.Windows.Media.Brushes.DimGray, null, new Point(38, 31), 3, 5);
            }
            else
            {
                var pen = new System.Windows.Media.Pen(System.Windows.Media.Brushes.White, 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                draw.DrawLine(pen, new Point(25, 21), new Point(15, 32));
                draw.DrawLine(pen, new Point(15, 32), new Point(25, 43));
                draw.DrawLine(pen, new Point(39, 21), new Point(49, 32));
                draw.DrawLine(pen, new Point(49, 32), new Point(39, 43));
            }
        }
        var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static IReadOnlyList<WebShortcut> Examples() => [
        new("example-chatgpt", "ChatGPT", new Uri("https://chatgpt.com/")),
        new("example-github", "GitHub", new Uri("https://github.com/")),
        new("example-reset", "重置公告", new Uri("https://codex-resets.com/zh-CN")),
    ];

    private static QuotaSnapshot Quota() => QuotaSnapshot.Available([
        new(QuotaWindowKind.Primary, 64, 300, Now.AddHours(3)), new(QuotaWindowKind.Secondary, 72, 10080, Now.AddDays(5)),
    ], Now, "示例订阅", SubscriptionExpirationSnapshot.NotProvided(Now, SubscriptionExpirationSource.None), new QuotaCreditsSnapshot(false, false, 0));

    private static T Named<T>(Window window, string name) where T : FrameworkElement => Assert.IsType<T>(window.FindName(name));
    private static HwndSource DpiFixture() => new(new HwndSourceParameters("WebShortcutsDpiFixture") { Width = 0, Height = 0, WindowStyle = 0 });
    private static FrameworkElement Layout(QuotaPopoverWindow window, int dpi)
    {
        var content = (FrameworkElement)window.Content;
        VisualTreeHelper.SetRootDpi(content, new DpiScale(dpi / 96d, dpi / 96d));
        content.Measure(new Size(window.Width, window.MaxHeight));
        content.Arrange(new Rect(new Point(), new Size(window.Width, Math.Min(window.MaxHeight, content.DesiredSize.Height))));
        content.UpdateLayout();
        return content;
    }

    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception failure) { error = failure; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Offline shortcut UI verification timed out.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
