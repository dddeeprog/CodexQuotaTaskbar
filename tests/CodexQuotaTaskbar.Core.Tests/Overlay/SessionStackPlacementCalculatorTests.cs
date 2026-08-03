using CodexQuotaTaskbar.Core.Overlay;

namespace CodexQuotaTaskbar.Core.Tests.Overlay;

public sealed class SessionStackPlacementCalculatorTests
{
    private static readonly TaskbarAnchor Anchor = new(
        "primary",
        new ScreenRect(0, 0, 1920, 1080),
        new ScreenRect(0, 1040, 1920, 1080),
        TaskbarEdge.Bottom,
        96,
        true,
        true);

    [Fact]
    public void Keeps_the_session_stack_below_the_island_and_shifts_the_pair_inside_the_work_area()
    {
        var island = new ScreenRect(807, 960, 1113, 1040);

        var result = SessionStackPlacementCalculator.CalculateBelow(Anchor, island, 392, 256, -16);

        Assert.Equal(new ScreenRect(807, 720, 1113, 800), result.Capsule);
        Assert.Equal(new ScreenRect(764, 784, 1156, 1040), result.Stack);
    }

    [Fact]
    public void Leaves_an_island_unchanged_when_the_stack_already_fits_below()
    {
        var island = new ScreenRect(807, 0, 1113, 80);

        var result = SessionStackPlacementCalculator.CalculateBelow(Anchor, island, 392, 256, -16);

        Assert.Equal(island, result.Capsule);
        Assert.Equal(64, result.Stack.Top);
        Assert.Equal(764, result.Stack.Left);
    }

    [Fact]
    public void Clamps_the_wider_stack_at_the_screen_edge()
    {
        var island = new ScreenRect(0, 500, 306, 580);

        var result = SessionStackPlacementCalculator.CalculateBelow(Anchor, island, 392, 256, -16);

        Assert.Equal(0, result.Stack.Left);
        Assert.InRange(result.Stack.Top, 0, 1040 - 256);
    }
}
