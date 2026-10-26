using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Host.Overlay;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class BongoGroupFitTests
{
    [Theory]
    [InlineData(96)] [InlineData(144)] [InlineData(192)]
    public void Classic_model_fit_reserves_the_expanded_task_stack(int dpi)
    {
        var scale = dpi / 96d;
        var work = new ScreenRect(-1000, 0, 1000, (int)(300 * scale));
        Assert.True(OverlayCoordinator.BongoGroupFits(work, dpi, 306, 112, 120));
        Assert.False(OverlayCoordinator.BongoGroupFits(work, dpi, 306, 112, 120, 180, 306));
        Assert.True(OverlayCoordinator.BongoGroupFits(work with { Bottom = (int)(420 * scale) }, dpi, 306, 112, 120, 180, 306));
    }

    [Fact]
    public void A_wide_classic_stack_cannot_be_ignored_when_the_owner_fits()
    {
        Assert.False(OverlayCoordinator.BongoGroupFits(new(0, 0, 290, 1000), 96, 280, 112, 120, 80, 306));
    }
}
