using CodexQuotaTaskbar.Core.Provider;
using CodexQuotaTaskbar.Core.Quota;

namespace CodexQuotaTaskbar.Core.Tests.Quota;

public sealed class AdaptiveQuotaTests
{
    private static QuotaSnapshot Parse(string bucket, string plan = "PRO 20X") =>
        AppServerRateLimitsParser.Parse("{\"rateLimits\":" + bucket + "}", DateTimeOffset.UnixEpoch, plan);
    private static string Window(int duration, int used = 20) => $"{{\"usedPercent\":{used},\"windowDurationMins\":{duration},\"resetsAt\":1785661200}}";

    [Theory]
    [InlineData("PRO 20X")] [InlineData("PRO 5X")] [InlineData("PLUS")]
    public void Actual_windows_can_appear_and_disappear_without_a_plan_change(string plan)
    {
        var week = Window(10080); var shortTerm = Window(300);
        foreach (var both in new[] { false, true, false })
        {
            var snapshot = Parse($"{{\"primary\":{(both ? shortTerm : "null")},\"secondary\":{week}}}", plan);
            var rows = QuotaCapsuleProjection.Create(snapshot).Rows;
            Assert.Equal(both ? 2 : 1, rows.Count); Assert.Equal("7 天", rows.Last().Label);
            if (both) Assert.Equal("5 小时", rows.First().Label);
            Assert.Empty(snapshot.UnresolvedWindows);
        }
    }

    [Theory]
    [InlineData(30, "30 分钟")] [InlineData(90, "90 分钟")] [InlineData(1500, "25 小时")]
    [InlineData(60, "1 小时")] [InlineData(10080, "7 天")]
    public void Durations_and_zero_remaining_are_not_defaulted(int duration, string label)
    {
        var snapshot = Parse($"{{\"primary\":{Window(duration, 100)},\"secondary\":null}}");
        var row = Assert.Single(QuotaCapsuleProjection.Create(snapshot).Rows);
        Assert.Equal(label, row.Label); Assert.Equal(0, row.RemainingPercent); Assert.Equal("0%", row.Text);
        Assert.Equal("Critical", row.ColorToken);
    }

    [Fact]
    public void Explicit_empty_does_not_resurrect_legacy_and_preserves_credits()
    {
        var snapshot = AppServerRateLimitsParser.Parse("""
            {"rateLimits":{"primary":{"usedPercent":20,"windowDurationMins":300,"resetsAt":1785661200}},
             "rateLimitsByLimitId":{"codex":{"primary":null,"secondary":null,"credits":{"hasCredits":true,"unlimited":false,"balance":"42"}}}}
            """, DateTimeOffset.UnixEpoch);
        Assert.Equal(QuotaAvailability.Available, snapshot.Availability); Assert.Empty(snapshot.Windows);
        Assert.Empty(snapshot.UnresolvedWindows); Assert.Equal(42, snapshot.ExtraCredits!.Balance);
        var row = Assert.Single(QuotaCapsuleProjection.Create(snapshot).Rows);
        Assert.Equal("无 Codex 窗口", row.Label); Assert.Null(row.RemainingPercent); Assert.Equal("—", row.Text);
    }

    [Fact]
    public void Missing_slots_are_unknown_not_an_empty_or_unlimited_response()
    {
        var snapshot = Parse("{}"); Assert.Equal(2, snapshot.UnresolvedWindows.Count);
        Assert.Equal("窗口未知", Assert.Single(QuotaCapsuleProjection.Create(snapshot).Rows).Label);
        Assert.Throws<AppServerProtocolException>(() => AppServerRateLimitsParser.Parse("{}", DateTimeOffset.UnixEpoch));
    }

    [Theory]
    [InlineData("{\"usedPercent\":42,\"windowDurationMins\":null,\"resetsAt\":null}")]
    [InlineData("{\"usedPercent\":42}")]
    public void Present_window_with_unknown_optional_fields_keeps_percentage(string partial)
    {
        var snapshot = Parse($"{{\"primary\":{partial},\"secondary\":null}}");
        var window = Assert.Single(snapshot.Windows); Assert.Null(window.WindowDurationMinutes); Assert.Null(window.ResetsAt);
        var row = Assert.Single(QuotaCapsuleProjection.Create(snapshot).Rows);
        Assert.Equal(58, row.RemainingPercent); Assert.Equal("时长未知", row.Label);
    }

    [Fact]
    public void Future_bucket_identity_is_its_map_key_not_a_blank_id_or_display_name()
    {
        var snapshot = AppServerRateLimitsParser.Parse("""
          {"rateLimitsByLimitId":{"codex":{"primary":null,"secondary":null},
            "future-a":{"limitId":" ","limitName":"同名","primary":{"usedPercent":90},"secondary":null},
            "future-b":{"limitId":"","limitName":"同名","primary":{"usedPercent":20},"secondary":null}}}
          """, DateTimeOffset.UnixEpoch);
        Assert.Equal(new[] { "future-a", "future-b" }, snapshot.Windows.Select(w => w.LimitId));
        Assert.Equal("无 Codex 窗口", Assert.Single(QuotaCapsuleProjection.Create(snapshot).Rows).Label);
    }

    [Fact]
    public void Cached_values_are_visibly_stale_not_current()
    {
        var snapshot = QuotaSnapshot.Stale([new(QuotaWindowKind.Secondary, 25, 10080, DateTimeOffset.UnixEpoch)],
            DateTimeOffset.UnixEpoch, "连接中断");
        Assert.EndsWith("缓存", Assert.Single(QuotaCapsuleProjection.Create(snapshot).Rows).Label);
        Assert.Equal("额度未知", Assert.Single(QuotaCapsuleProjection.Create(QuotaSnapshot.Unavailable("读取失败")).Rows).Label);
    }
}
