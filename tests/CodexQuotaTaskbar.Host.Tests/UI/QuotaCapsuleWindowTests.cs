using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Host.Overlay;
using CodexQuotaTaskbar.Host.UI;
using System.Windows.Media.Animation;
using System.Runtime.ExceptionServices;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class QuotaCapsuleWindowTests
{
    [Theory]
    [InlineData(96, 0)]
    [InlineData(108, 0)]
    [InlineData(144, -1920)]
    public void Integrated_panel_reserves_expansion_without_moving_the_first_row(int dpi, int origin)
    {
        var anchor = new TaskbarAnchor("panel", new(origin, 0, origin + 1920, 1080),
            new(origin, 1040, origin + 1920, 1080), TaskbarEdge.Bottom, dpi, true, true);
        var requested = new ScreenRect(origin + 1900, 1010, origin + 2180, 1142);
        var collapsed = OverlayCoordinator.CalculatePanelBounds(anchor, requested, 132, 200);
        for (double height = 132; height <= 200; height += 0.5)
        {
            var frame = OverlayCoordinator.CalculatePanelBounds(anchor, requested, height, 200);
            Assert.Equal(collapsed.Left, frame.Left);
            Assert.Equal(collapsed.Top, frame.Top);
            Assert.InRange(frame.Right, origin, origin + 1920);
            Assert.InRange(frame.Bottom, 0, 1040);
        }
    }

    [Theory]
    [InlineData(96, "left")]
    [InlineData(96, "right")]
    [InlineData(96, "bottom")]
    [InlineData(108, "left")]
    [InlineData(108, "right")]
    [InlineData(108, "bottom")]
    [InlineData(144, "left")]
    [InlineData(144, "right")]
    [InlineData(144, "bottom")]
    public void Inward_drag_requests_are_constrained_before_any_native_move_and_stay_stable_on_refresh(int dpi, string edge)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new QuotaCapsuleWindow("drag-order-test");
                try
                {
                    var anchor = new TaskbarAnchor("test", new(0, 0, 1920, 1080), new(0, 1040, 1920, 1080), TaskbarEdge.Bottom, dpi, true, true);
                    var width = (int)Math.Round(QuotaCapsuleWindow.WindowWidth * dpi / 96d);
                    var height = (int)Math.Round(QuotaCapsuleWindow.WindowHeight * dpi / 96d);
                    var applied = new List<ScreenRect>();
                    var requestCount = 0;
                    window.UserMoveRequested += requested =>
                    {
                        // This window has no HWND: an eager Place(requested) would throw before this callback.
                        Assert.Equal(default, window.PhysicalBounds);
                        requestCount++;
                        var resolved = OverlayCoordinator.CalculatePanelBounds(anchor, requested, 132, 200);
                        if (applied.Count == 0 || applied[^1] != resolved) applied.Add(resolved);
                        // Model the next timer/session refresh using the corrected position, not the raw request.
                        Assert.Equal(resolved, OverlayCoordinator.CalculatePanelBounds(anchor, resolved, 132, 200));
                    };
                    for (var step = 0; step <= 600; step += 10)
                    {
                        var left = edge == "left" ? -200 + step : edge == "right" ? 1850 - step : 800;
                        var top = edge == "bottom" ? 950 - step : 300;
                        var requested = new ScreenRect(left, top, left + width, top + height);
                        window.MoveByUser(requested);
                        window.MoveByUser(requested); // Native move notifications may repeat the same cursor position.
                    }
                    Assert.Equal(122, requestCount);
                    Assert.True(applied.Count > 1);
                    for (var index = 1; index < applied.Count; index++)
                    {
                        var movement = edge == "bottom" ? applied[index].Top - applied[index - 1].Top
                            : applied[index].Left - applied[index - 1].Left;
                        Assert.InRange(movement, edge == "left" ? 0 : -10, edge == "left" ? 10 : 0);
                    }
                }
                finally { window.Close(); }
            }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Drag ordering verification timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

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
        Assert.Equal(280, QuotaCapsuleWindow.WindowWidth);
        Assert.Equal(80, QuotaCapsuleWindow.WindowHeight);
        Assert.True(QuotaCapsuleWindow.BadgeShadowSafeInset >= 8 + 2);
    }

    [Fact]
    public void Island_blurs_the_capture_layer_behind_its_tint()
    {
        Assert.Equal("BackdropLayer", QuotaCapsuleWindow.GlassCaptureTargetName);
        Assert.Equal(SessionStackWindow.SessionGlassBlurRadius, QuotaCapsuleWindow.GlassBlurRadius);
        Assert.Equal(SessionStackWindow.SessionGlassMerging, QuotaCapsuleWindow.GlassMerging);
    }

    [Fact]
    public void Stale_quota_does_not_change_the_whole_island_material_opacity()
    {
        Assert.Equal(1, QuotaCapsuleWindow.ResolveSurfaceOpacity(CodexQuotaTaskbar.Core.Quota.QuotaAvailability.Available));
        Assert.Equal(1, QuotaCapsuleWindow.ResolveSurfaceOpacity(CodexQuotaTaskbar.Core.Quota.QuotaAvailability.Stale));
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
    public void Session_pair_at_secondary_monitor_edge_stays_centered_on_that_monitor()
    {
        var primary = new TaskbarAnchor("primary", new(0, 0, 1920, 1080), new(0, 1040, 1920, 1080), TaskbarEdge.Bottom, 96, true, true);
        var secondary = new TaskbarAnchor("secondary", new(-1920, 0, 0, 1080), new(-1920, 1040, 0, 1080), TaskbarEdge.Bottom, 144, false, true);
        var requested = new ScreenRect(-1980, 700, -1521, 868);
        var selected = OverlayCoordinator.SelectAnchor([primary, secondary], primary.MonitorId, requested);
        Assert.Same(secondary, selected);
        var placement = SessionStackPlacementCalculator.CalculateBelow(selected!, requested,
            SessionStackWindow.StackWidth, OverlayCoordinator.ReservedSessionHeight(4), -16);
        Assert.Equal(-1920, placement.Stack.Left);
        Assert.InRange(Math.Abs(placement.Capsule.Left + placement.Capsule.Right - placement.Stack.Left - placement.Stack.Right), 0, 1);
        var collapsed = OverlayCoordinator.ResizeStackHeight(placement.Stack, SessionStackWindow.CalculateHeight(4, false), selected!.Dpi);
        Assert.Equal(placement.Stack.Left, collapsed.Left);
        Assert.Equal(placement.Stack.Top, collapsed.Top);
        Assert.Equal(placement.Stack.Right, collapsed.Right);
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
