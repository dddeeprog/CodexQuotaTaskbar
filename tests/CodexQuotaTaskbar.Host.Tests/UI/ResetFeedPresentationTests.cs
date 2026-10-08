using CodexQuotaTaskbar.Host.Resets;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class ResetFeedPresentationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-08T12:00:00Z");

    // Public English and zh-CN feed examples observed on 2026-10-08; tests stay offline.
    internal static readonly (string Id, string Original, string Chinese)[] LocalizedExamples =
    [
        ("2108040921044639779",
            "Confirmed landed across all accounts. How are we doing so far? https://t.co/tzw7dNX4pN",
            "已确认在所有账户落地。到目前为止我们做得怎么样？ https://t.co/tzw7dNX4pN"),
        ("2107676072871600470",
            "We shipped four things that were deemed good to great and some math proofs, but the vote is clear and the community demands a reset. I did calibrate it and it *seems* that the game is rigged in reset's favor, but such are the rules at the moment.\n\nTherefore ... the reset has been https://t.co/mHSI0Pcu4M",
            "我们发布了四件被评为良好到优秀的东西以及一些数学证明，但投票结果很明确，社区要求一次重置。我确实校准了它，而且*似乎*游戏被操纵成对重置有利，但目前规则就是这样。\n\n因此……重置已经 https://t.co/mHSI0Pcu4M"),
        ("2106131810921136451",
            "Reset all propagated. Enjoy. https://t.co/GaVJhbptR0",
            "重置已全部传播。享受。 https://t.co/GaVJhbptR0"),
    ];

    [Theory]
    [InlineData(0, "已确认在所有账户落地。到目前为止我们做得怎么样？")]
    [InlineData(1, "我们发布了四件被评为良好到优秀的东西以及一些数学证明，但投票结果很明确，社区要求一次重置。我确实校准了它，而且*似乎*游戏被操纵成对重置有利，但目前规则就是这样。 因此……重置已经")]
    [InlineData(2, "重置已全部传播。享受。")]
    public void Prefers_chinese_translation_and_removes_post_links(int index, string expected)
    {
        var example = LocalizedExamples[index];
        var announcement = new ResetAnnouncement(example.Id, ResetKind.Banked, Now,
            example.Original, CodexResetFeedClient.SiteUri, false) { ChineseText = example.Chinese };

        var text = ResetFeedPresentation.AnnouncementText(announcement);

        Assert.Equal(expected, text);
        Assert.DoesNotContain("t.co", text);
        Assert.DoesNotContain("https://", text);
        Assert.DoesNotContain("以下为原文", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Missing_translation_is_honestly_labeled_and_original_links_are_removed(string? chinese)
    {
        var example = LocalizedExamples[0];
        var announcement = new ResetAnnouncement(example.Id, ResetKind.Banked, Now,
            example.Original + " https://x.com/thsottiaux/status/2108040921044639779",
            CodexResetFeedClient.SiteUri, false) { ChineseText = chinese };

        Assert.Equal("中文译文暂不可用，以下为原文：\nConfirmed landed across all accounts. How are we doing so far?",
            ResetFeedPresentation.AnnouncementText(announcement));
    }

    [Fact]
    public void Translated_body_does_not_expose_x_twitter_or_short_links()
    {
        var announcement = new ResetAnnouncement("example", ResetKind.Regular, Now, "Reset announced.",
            CodexResetFeedClient.SiteUri, false)
        {
            ChineseText = "重置已公布。 https://x.com/thsottiaux/status/123 https://twitter.com/thsottiaux/status/123 https://t.co/example",
        };

        Assert.Equal("重置已公布。", ResetFeedPresentation.AnnouncementText(announcement));
    }

    [Theory]
    [InlineData(0, "刚刚")]
    [InlineData(59, "刚刚")]
    [InlineData(60, "1分钟前")]
    [InlineData(3599, "59分钟前")]
    [InlineData(3600, "1小时前")]
    [InlineData(28800, "8小时前")]
    [InlineData(86400, "1天前")]
    [InlineData(-60, "时间待确认")]
    public void Shows_elapsed_announcement_time_not_a_personal_reset_countdown(int seconds, string expected)
    {
        var snapshot = Feed(ResetKind.Banked, Now.AddSeconds(-seconds));
        Assert.Equal($"最近重置 · {expected} · 备用", ResetFeedPresentation.IslandSummary(snapshot, Now));
    }

    [Fact]
    public void Distinguishes_loading_unavailable_empty_and_cached_data()
    {
        Assert.Equal("最近重置 · 正在读取…", ResetFeedPresentation.IslandSummary(ResetFeedSnapshot.Loading, Now));
        Assert.Equal("最近重置 · 暂不可用", ResetFeedPresentation.IslandSummary(ResetFeedSnapshot.Unavailable(), Now));
        Assert.Equal("最近重置 · 暂无公告", ResetFeedPresentation.IslandSummary(new([], Now, false, null), Now));
        var stale = Feed(ResetKind.Regular, Now.AddHours(-8)) with { IsStale = true, ErrorMessage = "无法连接" };
        Assert.Equal("最近重置 · 8小时前 · 常规 · 缓存", ResetFeedPresentation.IslandSummary(stale, Now));
        Assert.Equal("正在获取公开公告…", ResetFeedPresentation.Status(ResetFeedSnapshot.Loading));
        Assert.Equal("暂无重置公告", ResetFeedPresentation.Status(new([], Now, false, null)));
        Assert.Contains("上次获取", ResetFeedPresentation.Status(stale));
        Assert.Contains("重试", ResetFeedPresentation.Status(ResetFeedSnapshot.Unavailable()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Successful_feed_has_no_status_or_refresh_metadata(bool hasUpdatedAt)
    {
        var snapshot = Feed(ResetKind.Banked, Now.AddHours(-8)) with
        {
            LastUpdatedAt = hasUpdatedAt ? Now : null,
        };

        Assert.Equal(string.Empty, ResetFeedPresentation.Status(snapshot));
    }

    [Theory]
    [InlineData(ResetKind.Banked, "备用重置")]
    [InlineData(ResetKind.Regular, "常规重置")]
    public void Labels_reset_kind(ResetKind kind, string expected) => Assert.Equal(expected, ResetFeedPresentation.KindLabel(kind));

    private static ResetFeedSnapshot Feed(ResetKind kind, DateTimeOffset at) =>
        new([new("example", kind, at, "测试公告", CodexResetFeedClient.SiteUri, true)], Now, false, null);
}
