using CodexQuotaTaskbar.Host.Provider;

namespace CodexQuotaTaskbar.Host.Tests.Provider;

public sealed class SubscriptionPlanFormatterTests
{
    [Fact]
    public void Automatic_refresh_uses_the_three_minute_fallback_interval()
    {
        Assert.Equal(TimeSpan.FromMinutes(3), CodexRateLimitProvider.AutomaticRefreshInterval);
    }

    [Theory]
    [InlineData("free", "FREE")]
    [InlineData("plus", "PLUS")]
    [InlineData("pro", "PRO 20X")]
    [InlineData("prolite", "PRO 5X")]
    [InlineData("TEAM", "TEAM")]
    [InlineData("hc", "Enterprise")]
    [InlineData("education", "Edu")]
    [InlineData("future_plan", "其他")]
    [InlineData(null, "未知")]
    public void Formats_known_plans_without_exposing_account_fields(string? rawPlan, string expected)
    {
        Assert.Equal(expected, SubscriptionPlanFormatter.Format(rawPlan));
    }
}
