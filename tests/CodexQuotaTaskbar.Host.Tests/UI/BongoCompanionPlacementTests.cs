using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class BongoCompanionPlacementTests
{
    [Theory]
    [InlineData(96, 160)] [InlineData(96, 192)] [InlineData(96, 240)]
    [InlineData(144, 160)] [InlineData(144, 192)] [InlineData(144, 240)]
    [InlineData(192, 160)] [InlineData(192, 192)] [InlineData(192, 240)]
    public void Model_keeps_its_aspect_and_does_not_change_the_original_group(int dpi, double widthDip)
    {
        var work = new ScreenRect(-1920, 0, 0, 1040);
        var group = new ScreenRect(-620, 700, -200, 970);
        var unchanged = group;
        Assert.True(BongoCompanionPlacement.TryCalculate(work, group, -220, dpi, widthDip, out var bounds));
        Assert.Equal(unchanged, group);
        Assert.Equal((int)Math.Round(widthDip * dpi / 96d), bounds.Width);
        Assert.InRange(Math.Abs(bounds.Width / (double)bounds.Height - BongoCompanionPlacement.ModelAspect), 0, .02);
        AssertContained(work, bounds);
        Assert.True(bounds.Bottom < group.Top);
    }

    [Fact]
    public void Model_shrinks_above_group_without_crossing_the_minimum_width()
    {
        var group = new ScreenRect(400, 110, 680, 900);
        Assert.True(BongoCompanionPlacement.TryCalculate(new(0, 0, 1000, 1000), group, 650, 96, 240, out var bounds));
        Assert.InRange(bounds.Width, 140, 190);
        Assert.Equal(group.Top - 4, bounds.Bottom);
    }

    [Fact]
    public void Top_edge_chooses_below_and_preserves_all_task_rows()
    {
        var group = new ScreenRect(400, 0, 680, 300);
        Assert.True(BongoCompanionPlacement.TryCalculate(new(0, 0, 1000, 1000), group, 650, 96, 192, out var bounds));
        Assert.Equal(group.Bottom + 4, bounds.Top);
    }

    [Fact]
    public void Very_tall_group_uses_side_space_and_tiny_work_area_fails_closed()
    {
        Assert.True(BongoCompanionPlacement.TryCalculate(new(0, 0, 1000, 600), new(300, 0, 600, 600), 580, 96, 192, out var side));
        Assert.Equal(604, side.Left);
        Assert.False(BongoCompanionPlacement.TryCalculate(new(0, 0, 120, 100), new(0, 0, 120, 100), 110, 96, 192, out _));
    }

    [Theory]
    [InlineData(-100, 140)] [InlineData(900, 240)] [InlineData(double.NaN, 192)]
    public void Width_preference_is_bounded(double input, double expected) =>
        Assert.Equal(expected, BongoCompanionPlacement.ClampWidth(input));

    [Fact]
    public void A_drag_step_translates_model_immediately_without_resizing_or_polling()
    {
        var work = new ScreenRect(0, 0, 1920, 1040);
        var initial = new ScreenRect(600, 700, 880, 900);
        Assert.True(BongoCompanionPlacement.TryCalculate(work, initial, 850, 144, 192, out var previous));
        for (var delta = 1; delta <= 80; delta++)
        {
            Assert.True(BongoCompanionPlacement.TryCalculate(work,
                QuotaCapsuleWindow.TranslateBounds(initial, delta, -delta), 850 + delta, 144, 192, out var next));
            Assert.Equal(previous.Width, next.Width);
            Assert.Equal(previous.Height, next.Height);
            Assert.Equal(previous.Left + 1, next.Left);
            Assert.Equal(previous.Top - 1, next.Top);
            previous = next;
        }
    }

    private static void AssertContained(ScreenRect work, ScreenRect bounds)
    {
        Assert.InRange(bounds.Left, work.Left, work.Right);
        Assert.InRange(bounds.Right, work.Left, work.Right);
        Assert.InRange(bounds.Top, work.Top, work.Bottom);
        Assert.InRange(bounds.Bottom, work.Top, work.Bottom);
    }
}
