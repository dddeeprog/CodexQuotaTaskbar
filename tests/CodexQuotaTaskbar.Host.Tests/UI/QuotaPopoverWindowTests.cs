using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Host.UI;
using System.Windows.Media.Animation;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class QuotaPopoverWindowTests
{
    [Fact]
    public void Details_blur_the_capture_layer_behind_the_content_surface()
    {
        Assert.Equal("BackdropLayer", QuotaPopoverWindow.GlassCaptureTargetName);
        Assert.Equal(SessionStackWindow.SessionGlassBlurRadius, QuotaPopoverWindow.GlassBlurRadius);
        Assert.Equal(SessionStackWindow.SessionGlassMerging, QuotaPopoverWindow.GlassMerging);
    }

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

    [Fact]
    public void Formats_membership_expiration_or_explains_when_the_api_omits_it()
    {
        var localDate = new DateTimeOffset(
            2026,
            12,
            31,
            12,
            0,
            0,
            TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 12, 31, 12, 0, 0)));

        var available = SubscriptionExpirationSnapshot.Available(
            localDate,
            new DateTimeOffset(2026, 10, 8, 16, 8, 0, localDate.Offset),
            SubscriptionExpirationSource.CodexLogin);
        var missing = SubscriptionExpirationSnapshot.NotProvided(
            localDate,
            SubscriptionExpirationSource.ChatGptSubscription);

        Assert.Equal("2026年12月31日", QuotaPopoverWindow.FormatMembershipExpiration(available));
        Assert.Equal("未提供", QuotaPopoverWindow.FormatMembershipExpiration(missing));
        Assert.Contains("Codex 登录信息", QuotaPopoverWindow.FormatMembershipExpirationMetadata(available), StringComparison.Ordinal);
        Assert.Contains("更新于", QuotaPopoverWindow.FormatMembershipExpirationMetadata(available), StringComparison.Ordinal);
    }

    [Fact]
    public void Distinguishes_disabled_failed_and_stale_subscription_states()
    {
        var checkedAt = DateTimeOffset.Parse("2026-10-08T16:08:00+08:00");
        var unavailable = SubscriptionExpirationSnapshot.Unavailable(
            checkedAt,
            SubscriptionExpirationSource.ChatGptSubscription);
        var stale = SubscriptionExpirationSnapshot.Available(
            checkedAt.AddMonths(1),
            checkedAt,
            SubscriptionExpirationSource.ChatGptSubscription).AsStale();

        Assert.Equal("增强读取已关闭", QuotaPopoverWindow.FormatMembershipExpiration(SubscriptionExpirationSnapshot.Disabled()));
        Assert.Equal("暂不可用", QuotaPopoverWindow.FormatMembershipExpiration(unavailable));
        Assert.Contains("检查失败", QuotaPopoverWindow.FormatMembershipExpirationMetadata(unavailable), StringComparison.Ordinal);
        Assert.Contains("缓存", QuotaPopoverWindow.FormatMembershipExpirationMetadata(stale), StringComparison.Ordinal);
    }

    [Fact]
    public void Formats_extra_credits_for_balance_unlimited_zero_and_missing_states()
    {
        Assert.Equal("12,345.68", QuotaPopoverWindow.FormatExtraCredits(new QuotaCreditsSnapshot(true, false, 12345.6789m)));
        Assert.Equal("不限", QuotaPopoverWindow.FormatExtraCredits(new QuotaCreditsSnapshot(true, true, null)));
        Assert.Equal("可用", QuotaPopoverWindow.FormatExtraCredits(new QuotaCreditsSnapshot(true, false, null)));
        Assert.Equal("0", QuotaPopoverWindow.FormatExtraCredits(new QuotaCreditsSnapshot(false, false, null)));
        Assert.Equal("—", QuotaPopoverWindow.FormatExtraCredits(null));
    }

    [Theory]
    [InlineData(800, 100, 1128, 460, 0, 10)]
    [InlineData(800, 620, 1128, 980, 0, -10)]
    [InlineData(400, 300, 728, 660, 10, 0)]
    [InlineData(1192, 300, 1520, 660, -10, 0)]
    public void Details_enter_from_the_direction_of_the_island_and_session_group(
        int left, int top, int right, int bottom, double expectedX, double expectedY)
    {
        var anchor = new ScreenRect(800, 480, 1120, 600);
        var offset = QuotaPopoverWindow.CalculateEntranceOffset(anchor, new ScreenRect(left, top, right, bottom));

        Assert.Equal((expectedX, expectedY), offset);
        Assert.InRange(QuotaPopoverWindow.EntranceAnimationMilliseconds, 150, 300);
        Assert.InRange(QuotaPopoverWindow.ExitAnimationMilliseconds, 100, 180);
        Assert.Equal(EasingMode.EaseOut, QuotaPopoverWindow.ExitEasingMode);
    }
}
