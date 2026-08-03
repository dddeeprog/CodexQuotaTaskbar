using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class SessionStackWindowTests
{
    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(5, false, 1)]
    [InlineData(2, true, 2)]
    [InlineData(3, true, 3)]
    [InlineData(8, true, 3)]
    public void Expanded_stack_never_grows_beyond_three_visible_sessions(int total, bool expanded, int expected)
    {
        Assert.Equal(expected, SessionStackWindow.VisibleSessionCount(total, expanded));
    }

    [Fact]
    public void Expanded_stack_height_is_capped_at_three_sessions()
    {
        Assert.Equal(SessionStackWindow.CalculateHeight(3, true), SessionStackWindow.CalculateHeight(20, true));
        Assert.Equal(256, SessionStackWindow.CalculateHeight(20, true));
    }

    [Fact]
    public void Collapsed_stack_keeps_extra_depth_to_reveal_layered_sessions()
    {
        Assert.Equal(130, SessionStackWindow.CalculateHeight(4, false));
        Assert.Equal(98, SessionStackWindow.CalculateHeight(1, false));
    }

    [Fact]
    public void Shadow_safety_area_contains_the_full_blur_and_depth()
    {
        Assert.Equal(392, SessionStackWindow.StackWidth);
        Assert.True(SessionStackWindow.ShadowHorizontalInset >= 18);
        Assert.True(SessionStackWindow.ShadowTopInset >= 18 - 5);
        Assert.True(SessionStackWindow.ShadowBottomInset >= 18 + 5);
    }

    [Theory]
    [InlineData(0, -120, 180, 30)]
    [InlineData(160, -120, 180, 180)]
    [InlineData(40, 120, 180, 10)]
    public void Mouse_wheel_scroll_is_clamped_to_the_session_list(double current, int delta, double maximum, double expected)
    {
        Assert.Equal(expected, SessionStackWindow.CalculateScrollOffset(current, delta, maximum));
    }

    [Theory]
    [InlineData(true, 0, 60, true)]
    [InlineData(false, 0, 60, false)]
    [InlineData(true, 60, 60, false)]
    public void Smooth_scroll_respects_windows_animation_preferences(bool animationsEnabled, double current, double target, bool expected)
    {
        Assert.Equal(expected, SessionStackWindow.ShouldAnimateScroll(animationsEnabled, current, target));
        Assert.InRange(SessionStackWindow.ScrollAnimationMilliseconds, 150, 300);
    }

    [Theory]
    [InlineData(0, 0, 184, true)]
    [InlineData(2, 0, 184, true)]
    [InlineData(3, 0, 184, false)]
    [InlineData(3, 10, 184, false)]
    [InlineData(0, 60, 184, false)]
    [InlineData(1, 60, 184, true)]
    [InlineData(3, 63, 184, true)]
    public void Only_fully_visible_cards_draw_shadows_inside_the_scrolling_viewport(int index, double offset, double viewport, bool expected)
    {
        Assert.Equal(expected, SessionStackWindow.IsShadowSlotVisible(index, offset, viewport));
    }

    [Fact]
    public void Expansion_and_collapse_use_short_opposing_transitions()
    {
        Assert.Equal(-10, SessionStackWindow.ExpansionTransitionOffset(true));
        Assert.Equal(10, SessionStackWindow.ExpansionTransitionOffset(false));
        Assert.InRange(SessionStackWindow.ExpansionAnimationMilliseconds, 150, 300);
    }
}
