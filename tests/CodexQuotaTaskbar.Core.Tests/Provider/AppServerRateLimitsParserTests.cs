using CodexQuotaTaskbar.Core.Provider;

namespace CodexQuotaTaskbar.Core.Tests.Provider;

public sealed class AppServerRateLimitsParserTests
{
    [Fact]
    public void Parses_codex_bucket_and_converts_used_to_remaining()
    {
        const string json = """
        {"rateLimits":{"limitId":"codex","primary":{"usedPercent":28,"windowDurationMins":300,"resetsAt":1785661200},"secondary":{"usedPercent":59,"windowDurationMins":10080,"resetsAt":1786266000}},"rateLimitsByLimitId":{}}
        """;

        var snapshot = AppServerRateLimitsParser.Parse(json, DateTimeOffset.UnixEpoch);

        Assert.Equal(2, snapshot.Windows.Count);
        Assert.Equal(72, snapshot.Windows[0].RemainingPercent);
        Assert.Equal(41, snapshot.Windows[1].RemainingPercent);
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
