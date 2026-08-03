using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Host.Overlay;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class QuotaCapsuleWindowTests
{
    [Theory]
    [InlineData(3, 3, false)]
    [InlineData(4, 0, true)]
    [InlineData(0, -4, true)]
    public void Applies_a_small_drag_threshold(int deltaX, int deltaY, bool expected)
    {
        Assert.Equal(expected, QuotaCapsuleWindow.ExceedsDragThreshold(deltaX, deltaY));
    }

    [Fact]
    public void Translates_physical_bounds_without_resizing_the_island()
    {
        var original = new ScreenRect(100, 200, 318, 242);

        var moved = QuotaCapsuleWindow.TranslateBounds(original, 25, -30);

        Assert.Equal(new ScreenRect(125, 170, 343, 212), moved);
    }

    [Fact]
    public void Details_use_the_monitor_containing_a_freely_moved_island()
    {
        var primary = new TaskbarAnchor("primary", new ScreenRect(0, 0, 1920, 1080), new ScreenRect(0, 1040, 1920, 1080), TaskbarEdge.Bottom, 96, true, true);
        var secondary = new TaskbarAnchor("secondary", new ScreenRect(1920, 0, 3840, 1080), new ScreenRect(1920, 1040, 3840, 1080), TaskbarEdge.Bottom, 144, false, true);
        var movedIsland = new ScreenRect(2200, 300, 2418, 342);

        var selected = OverlayCoordinator.SelectAnchor([primary, secondary], primary.MonitorId, movedIsland);

        Assert.Same(secondary, selected);
    }
}
