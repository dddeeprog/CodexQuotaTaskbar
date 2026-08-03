using CodexQuotaTaskbar.Core.Overlay;

namespace CodexQuotaTaskbar.Core.Tests.Overlay;

public sealed class PopoverPlacementCalculatorTests
{
    [Theory]
    [InlineData(TaskbarEdge.Top, 96)]
    [InlineData(TaskbarEdge.Left, 96)]
    [InlineData(TaskbarEdge.Right, 96)]
    [InlineData(TaskbarEdge.Bottom, 96)]
    [InlineData(TaskbarEdge.Top, 144)]
    [InlineData(TaskbarEdge.Left, 144)]
    [InlineData(TaskbarEdge.Right, 144)]
    [InlineData(TaskbarEdge.Bottom, 144)]
    [InlineData(TaskbarEdge.Top, 192)]
    [InlineData(TaskbarEdge.Left, 192)]
    [InlineData(TaskbarEdge.Right, 192)]
    [InlineData(TaskbarEdge.Bottom, 192)]
    public void Popover_is_fully_clamped_to_monitor_for_every_edge(TaskbarEdge edge, int dpi)
    {
        var monitor = new ScreenRect(-1920, 0, 0, 1080);
        var taskbar = edge switch
        {
            TaskbarEdge.Top => new ScreenRect(-1920, 0, 0, 40),
            TaskbarEdge.Bottom => new ScreenRect(-1920, 1040, 0, 1080),
            TaskbarEdge.Left => new ScreenRect(-1920, 0, -1880, 1080),
            _ => new ScreenRect(-40, 0, 0, 1080),
        };
        var anchor = new TaskbarAnchor("secondary", monitor, taskbar, edge, dpi, false, true);
        var capsule = OverlayPlacementCalculator.Calculate(anchor, 190, 36, 12, 8);

        var result = PopoverPlacementCalculator.Calculate(anchor, capsule, 300, 240, 8);

        Assert.InRange(result.Left, monitor.Left, monitor.Right - result.Width);
        Assert.InRange(result.Top, monitor.Top, monitor.Bottom - result.Height);
        Assert.Equal((int)Math.Round(300 * dpi / 96d), result.Width);
        Assert.Equal((int)Math.Round(240 * dpi / 96d), result.Height);
    }

    [Theory]
    [InlineData(0, 42)]
    [InlineData(1702, 42)]
    [InlineData(0, 998)]
    [InlineData(1702, 998)]
    public void Freely_moved_island_keeps_details_inside_the_work_area(int left, int top)
    {
        var anchor = new TaskbarAnchor(
            "primary",
            new ScreenRect(0, 0, 1920, 1080),
            new ScreenRect(0, 1040, 1920, 1080),
            TaskbarEdge.Bottom,
            96,
            true,
            true);
        var capsule = new ScreenRect(left, top, left + 218, top + 42);

        var result = PopoverPlacementCalculator.Calculate(anchor, capsule, 328, 360, 8);

        Assert.InRange(result.Left, 0, 1920 - 328);
        Assert.InRange(result.Top, 0, 1040 - 360);
        Assert.InRange(result.Right, 328, 1920);
        Assert.InRange(result.Bottom, 360, 1040);
    }

    [Fact]
    public void Island_near_the_top_opens_details_below_it()
    {
        var anchor = new TaskbarAnchor(
            "primary",
            new ScreenRect(0, 0, 1920, 1080),
            new ScreenRect(0, 1040, 1920, 1080),
            TaskbarEdge.Bottom,
            96,
            true,
            true);
        var capsule = new ScreenRect(800, 4, 1018, 46);

        var result = PopoverPlacementCalculator.Calculate(anchor, capsule, 328, 360, 8);

        Assert.Equal(capsule.Bottom + 8, result.Top);
    }
}
