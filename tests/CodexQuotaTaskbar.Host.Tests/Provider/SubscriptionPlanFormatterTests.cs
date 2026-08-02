using CodexQuotaTaskbar.Host.Provider;

namespace CodexQuotaTaskbar.Host.Tests.Provider;

public sealed class SubscriptionPlanFormatterTests
{
    [Theory]
    [InlineData("free", "Free")]
    [InlineData("plus", "Plus")]
    [InlineData("pro", "Pro")]
    [InlineData("prolite", "Pro Lite")]
    [InlineData("TEAM", "Team")]
    [InlineData("hc", "Enterprise")]
    [InlineData("education", "Edu")]
    [InlineData("future_plan", "其他")]
    [InlineData(null, "未知")]
    public void Formats_known_plans_without_exposing_account_fields(string? rawPlan, string expected)
    {
        Assert.Equal(expected, SubscriptionPlanFormatter.Format(rawPlan));
    }
}
