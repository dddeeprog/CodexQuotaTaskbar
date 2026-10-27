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

    [Theory]
    [InlineData(-2, true)]
    [InlineData(2, true)]
    [InlineData(-3, false)]
    [InlineData(3, false)]
    public void Two_pixel_tolerance_applies_to_each_edge_on_a_negative_coordinate_monitor(int offset, bool expected)
    {
        var monitor = new ScreenRect(-2560, -1440, 0, 0);
        foreach (var frame in new[]
        {
            monitor with { Left = monitor.Left + offset },
            monitor with { Top = monitor.Top + offset },
            monitor with { Right = monitor.Right + offset },
            monitor with { Bottom = monitor.Bottom + offset },
        }) Assert.Equal(expected, FullscreenClassifier.IsTrueFullscreen(monitor, frame, false));
    }

    [Fact]
    public void Matching_another_display_or_a_work_area_does_not_match_this_monitor()
    {
        var monitor = new ScreenRect(-1920, 0, 0, 1080);
        Assert.False(FullscreenClassifier.IsTrueFullscreen(monitor, new(0, 0, 1920, 1080), false));
        Assert.False(FullscreenClassifier.IsTrueFullscreen(monitor, monitor with { Bottom = 1040 }, false));
        Assert.False(FullscreenClassifier.IsTrueFullscreen(monitor, monitor, true));
    }

    [Fact]
    public void Unknown_full_monitor_window_remains_fullscreen_until_capture_identity_is_evidenced()
    {
        // Geometry alone cannot distinguish a game/video from a capture overlay.
        // This is a safety baseline, NOT a test of a capture exemption or QQ UI.
        var monitor = new ScreenRect(0, 0, 2560, 1440);
        Assert.True(FullscreenClassifier.IsTrueFullscreen(monitor, monitor, false));
    }
}
