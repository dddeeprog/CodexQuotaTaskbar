using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class FloatingSurfaceLayoutTests
{
    [Fact]
    public void Full_surface_prefers_the_right_then_the_left_without_covering_the_island()
    {
        var work = new ScreenRect(0, 0, 1600, 1000);
        var island = new ScreenRect(650, 600, 900, 900);
        Assert.True(FloatingSurfaceLayout.TryPlace(work, island, 320, 300, [], out var right));
        Assert.True(right.Left >= island.Right + 8);
        var reservedRightSide = new ScreenRect(island.Right + 8, 8, 1592, 992);
        Assert.True(FloatingSurfaceLayout.TryPlace(work, island, 320, 300, [reservedRightSide], out var left));
        Assert.True(left.Right <= island.Left - 8);
        Safe(work, island, [reservedRightSide], left);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(1350, 0)] [InlineData(0, 700)] [InlineData(1350, 700)]
    public void All_four_edges_keep_the_entire_surface_clickable(int x, int y)
    {
        var work = new ScreenRect(0, 0, 1600, 1000);
        var island = new ScreenRect(x, y, x + 250, y + 300);
        Assert.True(FloatingSurfaceLayout.TryPlace(work, island, 320, 380, [], out var placed));
        Assert.Equal(320, placed.Width); Assert.Equal(380, placed.Height);
        Safe(work, island, [], placed);
    }

    [Theory]
    [InlineData(96)] [InlineData(144)] [InlineData(192)]
    public void Negative_coordinate_monitors_and_dpi_scaled_surfaces_do_not_cross_the_work_area(int dpi)
    {
        var work = new ScreenRect(-2560, -300, 0, 1140);
        var island = new ScreenRect(-400, 400, -152, 730);
        var width = 320 * dpi / 96; var height = 300 * dpi / 96;
        Assert.True(FloatingSurfaceLayout.TryPlace(work, island, width, height, [], out var placed,
            8 * dpi / 96, 8 * dpi / 96));
        Safe(work, island, [], placed, 8 * dpi / 96);
    }

    [Fact]
    public void Additional_surfaces_are_reserved_and_cannot_be_covered()
    {
        var work = new ScreenRect(0, 0, 1920, 1040);
        var island = new ScreenRect(1500, 700, 1750, 1000);
        Assert.True(FloatingSurfaceLayout.TryFindLane(work, island, 320, 240, 240, [], out var lane));
        Assert.True(FloatingSurfaceLayout.TryPlace(work, island, 360, 500, [lane], out var details));
        Assert.True(FloatingSurfaceLayout.TryPlace(work, island, 240, 350, [lane, details], out var menu));
        Safe(work, island, [details, menu], lane);
        Safe(work, island, [lane, menu], details);
        Safe(work, island, [lane, details], menu);
    }

    [Fact]
    public void A_scrollable_lane_can_fit_when_the_original_tall_surface_cannot()
    {
        var work = new ScreenRect(0, 0, 800, 600);
        var island = new ScreenRect(400, 0, 650, 600);
        Assert.False(FloatingSurfaceLayout.TryPlace(work, island, 320, 700, [], out _));
        Assert.True(FloatingSurfaceLayout.TryFindLane(work, island, 320, 240, 240, [], out var lane));
        Assert.InRange(lane.Width, 240, 320); Assert.True(lane.Height >= 240);
        Safe(work, island, [], lane);
    }

    [Fact]
    public void Insufficient_space_returns_failure_instead_of_clamping_over_the_card()
    {
        var work = new ScreenRect(0, 0, 300, 200);
        var island = new ScreenRect(8, 8, 292, 192);
        Assert.False(FloatingSurfaceLayout.TryPlace(work, island, 240, 170, [], out var placed));
        Assert.Equal(default, placed);
        Assert.False(FloatingSurfaceLayout.TryFindLane(work, island, 320, 240, 170, [], out var lane));
        Assert.Equal(default, lane);
    }

    [Fact]
    public void Corrupt_geometry_and_unbounded_obstacle_sets_fail_closed()
    {
        var work = new ScreenRect(0, 0, 1600, 1000); var island = new ScreenRect(600, 400, 850, 700);
        Assert.False(FloatingSurfaceLayout.TryPlace(work, island, 0, 100, [], out _));
        Assert.False(FloatingSurfaceLayout.TryPlace(work, default, 100, 100, [], out _));
        Assert.False(FloatingSurfaceLayout.TryPlace(work, island, 100, 100, new ScreenRect[9], out _));
        Assert.False(FloatingSurfaceLayout.TryFindLane(work, island, 100, 200, 100, [], out _));
        Assert.False(FloatingSurfaceLayout.TryFindLane(work, island, 320, 240, 170, [default], out _));
    }

    [Fact]
    public void Seeded_layouts_never_report_a_collision_or_an_offscreen_success()
    {
        var random = new Random(731);
        for (var i = 0; i < 150; i++)
        {
            var work = new ScreenRect(-1800, -200, 200, 1100);
            var x = random.Next(-1800, -100); var y = random.Next(-200, 700);
            var island = new ScreenRect(x, y, x + 250, y + 300);
            var obstacles = new[] { new ScreenRect(-1700, 50, -1300, 450), new ScreenRect(-800, 500, -450, 1000) };
            if (FloatingSurfaceLayout.TryPlace(work, island, 320, 380, obstacles, out var surface))
                Safe(work, island, obstacles, surface);
            if (FloatingSurfaceLayout.TryFindLane(work, island, 320, 240, 200, obstacles, out var lane))
                Safe(work, island, obstacles, lane);
        }
    }

    private static void Safe(ScreenRect work, ScreenRect island, IReadOnlyList<ScreenRect> obstacles,
        ScreenRect actual, int margin = 8)
    {
        Assert.InRange(actual.Left, work.Left + margin, work.Right - margin);
        Assert.InRange(actual.Right, work.Left + margin, work.Right - margin);
        Assert.InRange(actual.Top, work.Top + margin, work.Bottom - margin);
        Assert.InRange(actual.Bottom, work.Top + margin, work.Bottom - margin);
        Assert.False(FloatingSurfaceLayout.Intersects(actual, island));
        Assert.All(obstacles, obstacle => Assert.False(FloatingSurfaceLayout.Intersects(actual, obstacle)));
    }
}
