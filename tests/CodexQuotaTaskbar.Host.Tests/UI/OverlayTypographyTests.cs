using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class OverlayTypographyTests
{
    [Fact]
    public void Uses_a_modern_cjk_font_with_windows_fallback()
    {
        Assert.Equal("Noto Sans SC, Microsoft YaHei UI", OverlayTypography.TextFamilyName);
        Assert.Contains("Noto Sans SC", OverlayTypography.Text.Source);
        Assert.Contains("Microsoft YaHei UI", OverlayTypography.Text.Source);
    }

    [Fact]
    public void Keeps_numeric_readouts_compact()
    {
        Assert.Equal("Segoe UI Variable Display, Segoe UI", OverlayTypography.NumberFamilyName);
        Assert.Contains("Segoe UI Variable Display", OverlayTypography.Number.Source);
    }
}
