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
    public void Open_details_follow_the_island_by_the_same_physical_delta()
    {
        var details = new ScreenRect(900, 500, 1228, 780);
        var previousIsland = new ScreenRect(1100, 800, 1318, 842);
        var movedIsland = new ScreenRect(1020, 760, 1238, 802);

        var movedDetails = OverlayCoordinator.CalculateFollowerBounds(details, previousIsland, movedIsland);

        Assert.Equal(new ScreenRect(820, 460, 1148, 740), movedDetails);
        Assert.Equal(details.Width, movedDetails.Width);
        Assert.Equal(details.Height, movedDetails.Height);
    }
}
