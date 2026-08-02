using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class CapsuleThemePolicyTests
{
    [Theory]
    [InlineData(true, true, "OpaqueSystem")]
    [InlineData(false, false, "OpaqueSystem")]
    [InlineData(false, true, "Liquid")]
    public void Resolves_reversible_surface_mode(bool highContrast, bool glassEnabled, string expected)
    {
        Assert.Equal(expected, CapsuleThemePolicy.Resolve(highContrast, glassEnabled).ToString());
    }
}
