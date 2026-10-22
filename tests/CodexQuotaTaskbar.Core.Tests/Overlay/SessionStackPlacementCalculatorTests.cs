using CodexQuotaTaskbar.Core.Overlay;

namespace CodexQuotaTaskbar.Core.Tests.Overlay;

public sealed class SessionStackPlacementCalculatorTests
{
    private static readonly TaskbarAnchor Anchor = new(
        "primary",
        new ScreenRect(0, 0, 1920, 1080),
        new ScreenRect(0, 1040, 1920, 1080),
        TaskbarEdge.Bottom,
        96,
        true,
        true);

    [Fact]
    public void Keeps_the_session_stack_below_the_island_and_shifts_the_pair_inside_the_work_area()
    {
        var island = new ScreenRect(807, 960, 1113, 1040);

        var result = SessionStackPlacementCalculator.CalculateBelow(Anchor, island, 392, 256, -16);

        Assert.Equal(new ScreenRect(807, 720, 1113, 800), result.Capsule);
        Assert.Equal(new ScreenRect(764, 784, 1156, 1040), result.Stack);
    }

    [Fact]
    public void Leaves_an_island_unchanged_when_the_stack_already_fits_below()
    {
        var island = new ScreenRect(807, 0, 1113, 80);

        var result = SessionStackPlacementCalculator.CalculateBelow(Anchor, island, 392, 256, -16);

        Assert.Equal(island, result.Capsule);
        Assert.Equal(64, result.Stack.Top);
        Assert.Equal(764, result.Stack.Left);
    }

    [Fact]
    public void Clamps_the_wider_stack_at_the_screen_edge()
    {
        var island = new ScreenRect(0, 500, 306, 580);

        var result = SessionStackPlacementCalculator.CalculateBelow(Anchor, island, 392, 256, -16);

        Assert.Equal(0, result.Stack.Left);
        Assert.Equal(43, result.Capsule.Left);
        Assert.InRange(result.Stack.Top, 0, 1040 - 256);
    }

    [Theory]
    [InlineData(96, -150, 320)]
    [InlineData(96, 1850, 320)]
    [InlineData(108, -150, 320)]
    [InlineData(108, 1850, 320)]
    [InlineData(144, -150, 320)]
    [InlineData(144, 1850, 320)]
    [InlineData(96, 1850, 250)]
    public void Edge_correction_moves_both_windows_without_breaking_their_center_alignment(int dpi, int left, double stackWidth)
    {
        var scale = dpi / 96d;
        var width = (int)Math.Round(306 * scale);
        var height = (int)Math.Round(112 * scale);
        var island = new ScreenRect(left, 500, left + width, 500 + height);
        var result = SessionStackPlacementCalculator.CalculateBelow(Anchor with { Dpi = dpi }, island, stackWidth, 256, -16);

        Assert.InRange(result.Capsule.Left, 0, 1920 - width);
        Assert.InRange(result.Stack.Left, 0, 1920 - result.Stack.Width);
        Assert.InRange(Math.Abs((result.Capsule.Left + result.Capsule.Right) - (result.Stack.Left + result.Stack.Right)), 0, 1);
        Assert.Equal(result.Capsule.Bottom + (int)Math.Round(-16 * scale), result.Stack.Top);
        Assert.True(result.Stack.Bottom <= 1040);
        Assert.Equal(result, SessionStackPlacementCalculator.CalculateBelow(Anchor with { Dpi = dpi }, result.Capsule, stackWidth, 256, -16));
    }

    [Theory]
    [InlineData(TaskbarEdge.Left)]
    [InlineData(TaskbarEdge.Right)]
    public void Side_taskbars_on_negative_coordinate_monitor_leave_the_pair_inside_the_work_area(TaskbarEdge edge)
    {
        var taskbar = edge == TaskbarEdge.Left
            ? new ScreenRect(-1920, 0, -1880, 1080)
            : new ScreenRect(-40, 0, 0, 1080);
        var anchor = Anchor with { MonitorBounds = new(-1920, 0, 0, 1080), TaskbarBounds = taskbar, Edge = edge };
        var island = edge == TaskbarEdge.Left ? new ScreenRect(-2000, 500, -1694, 612) : new ScreenRect(-100, 500, 206, 612);
        var result = SessionStackPlacementCalculator.CalculateBelow(anchor, island, 320, 256, -16);

        Assert.True(result.Stack.Left >= (edge == TaskbarEdge.Left ? -1880 : -1920));
        Assert.True(result.Stack.Right <= (edge == TaskbarEdge.Right ? -40 : 0));
        Assert.Equal(result.Capsule.Left + result.Capsule.Right, result.Stack.Left + result.Stack.Right);
    }
}
