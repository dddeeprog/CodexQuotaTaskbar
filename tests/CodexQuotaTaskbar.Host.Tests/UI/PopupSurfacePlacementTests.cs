using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class PopupSurfacePlacementTests
{
    [Theory]
    [InlineData(10)] [InlineData(16)]
    public void Visible_gap_is_six_with_unchanged_outer_size(int inset)
    {
        var work = new ScreenRect(0, 0, 1600, 1000);
        var island = new ScreenRect(600, 400, 850, 700);
        Assert.True(PopupSurfacePlacement.TryPlace(work, island, 320, 380, inset, [], out var actual));
        Assert.Equal(320, actual.Width); Assert.Equal(380, actual.Height);
        Assert.Equal(island.Right + 6, Visible(actual, inset).Left);
        Assert.True(actual.Left < island.Right + 6);
        Safe(work, island, actual, inset);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void All_four_directions_use_the_visible_edge(int direction)
    {
        var work = direction switch
        {
            0 => new ScreenRect(0, 0, 1500, 900),
            1 => new ScreenRect(0, 0, 750, 900),
            _ => new ScreenRect(0, 0, 750, 1200),
        };
        var island = direction switch
        {
            0 => new ScreenRect(500, 300, 750, 600),
            1 => new ScreenRect(500, 300, 750, 600),
            2 => new ScreenRect(200, 0, 550, 500),
            _ => new ScreenRect(200, 700, 550, 1200),
        };
        Assert.True(PopupSurfacePlacement.TryPlace(work, island, 400, 340, 16, [], out var actual));
        var visible = Visible(actual, 16);
        var gap = direction switch
        {
            0 => visible.Left - island.Right,
            1 => island.Left - visible.Right,
            2 => visible.Top - island.Bottom,
            _ => island.Top - visible.Bottom,
        };
        Assert.Equal(6, gap);
        Safe(work, island, actual, 16);
    }

    [Theory]
    [InlineData(96)] [InlineData(120)] [InlineData(144)] [InlineData(192)]
    public void Dpi_and_negative_monitor_coordinates_keep_the_scaled_gap(int dpi)
    {
        var scale = dpi / 96d;
        var inset = (int)Math.Ceiling(16 * scale); var gap = (int)Math.Ceiling(6 * scale);
        var margin = (int)Math.Ceiling(8 * scale);
        var work = new ScreenRect(-3000, -500, 0, 1500);
        var island = new ScreenRect(-2100, 200, -1800, 600);
        var width = (int)Math.Ceiling(364 * scale); var height = (int)Math.Ceiling(420 * scale);
        Assert.True(PopupSurfacePlacement.TryPlace(work, island, width, height, inset, [], out var actual, gap, margin));
        Assert.Equal(gap, Visible(actual, inset).Left - island.Right);
        Assert.Equal(width, actual.Width); Assert.Equal(height, actual.Height);
        Safe(work, island, actual, inset, margin);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(1350, 0)] [InlineData(0, 700)] [InlineData(1350, 700)]
    public void Work_area_corners_preserve_the_entire_window(int x, int y)
    {
        var work = new ScreenRect(0, 0, 1600, 1000);
        var island = new ScreenRect(x, y, x + 250, y + 300);
        Assert.True(PopupSurfacePlacement.TryPlace(work, island, 320, 380, 10, [], out var actual));
        Safe(work, island, actual, 10);
    }

    [Fact]
    public void Other_surfaces_exclude_even_the_transparent_outer_window()
    {
        var work = new ScreenRect(0, 0, 1600, 1000);
        var island = new ScreenRect(650, 400, 900, 700);
        var other = new ScreenRect(906, 8, 1592, 992);
        Assert.True(PopupSurfacePlacement.TryPlace(work, island, 320, 380, 16, [other], out var actual));
        Assert.False(FloatingSurfaceLayout.Intersects(actual, other));
        Assert.True(Visible(actual, 16).Right <= island.Left - 6);
        Safe(work, island, actual, 16);
    }

    [Fact]
    public void Scroll_lane_keeps_the_exact_outer_width_and_a_safe_height()
    {
        var work = new ScreenRect(0, 0, 800, 600);
        var island = new ScreenRect(400, 0, 650, 600);
        Assert.False(PopupSurfacePlacement.TryPlace(work, island, 320, 700, 16, [], out _));
        Assert.True(PopupSurfacePlacement.TryFindLane(work, island, 320, 160, 16, [], out var lane));
        Assert.Equal(320, lane.Width); Assert.True(lane.Height >= 160);
        Safe(work, island, lane, 16);
        Assert.Equal(6, island.Left - Visible(lane, 16).Right);
    }

    [Fact]
    public void Drag_and_repeat_placement_are_deterministic()
    {
        var work = new ScreenRect(0, 0, 1920, 1080);
        for (var x = 500; x <= 620; x += 4)
        {
            var island = new ScreenRect(x, 500, x + 250, 800);
            Assert.True(PopupSurfacePlacement.TryPlace(work, island, 272, 300, 10, [], out var first));
            Assert.True(PopupSurfacePlacement.TryPlace(work, island, 272, 300, 10, [], out var second));
            Assert.Equal(first, second);
            Assert.Equal(6, Visible(first, 10).Left - island.Right);
            Safe(work, island, first, 10);
        }
    }

    [Fact]
    public void Invalid_insets_sizes_obstacles_and_insufficient_work_area_fail_closed()
    {
        var work = new ScreenRect(0, 0, 800, 600); var island = new ScreenRect(400, 200, 650, 500);
        Assert.False(PopupSurfacePlacement.TryPlace(work, island, 320, 300, -1, [], out _));
        Assert.False(PopupSurfacePlacement.TryPlace(work, island, 20, 300, 10, [], out _));
        Assert.False(PopupSurfacePlacement.TryPlace(work, island, 320, 20, 10, [], out _));
        Assert.False(PopupSurfacePlacement.TryPlace(work, default, 320, 300, 10, [], out _));
        Assert.False(PopupSurfacePlacement.TryPlace(work, island, 320, 300, 10, [default], out _));
        Assert.False(PopupSurfacePlacement.TryPlace(work, island, 320, 300, 10, new ScreenRect[9], out _));
        Assert.False(PopupSurfacePlacement.TryPlace(new(0, 0, 19, 19), island, 320, 300, 10, [], out _));
        Assert.False(PopupSurfacePlacement.TryFindLane(work, island, 320, 20, 10, [], out _));
    }

    private static ScreenRect Visible(ScreenRect bounds, int inset) =>
        new(bounds.Left + inset, bounds.Top + inset, bounds.Right - inset, bounds.Bottom - inset);

    private static void Safe(ScreenRect work, ScreenRect island, ScreenRect outer, int inset, int margin = 8)
    {
        Assert.InRange(outer.Left, work.Left + margin, work.Right - margin);
        Assert.InRange(outer.Right, work.Left + margin, work.Right - margin);
        Assert.InRange(outer.Top, work.Top + margin, work.Bottom - margin);
        Assert.InRange(outer.Bottom, work.Top + margin, work.Bottom - margin);
        Assert.False(FloatingSurfaceLayout.Intersects(Visible(outer, inset), island));
    }
}
