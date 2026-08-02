using CodexQuotaTaskbar.Core.Overlay;
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
}
