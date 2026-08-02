using CodexQuotaTaskbar.Core.Overlay;

namespace CodexQuotaTaskbar.Core.Tests.Overlay;

public sealed class OverlayPlacementCalculatorTests
{
    [Theory]
    [InlineData(TaskbarEdge.Bottom, 1718, 996)]
    [InlineData(TaskbarEdge.Top, 1718, 48)]
    [InlineData(TaskbarEdge.Left, 48, 1032)]
    [InlineData(TaskbarEdge.Right, 1682, 1032)]
    public void Places_capsule_beside_each_taskbar_edge(TaskbarEdge edge, int expectedX, int expectedY)
    {
        var anchor = new TaskbarAnchor("screen", new ScreenRect(0, 0, 1920, 1080),
            edge switch
            {
                TaskbarEdge.Bottom => new ScreenRect(0, 1040, 1920, 1080),
                TaskbarEdge.Top => new ScreenRect(0, 0, 1920, 40),
                TaskbarEdge.Left => new ScreenRect(0, 0, 40, 1080),
                _ => new ScreenRect(1880, 0, 1920, 1080),
            }, edge, 96, true, true);

        var result = OverlayPlacementCalculator.Calculate(anchor, 190, 36, 12, 8);

        Assert.Equal(new ScreenRect(expectedX, expectedY, expectedX + 190, expectedY + 36), result);
    }

    [Fact]
    public void Preserves_negative_monitor_coordinates_and_scales_dips()
    {
        var anchor = new TaskbarAnchor("left", new ScreenRect(-2560, 0, 0, 1440),
            new ScreenRect(-2560, 1380, 0, 1440), TaskbarEdge.Bottom, 144, false, true);

        var result = OverlayPlacementCalculator.Calculate(anchor, 190, 36, 12, 8);

        Assert.Equal(-303, result.Left);
        Assert.Equal(1314, result.Top);
        Assert.Equal(285, result.Width);
        Assert.Equal(54, result.Height);
    }
}
