using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class OverlayGlassMaterialTests
{
    [Fact]
    public void Shared_surface_matches_the_session_glass_recipe()
    {
        var brush = OverlayGlassMaterial.CreateSurfaceBrush();

        Assert.Equal(20, OverlayGlassMaterial.BlurRadius);
        Assert.Equal(0.92, OverlayGlassMaterial.Merging);
        Assert.Equal(70, OverlayGlassMaterial.BorderAlpha);
        Assert.Same(OverlayGlassMaterial.Surface, OverlayGlassMaterial.HoverSurface);
        Assert.Equal(2, brush.GradientStops.Count);
        Assert.Equal((136, 58, 58, 60), Components(brush.GradientStops[0].Color));
        Assert.Equal((116, 48, 48, 50), Components(brush.GradientStops[1].Color));
    }

    private static (byte A, byte R, byte G, byte B) Components(System.Windows.Media.Color color) =>
        (color.A, color.R, color.G, color.B);
}
