using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Host.Overlay;
using CodexQuotaTaskbar.Host.UI;
using System.Windows.Media.Animation;

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
        var original = new ScreenRect(100, 200, 406, 280);

        var moved = QuotaCapsuleWindow.TranslateBounds(original, 25, -30);

        Assert.Equal(new ScreenRect(125, 170, 431, 250), moved);
    }

    [Fact]
    public void Badge_shadow_has_more_safety_space_than_its_blur_and_depth()
    {
        Assert.Equal(306, QuotaCapsuleWindow.WindowWidth);
        Assert.Equal(92, QuotaCapsuleWindow.WindowHeight);
        Assert.True(QuotaCapsuleWindow.BadgeShadowSafeInset >= 8 + 2);
    }

    [Fact]
    public void Island_blurs_the_capture_layer_behind_its_tint()
    {
        Assert.Equal("BackdropLayer", QuotaCapsuleWindow.GlassCaptureTargetName);
        Assert.Equal(SessionStackWindow.SessionGlassBlurRadius, QuotaCapsuleWindow.GlassBlurRadius);
        Assert.Equal(SessionStackWindow.SessionGlassMerging, QuotaCapsuleWindow.GlassMerging);
    }

    [Theory]
    [InlineData(1, 2, 3)]
    [InlineData(2, 1, -3)]
    [InlineData(2, 2, 0)]
    public void Badge_numbers_slide_in_the_direction_of_the_count_change(int previous, int current, double expected)
    {
        Assert.Equal(expected, QuotaCapsuleWindow.NumberTransitionOffset(previous, current));
        Assert.Equal(180, QuotaCapsuleWindow.BadgeAnimationMilliseconds);
        Assert.Equal(EasingMode.EaseOut, QuotaCapsuleWindow.ExitEasingMode);
    }

    [Fact]
    public void Details_use_the_monitor_containing_a_freely_moved_island()
    {
        var primary = new TaskbarAnchor("primary", new ScreenRect(0, 0, 1920, 1080), new ScreenRect(0, 1040, 1920, 1080), TaskbarEdge.Bottom, 96, true, true);
        var secondary = new TaskbarAnchor("secondary", new ScreenRect(1920, 0, 3840, 1080), new ScreenRect(1920, 1040, 3840, 1080), TaskbarEdge.Bottom, 144, false, true);
        var movedIsland = new ScreenRect(2200, 300, 2506, 380);

        var selected = OverlayCoordinator.SelectAnchor([primary, secondary], primary.MonitorId, movedIsland);

        Assert.Same(secondary, selected);
    }

    [Fact]
    public void Detail_avoidance_uses_the_full_island_and_session_stack_bounds()
    {
        var island = new ScreenRect(788, 288, 1094, 368);
        var sessions = new ScreenRect(745, 352, 1137, 608);

        var result = OverlayCoordinator.Union(island, sessions);

        Assert.Equal(new ScreenRect(745, 288, 1137, 608), result);
    }

    [Fact]
    public void Session_placement_reserves_expanded_height_so_the_first_card_stays_anchored()
    {
        Assert.Equal(256, OverlayCoordinator.ReservedSessionHeight(8));

        var reserved = new ScreenRect(745, 352, 1137, 608);
        var collapsed = OverlayCoordinator.ResizeStackHeight(reserved, 130, 96);
        var expanded = OverlayCoordinator.ResizeStackHeight(reserved, 256, 96);

        Assert.Equal(reserved.Top, collapsed.Top);
        Assert.Equal(reserved.Top, expanded.Top);
        Assert.Equal(482, collapsed.Bottom);
        Assert.Equal(608, expanded.Bottom);
    }
}
