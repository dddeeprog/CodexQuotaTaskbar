using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class QuotaPopoverWindowTests
{
    [Theory]
    [InlineData("codex", "Codex · 7 天额度")]
    [InlineData("CODEX", "Codex · 7 天额度")]
    [InlineData("codex_bengalfox", "Spark · 7 天额度")]
    [InlineData("other_limit", "其他 · 7 天额度")]
    public void Formats_quota_source_in_window_title(string limitId, string expected)
    {
        var window = new QuotaWindowSnapshot(
            QuotaWindowKind.Primary,
            50,
            10080,
            DateTimeOffset.UtcNow.AddDays(7),
            limitId);

        Assert.Equal(expected, QuotaPopoverWindow.FormatWindowTitle(window));
    }

    [Fact]
    public void Orders_codex_above_spark_and_spark_above_other_sources()
    {
        Assert.True(QuotaPopoverWindow.SourceRank("codex") < QuotaPopoverWindow.SourceRank("codex_bengalfox"));
        Assert.True(QuotaPopoverWindow.SourceRank("codex_bengalfox") < QuotaPopoverWindow.SourceRank("other_limit"));
    }
}
