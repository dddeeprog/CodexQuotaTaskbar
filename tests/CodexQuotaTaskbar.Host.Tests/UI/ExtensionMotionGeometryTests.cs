using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Host.Overlay;

namespace CodexQuotaTaskbar.Host.Tests.UI;

// Pure physical geometry: no HWND, pointer, account, network or input listener.
public sealed class ExtensionMotionGeometryTests
{
    [Theory]
    [InlineData(96)] [InlineData(120)] [InlineData(144)] [InlineData(192)]
    public void Both_extensions_change_only_their_own_edge_while_the_middle_fits(int dpi)
    {
        var scale = dpi / 96d;
        var work = new ScreenRect(-2400, -1200, 0, 1200);
        var core = new ScreenRect(-1800, -400, -1800 + Pixels(280, scale), -400 + Pixels(319, scale));
        foreach (var top in new[] { 0d, 2d, 18.5d, 36d, 37d })
        foreach (var footer in new[] { 0d, 1d, 13d, 25d, 26d })
        {
            var topPixels = Pixels(top, scale);
            var footerPixels = Pixels(footer, scale);
            var result = OverlayCoordinator.CalculateExtensionBounds(work, core, topPixels, footerPixels);
            Assert.Equal(core.Left, result.Left);
            Assert.Equal(core.Right, result.Right);
            Assert.Equal(core.Top, result.Top + topPixels);
            Assert.Equal(core.Bottom, result.Bottom - footerPixels);
            Assert.Equal(core.Height + topPixels + footerPixels, result.Height);
            // Reconciliation must use the unchanged middle, not the previous expanded rectangle.
            Assert.Equal(result, OverlayCoordinator.CalculateExtensionBounds(work, core, topPixels, footerPixels));
        }
        Assert.Equal(core, OverlayCoordinator.CalculateExtensionBounds(work, core, 0, 0));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1080)]
    public void At_top_edge_only_the_required_amount_of_middle_translation_is_added(int workTop)
    {
        var work = new ScreenRect(-1920, workTop, 0, workTop + 900);
        var core = new ScreenRect(-1500, workTop + 8, -1220, workTop + 327);
        var result = OverlayCoordinator.CalculateExtensionBounds(work, core, 37, 26);
        Assert.Equal(workTop, result.Top);
        Assert.Equal(29, (result.Top + 37) - core.Top);
        Assert.Equal(core.Height + 63, result.Height);
        Assert.Equal(core.Bottom + 29, result.Bottom - 26);
        Assert.Equal(core, OverlayCoordinator.CalculateExtensionBounds(work, core, 0, 0));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1080)]
    public void At_bottom_edge_footer_grows_down_until_space_ends_then_middle_moves_only_as_needed(int workTop)
    {
        var work = new ScreenRect(-1920, workTop, 0, workTop + 900);
        var core = new ScreenRect(-1500, work.Bottom - 327, -1220, work.Bottom - 8);
        var result = OverlayCoordinator.CalculateExtensionBounds(work, core, 37, 26);
        Assert.Equal(work.Bottom, result.Bottom);
        Assert.Equal(-18, (result.Bottom - 26) - core.Bottom);
        Assert.Equal(core.Height + 63, result.Height);
        Assert.Equal(core.Top - 18, result.Top + 37);
        Assert.Equal(core, OverlayCoordinator.CalculateExtensionBounds(work, core, 0, 0));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Pixel_by_pixel_edge_expansion_has_no_extra_anchor_jump(bool bottomEdge)
    {
        var work = new ScreenRect(0, 0, 1920, 1080);
        var core = bottomEdge ? new ScreenRect(500, 753, 780, 1072) : new ScreenRect(500, 8, 780, 327);
        var previous = OverlayCoordinator.CalculateExtensionBounds(work, core, 0, 0);
        var previousCoreTop = core.Top;
        for (var pixel = 1; pixel <= 37; pixel++)
        {
            var footer = Math.Min(pixel, 26);
            var result = OverlayCoordinator.CalculateExtensionBounds(work, core, pixel, footer);
            Assert.InRange(result.Top, work.Top, work.Bottom);
            Assert.InRange(result.Bottom, work.Top, work.Bottom);
            Assert.InRange(Math.Abs(result.Top - previous.Top), 0, 2);
            Assert.InRange(Math.Abs(result.Bottom - previous.Bottom), 0, 2);
            var coreTop = result.Top + pixel;
            Assert.InRange(Math.Abs(coreTop - previousCoreTop), 0, 1);
            previous = result;
            previousCoreTop = coreTop;
        }
    }

    [Fact]
    public void Classic_task_tail_and_both_extensions_are_reserved_together()
    {
        var work = new ScreenRect(0, 0, 1000, 800);
        var core = new ScreenRect(500, 600, 806, 712);
        var result = OverlayCoordinator.CalculateExtensionBounds(work, core, 37, 26, 120);
        Assert.Equal(680, result.Bottom);
        Assert.Equal(120, work.Bottom - result.Bottom);
        Assert.Equal(core.Height + 37 + 26, result.Height);
        Assert.Equal(542, result.Top + 37);
    }

    [Fact]
    public void Exact_fit_and_horizontal_edges_are_clamped_without_changing_width_or_height()
    {
        var work = new ScreenRect(-800, -600, 0, 0);
        var core = new ScreenRect(-900, -500, -620, -100);
        var result = OverlayCoordinator.CalculateExtensionBounds(work, core, 150, 50);
        Assert.Equal(new ScreenRect(-800, -600, -520, 0), result);
        var right = OverlayCoordinator.CalculateExtensionBounds(work, core with { Left = -100, Right = 180 }, 150, 50);
        Assert.Equal(new ScreenRect(-280, -600, 0, 0), right);
    }

    [Theory]
    [InlineData(-1, 0, 0)] [InlineData(0, -1, 0)] [InlineData(0, 0, -1)]
    [InlineData(37, 26, 0)] [InlineData(int.MaxValue, 0, 0)]
    public void Impossible_or_negative_extensions_are_rejected_instead_of_silently_cropping(int top, int footer, int tail)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OverlayCoordinator.CalculateExtensionBounds(
            new(0, 0, 280, 340), new(0, 0, 280, 319), top, footer, tail));
    }

    [Fact]
    public void Legacy_top_only_geometry_remains_identical_to_the_new_footer_zero_case()
    {
        var work = new ScreenRect(-1920, -1080, 0, 0);
        var core = new ScreenRect(-1500, -1000, -1194, -888);
        foreach (var top in new[] { 0, 1, 37, 129, 233 })
        Assert.Equal(OverlayCoordinator.CalculateTopExtensionBounds(work, core, top, 120),
            OverlayCoordinator.CalculateExtensionBounds(work, core, top, 0, 120));
    }

    private static int Pixels(double dip, double scale) => (int)Math.Round(dip * scale);
}
