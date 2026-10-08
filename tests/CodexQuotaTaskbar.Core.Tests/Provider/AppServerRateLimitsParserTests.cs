using CodexQuotaTaskbar.Core.Provider;
using CodexQuotaTaskbar.Core.Quota;

namespace CodexQuotaTaskbar.Core.Tests.Provider;

public sealed class AppServerRateLimitsParserTests
{
    [Fact]
    public void Parses_codex_bucket_and_converts_used_to_remaining()
    {
        const string json = """
        {"rateLimits":{"limitId":"codex","primary":{"usedPercent":28,"windowDurationMins":300,"resetsAt":1785661200},"secondary":{"usedPercent":59,"windowDurationMins":10080,"resetsAt":1786266000},"credits":{"hasCredits":true,"unlimited":false,"balance":"12345.6789000000"}},"rateLimitsByLimitId":{}}
        """;

        var snapshot = AppServerRateLimitsParser.Parse(json, DateTimeOffset.UnixEpoch, "Pro Lite");

        Assert.Equal(2, snapshot.Windows.Count);
        Assert.Equal(72, snapshot.Windows[0].RemainingPercent);
        Assert.Equal(41, snapshot.Windows[1].RemainingPercent);
        Assert.Equal("Pro Lite", snapshot.SubscriptionPlan);
        Assert.NotNull(snapshot.ExtraCredits);
        Assert.True(snapshot.ExtraCredits.HasCredits);
        Assert.False(snapshot.ExtraCredits.Unlimited);
        Assert.Equal(12345.6789000000m, snapshot.ExtraCredits.Balance);
    }

    [Fact]
    public void Falls_back_to_codex_bucket_for_unlimited_credits()
    {
        const string json = """
        {"rateLimitsByLimitId":{"codex":{"limitId":"codex","primary":{"usedPercent":0,"windowDurationMins":300,"resetsAt":1785661200},"credits":{"hasCredits":true,"unlimited":true,"balance":null}}}}
        """;

        var snapshot = AppServerRateLimitsParser.Parse(json, DateTimeOffset.UnixEpoch);

        Assert.NotNull(snapshot.ExtraCredits);
        Assert.True(snapshot.ExtraCredits.HasCredits);
        Assert.True(snapshot.ExtraCredits.Unlimited);
        Assert.Null(snapshot.ExtraCredits.Balance);
    }

    [Fact]
    public void Ignores_malformed_optional_credit_balance_without_losing_quota_windows()
    {
        const string json = """
        {"rateLimits":{"limitId":"codex","primary":{"usedPercent":28,"windowDurationMins":300,"resetsAt":1785661200},"credits":{"hasCredits":true,"unlimited":false,"balance":"not-a-number"}}}
        """;

        var snapshot = AppServerRateLimitsParser.Parse(json, DateTimeOffset.UnixEpoch);

        Assert.Single(snapshot.Windows);
        Assert.NotNull(snapshot.ExtraCredits);
        Assert.Null(snapshot.ExtraCredits.Balance);
    }

    [Fact]
    public void Keeps_legacy_quota_windows_when_credit_metadata_is_missing()
    {
        const string json = """
        {"rateLimits":{"limitId":"codex","primary":{"usedPercent":28,"windowDurationMins":300,"resetsAt":1785661200}},"rateLimitsByLimitId":{}}
        """;

        var snapshot = AppServerRateLimitsParser.Parse(json, DateTimeOffset.UnixEpoch);

        Assert.Single(snapshot.Windows);
        Assert.Null(snapshot.ExtraCredits);
    }

    [Fact]
    public void Stale_snapshot_keeps_account_metadata()
    {
        var expiry = DateTimeOffset.Parse("2026-12-31T12:00:00+08:00");
        var credits = new QuotaCreditsSnapshot(true, false, 42.5m);
        var subscription = SubscriptionExpirationSnapshot.Available(
            expiry,
            DateTimeOffset.Parse("2026-10-08T12:00:00+08:00"),
            SubscriptionExpirationSource.CodexLogin);

        var snapshot = QuotaSnapshot.Stale([], DateTimeOffset.UnixEpoch, "连接中断", "PRO 20X", subscription, credits);

        Assert.Equal("PRO 20X", snapshot.SubscriptionPlan);
        Assert.Equal(expiry, snapshot.MembershipExpiresAt);
        Assert.Same(subscription, snapshot.SubscriptionExpiration);
        Assert.Same(credits, snapshot.ExtraCredits);
    }

    [Theory]
    [InlineData("{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":null,\"windowDurationMins\":60,\"resetsAt\":1}}}")]
    [InlineData("{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":101,\"windowDurationMins\":60,\"resetsAt\":1}}}")]
    [InlineData("not json")]
    public void Rejects_malformed_required_fields(string json)
    {
        Assert.Throws<AppServerProtocolException>(() => AppServerRateLimitsParser.Parse(json, DateTimeOffset.UtcNow));
    }
}
