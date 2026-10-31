using CodexQuotaTaskbar.Core.Provider;
using CodexQuotaTaskbar.Core.Quota;

namespace CodexQuotaTaskbar.Core.Tests.Provider;

public sealed class AppServerRateLimitsParserTests
{
    [Fact]
    public void Exposes_personal_reset_credits_independently_of_paid_credits()
    {
        var snapshot = AppServerRateLimitsParser.Parse("""{"rateLimits":{"primary":null,"secondary":null},"rateLimitResetCredits":{"availableCount":0,"credits":[]}}""", DateTimeOffset.UnixEpoch);
        Assert.Equal(0, snapshot.ResetCredits.AvailableCount);
        Assert.Empty(snapshot.ResetCredits.Credits!);
    }

    private static QuotaResetCreditsSnapshot ResetCards(string field) => AppServerRateLimitsParser.Parse(
        "{\"rateLimits\":{\"primary\":null,\"secondary\":null},\"rateLimitResetCredits\":" + field + "}", DateTimeOffset.UnixEpoch).ResetCredits;

    [Fact]
    public void Expiry_field_presence_distinguishes_missing_from_explicit_null()
    {
        var cards = ResetCards("""{"availableCount":2,"credits":[{"id":"s1","resetType":"codexRateLimits","status":"available","grantedAt":0},{"id":"s2","resetType":"codexRateLimits","status":"available","grantedAt":0,"expiresAt":null}]}""");
        Assert.False(cards.Credits![0].HasExpiryInformation);
        Assert.True(cards.Credits![1].HasExpiryInformation);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"availableCount\":-1}")]
    [InlineData("{\"availableCount\":\"3\"}")]
    [InlineData("{\"availableCount\":1.5}")]
    public void Invalid_or_unknown_personal_credit_count_is_not_zero(string field)
    {
        Assert.Equal(QuotaResetCreditsSnapshot.Unknown, ResetCards(field));
    }

    [Fact]
    public void Missing_personal_credit_field_does_not_use_paid_credit_balance()
    {
        var snapshot = AppServerRateLimitsParser.Parse("""{"rateLimits":{"primary":null,"credits":{"hasCredits":true,"unlimited":false,"balance":10}}} """, DateTimeOffset.UnixEpoch);
        Assert.Equal(QuotaResetCreditsSnapshot.Unknown, snapshot.ResetCredits);
    }

    [Fact]
    public void Count_only_and_truncated_details_keep_authoritative_int64_count()
    {
        var countOnly = ResetCards("""{"availableCount":9223372036854775807,"credits":null}""");
        Assert.Equal(long.MaxValue, countOnly.AvailableCount);
        Assert.Null(countOnly.Credits);
        var partial = ResetCards("""{"availableCount":7,"credits":[{"id":"synthetic","resetType":"codexRateLimits","status":"available","grantedAt":0,"expiresAt":42}]}""");
        Assert.Equal(7, partial.AvailableCount);
        Assert.Single(partial.Credits!);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(42), partial.Credits![0].ExpiresAt);
        Assert.Equal(DateTimeOffset.UnixEpoch, partial.CapturedAt);
    }

    [Theory]
    [InlineData("{\"availableCount\":2,\"credits\":false}")]
    [InlineData("{\"availableCount\":2}")]
    public void Unavailable_details_preserve_count_without_fabricating_rows(string json)
    {
        var cards = ResetCards(json);
        Assert.Equal(2, cards.AvailableCount);
        Assert.Null(cards.Credits);
    }

    [Theory]
    [InlineData("\"future\"", "\"available\"", "0", "0")]
    [InlineData("\"codexRateLimits\"", "\"expired\"", "0", "0")]
    [InlineData("\"codexRateLimits\"", "\"available\"", "999999999999999", "0")]
    [InlineData("\"codexRateLimits\"", "\"available\"", "0", "999999999999999")]
    [InlineData("\"codexRateLimits\"", "\"available\"", "null", "0")]
    public void Invalid_card_metadata_does_not_poison_quota_or_authoritative_count(string type, string status, string granted, string expires)
    {
        var cards = ResetCards($"{{\"availableCount\":4,\"credits\":[{{\"id\":\"s\",\"resetType\":{type},\"status\":{status},\"grantedAt\":{granted},\"expiresAt\":{expires}}}]}}");
        Assert.Equal(4, cards.AvailableCount);
        Assert.Empty(cards.Credits!);
    }

    [Fact]
    public void Known_unknown_enums_are_preserved_but_duplicates_are_not_counted_twice()
    {
        var cards = ResetCards("""{"availableCount":2,"credits":[{"id":"s","resetType":"unknown","status":"unknown","grantedAt":0},{"id":"s","resetType":"codexRateLimits","status":"available","grantedAt":0}]}""");
        var card = Assert.Single(cards.Credits!);
        Assert.Equal("unknown", card.Status);
        Assert.Equal("unknown", card.ResetType);
    }

    [Fact]
    public void Subscription_updates_preserve_cards_while_unavailable_snapshots_clear_them()
    {
        var cards = ResetCards("""{"availableCount":1,"credits":null}""");
        var snapshot = QuotaSnapshot.Available([], DateTimeOffset.UnixEpoch) with { ResetCredits = cards };
        Assert.Same(cards, snapshot.WithSubscriptionExpiration(SubscriptionExpirationSnapshot.Disabled()).ResetCredits);
        Assert.Equal(QuotaResetCreditsSnapshot.Unknown, QuotaSnapshot.Unavailable("offline").ResetCredits);
    }

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
    [InlineData("{\"primary\":{\"usedPercent\":null,\"windowDurationMins\":60,\"resetsAt\":1785661200}}")]
    [InlineData("{\"primary\":{\"usedPercent\":10,\"windowDurationMins\":60,\"resetsAt\":9999999999999}}")]
    [InlineData("{\"primary\":42}")]
    [InlineData("null")]
    public void Unknown_nullable_or_unrepresentable_bucket_does_not_discard_valid_codex(string unknownBucket)
    {
        var json = "{\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"usedPercent\":28,\"windowDurationMins\":300,\"resetsAt\":1785661200}},\"future-meter\":" + unknownBucket + "}}";
        var snapshot = AppServerRateLimitsParser.Parse(json, DateTimeOffset.UnixEpoch);
        var known = Assert.Single(snapshot.Windows);
        Assert.Equal("codex", known.LimitId);
        Assert.Equal(72, known.RemainingPercent);
    }

    [Theory]
    [InlineData("{\"rateLimits\":{\"primary\":{\"usedPercent\":10,\"windowDurationMins\":\"bad\",\"resetsAt\":1785661200}}}")]
    [InlineData("{\"rateLimits\":{\"primary\":{\"usedPercent\":10,\"windowDurationMins\":300,\"resetsAt\":\"bad\"}}}")]
    [InlineData("{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":null,\"windowDurationMins\":60,\"resetsAt\":1}}}")]
    [InlineData("{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":101,\"windowDurationMins\":60,\"resetsAt\":1}}}")]
    [InlineData("not json")]
    public void Rejects_malformed_required_fields(string json)
    {
        Assert.Throws<AppServerProtocolException>(() => AppServerRateLimitsParser.Parse(json, DateTimeOffset.UtcNow));
    }
}
