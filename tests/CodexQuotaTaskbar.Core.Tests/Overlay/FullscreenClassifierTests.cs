using CodexQuotaTaskbar.Core.Overlay;

namespace CodexQuotaTaskbar.Core.Tests.Overlay;

public sealed class FullscreenClassifierTests
{
    [Fact]
    public void Full_monitor_bounds_are_fullscreen_but_work_area_maximized_is_not()
    {
        var monitor = new ScreenRect(0, 0, 1920, 1080);

        Assert.True(FullscreenClassifier.IsTrueFullscreen(monitor, new ScreenRect(0, 0, 1920, 1080), false));
        Assert.False(FullscreenClassifier.IsTrueFullscreen(monitor, new ScreenRect(0, 0, 1920, 1040), false));
        Assert.False(FullscreenClassifier.IsTrueFullscreen(monitor, monitor, true));
    }
}
