using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.UI;
using Size = System.Windows.Size;

namespace CodexQuotaTaskbar.Host.Tests.UI;

[Collection("Native UI isolation")]
public sealed class BongoIslandSurfaceTests
{
    [Fact]
    public void Model_and_existing_quota_controls_share_one_visual_tree_without_creating_a_window() => Sta(() =>
    {
        var quota = new Grid();
        var button = new Button { Content = "Offline example quota" };
        quota.Children.Add(button);
        var surface = new BongoIslandSurface(quota, 192, 168);
        surface.Present(SolidFrame());
        Arrange(surface);

        Assert.True(surface.OwnsQuotaBody);
        Assert.Same(surface, VisualTreeHelper.GetParent(quota));
        Assert.Same(quota, VisualTreeHelper.GetParent(button));
        Assert.Single(surface.Children.OfType<Grid>(), child => ReferenceEquals(child, quota));
        Assert.True(surface.HasModel);
        Assert.Null(Window.GetWindow(surface));
        Assert.Null(Window.GetWindow(button));
        Assert.Null(PresentationSource.FromVisual(surface));
        Assert.False(surface.IsLoaded);
    });

    [Theory]
    [InlineData(160, 96)] [InlineData(160, 144)] [InlineData(160, 192)]
    [InlineData(192, 96)] [InlineData(192, 144)] [InlineData(192, 192)]
    [InlineData(240, 96)] [InlineData(240, 144)] [InlineData(240, 192)]
    public void Size_and_dpi_variants_keep_model_centered_and_quota_below_the_tabletop(double size, int dpi) => Sta(() =>
    {
        var quota = new Grid { Background = Brushes.DarkSlateGray };
        var surface = new BongoIslandSurface(quota, size, 168);
        Arrange(surface);
        var origin = quota.TranslatePoint(default, surface);

        Assert.Equal(size, surface.ModelBoundsDip.Width);
        Assert.Equal(surface.Width / 2, surface.ModelBoundsDip.Left + surface.ModelBoundsDip.Width / 2, 6);
        Assert.True(surface.ModelBoundsDip.Top > 0);
        Assert.InRange(surface.ModelBoundsDip.Left, 0, surface.Width);
        Assert.InRange(surface.ModelBoundsDip.Right, 0, surface.Width);
        Assert.True(surface.ModelBoundsDip.Bottom <= origin.Y + .001);
        Assert.Equal(surface.StageHeightDip, origin.Y, 6);
        Assert.Equal(168, quota.ActualHeight, 6);
        Assert.Equal(surface.Width, quota.ActualWidth, 6);

        var bitmap = Snapshot(surface, dpi);
        Assert.Equal((int)Math.Ceiling(surface.Width * dpi / 96d), bitmap.PixelWidth);
        Assert.Equal((int)Math.Ceiling(surface.Height * dpi / 96d), bitmap.PixelHeight);
        Assert.Null(PresentationSource.FromVisual(surface));
    });

    [Theory]
    [InlineData(96)] [InlineData(144)] [InlineData(192)]
    public void Shared_card_clips_all_children_to_one_rounded_outer_edge(int dpi) => Sta(() =>
    {
        var quota = new Grid { Background = Brushes.White };
        var surface = new BongoIslandSurface(quota, 240, 168);
        surface.Present(SolidFrame());
        Arrange(surface);

        var clip = Assert.IsType<RectangleGeometry>(surface.Clip);
        Assert.Equal(new Rect(0, 0, surface.Width, surface.Height), clip.Rect);
        Assert.True(clip.RadiusX > 0);
        Assert.Equal(clip.RadiusX, clip.RadiusY);
        Assert.False(clip.FillContains(new Point(0, 0)));
        Assert.True(clip.FillContains(new Point(surface.Width / 2, 1)));

        var bitmap = Snapshot(surface, dpi);
        Assert.Equal(0, Alpha(bitmap, 0, 0));
        Assert.Equal(0, Alpha(bitmap, bitmap.PixelWidth - 1, 0));
        Assert.Equal(0, Alpha(bitmap, 0, bitmap.PixelHeight - 1));
        Assert.Equal(0, Alpha(bitmap, bitmap.PixelWidth - 1, bitmap.PixelHeight - 1));
        Assert.True(Alpha(bitmap, bitmap.PixelWidth / 2, bitmap.PixelHeight - 2) > 0);
    });

    [Fact]
    public void Clearing_a_frame_preserves_the_quota_and_shared_geometry() => Sta(() =>
    {
        var quota = new Grid();
        var surface = new BongoIslandSurface(quota, 192, 168);
        var height = surface.Height;
        Assert.False(surface.HasModel);
        surface.Present(SolidFrame());
        Assert.True(surface.HasModel);
        surface.Present(null);
        Assert.False(surface.HasModel);
        Assert.True(surface.OwnsQuotaBody);
        Assert.Same(surface, VisualTreeHelper.GetParent(quota));
        Assert.Equal(height, surface.Height);
    });

    [Theory]
    [InlineData(48)] [InlineData(82)] [InlineData(168)] [InlineData(194)]
    public void Task_body_sizes_keep_the_quotas_attached_and_preserve_bottom_anchoring(double bodyHeight) => Sta(() =>
    {
        var surface = new BongoIslandSurface(new Grid(), 192, bodyHeight);
        Arrange(surface);
        var work = new ScreenRect(0, 0, 1920, 1040);
        var panel = new ScreenRect(1600, 750, 1880, 1000);
        Assert.True(BongoIslandSurface.TryPlace(work, panel, 96, surface.Height, out var placed));
        Assert.Equal(984, placed.Bottom);
        Assert.Equal(surface.StageHeightDip + bodyHeight, surface.ActualHeight, 6);
        Assert.Equal(surface.Width, surface.ActualWidth, 6);
    });

    [Theory]
    [InlineData(96)] [InlineData(144)] [InlineData(192)]
    public void Interior_move_translates_the_complete_surface_as_one_rectangle(int dpi)
    {
        var work = new ScreenRect(-3000, -2000, 5000, 4000);
        var panel = new ScreenRect(-1000, -200, -500, 700);
        Assert.True(BongoIslandSurface.TryPlace(work, panel, dpi, 300, out var before));
        var moved = new ScreenRect(panel.Left + 120, panel.Top - 85, panel.Right + 120, panel.Bottom - 85);
        Assert.True(BongoIslandSurface.TryPlace(work, moved, dpi, 300, out var after));
        Assert.Equal(before.Width, after.Width);
        Assert.Equal(before.Height, after.Height);
        Assert.Equal(120, after.Left - before.Left);
        Assert.Equal(-85, after.Top - before.Top);
    }

    [Theory]
    [InlineData(-1900, -200)] [InlineData(-300, -200)]
    [InlineData(-1900, 950)] [InlineData(-300, 950)]
    public void Edge_requests_on_a_negative_coordinate_monitor_stay_entirely_in_work_area(int left, int top)
    {
        var work = new ScreenRect(-1920, 0, 0, 1040);
        var panel = new ScreenRect(left, top, left + 280, top + 200);
        Assert.True(BongoIslandSurface.TryPlace(work, panel, 144, 300, out var placed));
        Assert.InRange(placed.Left, work.Left, work.Right);
        Assert.InRange(placed.Right, work.Left, work.Right);
        Assert.InRange(placed.Top, work.Top, work.Bottom);
        Assert.InRange(placed.Bottom, work.Top, work.Bottom);
        Assert.True(placed.Width > 0 && placed.Height > 0);
    }

    [Fact]
    public void Insufficient_space_rejects_the_layout_instead_of_covering_or_truncating_content()
    {
        var panel = new ScreenRect(0, 0, 280, 200);
        Assert.False(BongoIslandSurface.TryPlace(new(0, 0, 200, 1000), panel, 96, 300, out var narrow));
        Assert.Equal(default, narrow);
        Assert.False(BongoIslandSurface.TryPlace(new(0, 0, 2000, 200), panel, 96, 300, out var shortArea));
        Assert.Equal(default, shortArea);
    }

    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    [InlineData(-1)] [InlineData(47)] [InlineData(1001)]
    public void Invalid_task_body_height_is_rejected_before_attaching_children(double height) => Sta(() =>
    {
        var body = new Grid();
        Assert.Throws<ArgumentOutOfRangeException>(() => new BongoIslandSurface(body, 192, height));
        Assert.Null(VisualTreeHelper.GetParent(body));
    });

    [Fact]
    public void Already_attached_quota_content_is_rejected_without_reparenting_it() => Sta(() =>
    {
        var parent = new Grid();
        var body = new Grid(); parent.Children.Add(body);
        Assert.Throws<ArgumentException>(() => new BongoIslandSurface(body, 192, 168));
        Assert.Same(parent, VisualTreeHelper.GetParent(body));
    });

    [Theory]
    [InlineData(double.NaN)] [InlineData(double.NegativeInfinity)] [InlineData(47)] [InlineData(1501)]
    public void Invalid_placement_heights_return_no_rectangle(double height)
    {
        Assert.False(BongoIslandSurface.TryPlace(new(0, 0, 1920, 1040), new(500, 500, 780, 700), 96, height, out var placed));
        Assert.Equal(default, placed);
    }

    [Theory]
    [InlineData(96, false)] [InlineData(96, true)]
    [InlineData(144, false)] [InlineData(144, true)]
    [InlineData(192, false)] [InlineData(192, true)]
    public void Bottom_docked_fused_card_uses_its_full_top_as_chat_anchor_without_bubble_overlap(int dpi, bool expanded) => Sta(() =>
    {
        var work = new ScreenRect(0, 0, 2560, 1400);
        var bodyHeight = QuotaCapsuleWindow.WindowHeight + QuotaCapsuleWindow.PanelSessionHeight(3, expanded) - 32;
        var surface = new BongoIslandSurface(new Grid(), 192, bodyHeight);
        var originalPanel = new ScreenRect(2100, 1100, 2380, 1384);
        Assert.True(BongoIslandSurface.TryPlace(work, originalPanel, dpi, surface.SurfaceHeightDip, out var card));
        var anchor = new DotAnchor(card.Left + card.Width / 2, card.Top, card.Bottom,
            work.Left, work.Top, work.Right, work.Bottom, dpi, true, 0);

        var bubble = DotChatWindow.CalculateBounds(anchor, 320, 260);

        AssertBubbleInWorkArea(work, bubble);
        Assert.True(bubble.Bottom <= card.Top,
            "A bottom-docked card has enough room for the existing native bubble above its complete model/quota surface.");
        Assert.False(bubble.IntersectsWith(new(card.Left, card.Top, card.Width, card.Height)));
    });

    [Theory]
    [InlineData(96, false)] [InlineData(96, true)]
    [InlineData(144, false)] [InlineData(144, true)]
    [InlineData(192, false)] [InlineData(192, true)]
    public void Top_edge_fused_card_supplies_full_bottom_so_chat_can_fall_below_the_entire_card(int dpi, bool expanded) => Sta(() =>
    {
        var work = new ScreenRect(-2560, 0, 0, 1400);
        var bodyHeight = QuotaCapsuleWindow.WindowHeight + QuotaCapsuleWindow.PanelSessionHeight(3, expanded) - 32;
        var surface = new BongoIslandSurface(new Grid(), 192, bodyHeight);
        var scale = dpi / 96d;
        var desiredBottom = (int)Math.Ceiling(surface.SurfaceHeightDip * scale) + (int)Math.Round(16 * scale) + 8;
        var originalPanel = new ScreenRect(-1000, desiredBottom - 200, -720, desiredBottom);
        Assert.True(BongoIslandSurface.TryPlace(work, originalPanel, dpi, surface.SurfaceHeightDip, out var card));
        Assert.Equal(8, card.Top);
        var anchor = new DotAnchor(card.Left + card.Width / 2, card.Top, card.Bottom,
            work.Left, work.Top, work.Right, work.Bottom, dpi, true, 0);

        var bubble = DotChatWindow.CalculateBounds(anchor, 320, 260);

        AssertBubbleInWorkArea(work, bubble);
        Assert.True(bubble.Top >= card.Bottom,
            "At the top edge, fallback placement must use the complete card bottom rather than a 56-DIP companion slot.");
        Assert.False(bubble.IntersectsWith(new(card.Left, card.Top, card.Width, card.Height)));
    });

    private static void AssertBubbleInWorkArea(ScreenRect work, System.Drawing.Rectangle bubble)
    {
        Assert.True(bubble.Width > 0 && bubble.Height > 0);
        Assert.InRange(bubble.Left, work.Left + 8, work.Right - 8);
        Assert.InRange(bubble.Right, work.Left + 8, work.Right - 8);
        Assert.InRange(bubble.Top, work.Top + 8, work.Bottom - 8);
        Assert.InRange(bubble.Bottom, work.Top + 8, work.Bottom - 8);
    }

    private static void Arrange(BongoIslandSurface surface)
    {
        surface.Measure(new Size(surface.Width, surface.Height));
        surface.Arrange(new Rect(0, 0, surface.Width, surface.Height));
        surface.UpdateLayout();
    }

    private static BitmapSource SolidFrame()
    {
        var data = new byte[] { 255, 255, 255, 255 };
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Pbgra32, null, data, 4);
        bitmap.Freeze(); return bitmap;
    }

    private static RenderTargetBitmap Snapshot(BongoIslandSurface surface, int dpi)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.Width * dpi / 96d),
            (int)Math.Ceiling(surface.Height * dpi / 96d), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(surface); return bitmap;
    }

    private static byte Alpha(BitmapSource bitmap, int x, int y)
    {
        var pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0); return pixel[3];
    }

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Fused surface offscreen verification timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
