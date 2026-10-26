using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Host.Shortcuts;
using CodexQuotaTaskbar.Host.Tests.TestSupport;
using CodexQuotaTaskbar.Host.UI;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class WebShortcutTileFillTests
{
    [Theory]
    [InlineData(120, 120, 96)]
    [InlineData(240, 80, 96)]
    [InlineData(80, 240, 96)]
    [InlineData(120, 120, 144)]
    [InlineData(240, 80, 144)]
    [InlineData(80, 240, 144)]
    [InlineData(120, 120, 192)]
    [InlineData(240, 80, 192)]
    [InlineData(80, 240, 192)]
    public void Manual_images_fill_the_tile_with_round_corners_and_centered_proportional_crop(int width, int height, int dpi) => Sta(() =>
    {
        using var fixture = DpiFixture();
        var window = new QuotaPopoverWindow();
        try
        {
            var site = Site("manual") with { ManualIconKey = new string('a', 32) + ".png" };
            window.ApplyShortcuts(new([site]));
            window.ApplyShortcutIcon(site.Id, new(null, null) { Image = Gradient(width, height) });
            Layout(window, dpi);
            var button = Tile(window, site.Id);
            var icon = Assert.IsType<Border>(button.Content);
            var image = Assert.IsType<ImageBrush>(icon.Background);
            Assert.Equal(52, icon.ActualWidth, 3);
            Assert.Equal(52, icon.ActualHeight, 3);
            Assert.Null(icon.Child);
            Assert.Equal(Stretch.UniformToFill, image.Stretch);
            Assert.Equal(AlignmentX.Center, image.AlignmentX);
            Assert.Equal(AlignmentY.Center, image.AlignmentY);
            Assert.False(icon.IsHitTestVisible);
            var clip = Assert.IsType<RectangleGeometry>(icon.Clip);
            Assert.Equal(new Rect(0, 0, 52, 52), clip.Rect);
            Assert.Equal(13, clip.RadiusX);
            Assert.Equal(13, clip.RadiusY);

            var bitmap = RenderIcon(icon, dpi);
            foreach (var corner in new[] { new Point(1, 1), new Point(50, 1), new Point(1, 50), new Point(50, 50) })
                Assert.Equal(0, Pixel(bitmap, corner.X, corner.Y, dpi).A);
            // Opaque pixels near each flat edge distinguish full coverage from the old 32 DIP inset.
            foreach (var edge in new[] { new Point(26, 2), new Point(50, 26), new Point(26, 50), new Point(2, 26) })
                Assert.Equal(255, Pixel(bitmap, edge.X, edge.Y, dpi).A);

            // The 2-D source gradient detects stretching and off-center crop in both axes.
            foreach (var point in new[] { new Point(14, 26), new Point(38, 26), new Point(26, 14), new Point(26, 38) })
            {
                var actual = Pixel(bitmap, point.X, point.Y, dpi);
                var expected = ExpectedGradient(width, height, point.X, point.Y, dpi);
                Assert.InRange(Math.Abs(actual.R - expected.R), 0, 8);
                Assert.InRange(Math.Abs(actual.G - expected.G), 0, 8);
            }
            Assert.False(window.IsVisible);
            Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Handle);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void Manual_fill_preserves_transparent_source_pixels_instead_of_inventing_an_opaque_backplate(int dpi) => Sta(() =>
    {
        using var fixture = DpiFixture();
        var window = new QuotaPopoverWindow();
        try
        {
            var site = Site("alpha") with { ManualIconKey = new string('b', 32) + ".png" };
            window.ApplyShortcuts(new([site]));
            window.ApplyShortcutIcon(site.Id, new(null, null) { Image = Gradient(120, 120, transparentLeftHalf: true) });
            Layout(window, dpi);
            var icon = Assert.IsType<Border>(Tile(window, site.Id).Content);
            var bitmap = RenderIcon(icon, dpi);
            Assert.Equal(0, Pixel(bitmap, 14, 26, dpi).A);
            Assert.Equal(255, Pixel(bitmap, 38, 26, dpi).A);
            Assert.Equal(0, Pixel(bitmap, 1, 1, dpi).A);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void Automatic_favicons_fallback_letters_and_add_tile_keep_their_existing_sizes(int dpi) => Sta(() =>
    {
        using var fixture = DpiFixture();
        var window = new QuotaPopoverWindow();
        try
        {
            var automatic = Site("automatic");
            var fallback = Site("fallback");
            window.ApplyShortcuts(new([automatic, fallback]));
            window.ApplyShortcutIcon(automatic.Id, new(null, null) { Image = Gradient(240, 80) });
            Layout(window, dpi);
            var icon = Assert.IsType<Border>(Tile(window, automatic.Id).Content);
            Assert.Equal(32, icon.Width);
            Assert.Equal(32, icon.Height);
            Assert.Null(icon.Clip);
            Assert.Equal(new CornerRadius(0), icon.CornerRadius);
            var image = Assert.IsType<Image>(icon.Child);
            Assert.Equal(32, image.Width);
            Assert.Equal(32, image.Height);
            Assert.Equal(Stretch.Uniform, image.Stretch);
            var letter = Assert.IsType<Border>(Tile(window, fallback.Id).Content);
            Assert.Equal(32, letter.Width);
            Assert.IsType<TextBlock>(letter.Child);
            var add = Assert.IsType<Button>(window.FindName("AddShortcutButton"));
            Assert.Equal("+", add.Content);
            Assert.Equal(52, add.ActualWidth, 3);
            Assert.Equal(52, add.ActualHeight, 3);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Hover_and_focus_outline_are_drawn_above_the_manual_image() => Sta(() =>
    {
        using var fixture = DpiFixture();
        var window = new QuotaPopoverWindow();
        try
        {
            var site = Site("template") with { ManualIconKey = new string('c', 32) + ".png" };
            window.ApplyShortcuts(new([site]));
            window.ApplyShortcutIcon(site.Id, new(null, null) { Image = Gradient(120, 120) });
            Layout(window, 144);
            var tile = Tile(window, site.Id);
            tile.ApplyTemplate();
            var hover = Assert.IsType<Border>(tile.Template.FindName("ShortcutHover", tile));
            var outline = Assert.IsType<Border>(tile.Template.FindName("ShortcutOutline", tile));
            var templateGrid = Assert.IsType<Grid>(VisualTreeHelper.GetParent(hover));
            var presenter = Assert.Single(templateGrid.Children.OfType<ContentPresenter>());
            Assert.True(templateGrid.Children.IndexOf(outline) > templateGrid.Children.IndexOf(presenter));
            Assert.True(templateGrid.Children.IndexOf(hover) > templateGrid.Children.IndexOf(presenter));
            Assert.False(hover.IsHitTestVisible);
            Assert.False(outline.IsHitTestVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Optional_preview_uses_only_generated_example_images_and_keeps_a_letter_fallback() => Sta(() =>
    {
        var configured = Environment.GetEnvironmentVariable("CQT_WEB_SHORTCUTS_FILL_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(configured)) return;
        var directory = Path.GetFullPath(configured);
        Assert.Equal(Path.GetFullPath(Path.Combine(RepositoryPaths.Root, "..", "web-shortcuts-preview")), directory, ignoreCase: true);
        Directory.CreateDirectory(directory);
        using var fixture = DpiFixture();
        var window = new QuotaPopoverWindow();
        try
        {
            var now = DateTimeOffset.Parse("2026-10-23T18:00:00Z");
            window.Apply(QuotaSnapshot.Available([
                new(QuotaWindowKind.Primary, 64, 300, now.AddHours(3)),
                new(QuotaWindowKind.Secondary, 72, 10080, now.AddDays(5)),
            ], now, "示例订阅", SubscriptionExpirationSnapshot.NotProvided(now, SubscriptionExpirationSource.None), new(false, false, 0)));
            var sites = Enumerable.Range(0, 5).Select(index => Site($"example-{index}") with
            {
                Name = new[] { "方形示例", "横图示例", "竖图示例", "透明示例", "字母占位示例" }[index],
                ManualIconKey = index < 4 ? index.ToString("x32") + ".png" : null,
            }).ToArray();
            window.ApplyShortcuts(new(sites));
            for (var index = 0; index < 4; index++)
                window.ApplyShortcutIcon(sites[index].Id, new("纯示例图片", null) { Image = ExamplePicture(index) });
            Assert.IsType<TextBlock>(Assert.IsType<Border>(Tile(window, sites[4].Id).Content).Child);
            Assert.IsType<TextBlock>(window.FindName("ShortcutsSectionTitle")).Text = "常用网站 · 满铺示例";
            var content = Layout(window, 144);
            Assert.IsType<ScrollViewer>(window.FindName("DetailsScroller")).ScrollToBottom();
            content.UpdateLayout();
            Assert.False(window.IsVisible);
            Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Handle);
            var width = content.ActualWidth + content.Margin.Left + content.Margin.Right;
            var height = content.ActualHeight + content.Margin.Top + content.Margin.Bottom;
            Assert.Equal(window.Width, width, 3);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * 1.5), (int)Math.Ceiling(height * 1.5), 144, 144, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(directory, "web-shortcuts-fill-144.png"));
            encoder.Save(output);
        }
        finally { window.Close(); }
    });

    private static BitmapSource Gradient(int width, int height, bool transparentLeftHalf = false)
    {
        var data = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                data[offset] = 40;
                data[offset + 1] = (byte)(20 + y * 200 / (height - 1));
                data[offset + 2] = (byte)(20 + x * 200 / (width - 1));
                data[offset + 3] = (byte)(transparentLeftHalf && x < width / 2 ? 0 : 255);
            }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, data, width * 4);
        source.Freeze();
        return source;
    }

    private static Color ExpectedGradient(int width, int height, double x, double y, int dpi)
    {
        var dpiScale = dpi / 96d;
        var dipX = (Math.Floor(x * dpiScale) + 0.5) / dpiScale;
        var dipY = (Math.Floor(y * dpiScale) + 0.5) / dpiScale;
        var fillScale = Math.Max(52d / width, 52d / height);
        var sourceX = Math.Clamp((dipX - 26) / fillScale + width / 2d - 0.5, 0, width - 1);
        var sourceY = Math.Clamp((dipY - 26) / fillScale + height / 2d - 0.5, 0, height - 1);
        return Color.FromRgb((byte)(20 + sourceX * 200 / (width - 1)), (byte)(20 + sourceY * 200 / (height - 1)), 40);
    }

    private static RenderTargetBitmap RenderIcon(Border icon, int dpi)
    {
        var visual = new DrawingVisual();
        VisualTreeHelper.SetRootDpi(visual, new DpiScale(dpi / 96d, dpi / 96d));
        using (var draw = visual.RenderOpen())
        {
            var brush = new VisualBrush(icon)
            {
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 52, 52),
                ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 52, 52), Stretch = Stretch.Fill,
            };
            draw.DrawRectangle(brush, null, new Rect(0, 0, 52, 52));
        }
        var size = (int)Math.Ceiling(52 * dpi / 96d);
        var output = new RenderTargetBitmap(size, size, dpi, dpi, PixelFormats.Pbgra32);
        output.Render(visual);
        return output;
    }

    private static Color Pixel(BitmapSource source, double xDip, double yDip, int dpi)
    {
        var x = Math.Clamp((int)Math.Floor(xDip * dpi / 96d), 0, source.PixelWidth - 1);
        var y = Math.Clamp((int)Math.Floor(yDip * dpi / 96d), 0, source.PixelHeight - 1);
        var bytes = new byte[4];
        source.CopyPixels(new Int32Rect(x, y, 1, 1), bytes, 4, 0);
        return Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
    }

    private static BitmapSource ExamplePicture(int index)
    {
        var width = index == 1 ? 240 : index == 2 ? 80 : 128;
        var height = index == 1 ? 80 : index == 2 ? 240 : 128;
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            if (index != 3)
            {
                var colors = new[] { Color.FromRgb(246, 119, 139), Color.FromRgb(44, 142, 173), Color.FromRgb(132, 109, 204) };
                draw.DrawRectangle(new SolidColorBrush(colors[index]), null, new Rect(0, 0, width, height));
                draw.DrawEllipse(new SolidColorBrush(Color.FromRgb(255, 221, 127)), null,
                    new Point(width / 2d + width / 10d, height / 2d - Math.Min(width, height) / 7d), Math.Min(width, height) / 4d, Math.Min(width, height) / 4d);
                var mountains = new StreamGeometry();
                using (var geometry = mountains.Open())
                {
                    geometry.BeginFigure(new Point(0, height), true, true);
                    geometry.LineTo(new Point(width / 2d, height / 2d - Math.Min(width, height) / 8d), true, false);
                    geometry.LineTo(new Point(width, height), true, false);
                }
                draw.DrawGeometry(new SolidColorBrush(Color.FromArgb(200, 49, 98, 114)), null, mountains);
            }
            else
            {
                var pen = new System.Windows.Media.Pen(new SolidColorBrush(Color.FromRgb(93, 209, 183)), 16);
                draw.DrawEllipse(null, pen, new Point(64, 64), 39, 39);
                draw.DrawEllipse(System.Windows.Media.Brushes.White, null, new Point(50, 61), 7, 10);
                draw.DrawEllipse(System.Windows.Media.Brushes.White, null, new Point(78, 61), 7, 10);
            }
        }
        var output = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        output.Render(visual);
        output.Freeze();
        return output;
    }

    private static WebShortcut Site(string id) => new(id, "示例网站", new Uri("https://example.com/" + id));
    private static Button Tile(QuotaPopoverWindow window, string id) => Assert.Single(Assert.IsType<WrapPanel>(window.FindName("ShortcutsGrid")).Children.OfType<Button>(), button => Equals(button.Tag, id));
    private static HwndSource DpiFixture() => new(new HwndSourceParameters("ShortcutFillDpiFixture") { Width = 0, Height = 0, WindowStyle = 0 });
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Offline manual image fill verification timed out.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
