using System.Globalization;
using CodexQuotaTaskbar.Host.Resets;

namespace CodexQuotaTaskbar.Host.UI;

internal sealed record ResetScheduleRowPresentation(bool IsVisible, string Countdown, string LocalTime, string Tooltip)
{
    internal bool IsTimingKnown { get; init; } = true;
    private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");
    private static readonly ResetScheduleRowPresentation Hidden = new(false, string.Empty, string.Empty, string.Empty);

    internal static ResetScheduleRowPresentation Create(ResetScheduleSnapshot snapshot, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        if (snapshot.IsTimingUnknown)
            return new(true, "重置时间暂未知", string.Empty,
                "公共预告的时间或当前连续额度上下文尚未验证；不代表本账号已重置。") { IsTimingKnown = false };
        // A validated public latch survives later site status/clock jitter and transport cache expiry.
        // Unlatched/legacy callers retain the ordinary freshness contract.
        if (!snapshot.IsLatchedSchedule && (snapshot.IsLoading || snapshot.IsStale || !string.IsNullOrEmpty(snapshot.ErrorMessage)
            || snapshot.LastUpdatedAt is not { } checkedAt || now - checkedAt >= ResetScheduleClient.FreshnessLifetime
            || checkedAt > now.AddMinutes(1)) || snapshot.Schedule?.ScheduledFor is not { } deadline
            || !snapshot.IsLatchedSchedule && deadline <= now)
            return Hidden;

        zone ??= TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(deadline, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var day = (local.Date - today).Days;
        var localTime = day switch
        {
            0 => $"本地 {local:HH:mm}",
            1 => $"明日 {local:HH:mm}",
            _ => local.ToString("M/d HH:mm", Chinese),
        };
        var remaining = snapshot.IsLatchedSchedule ? deadline - now
            : TimeSpan.FromMinutes(Math.Max(1, Math.Ceiling((deadline - now).TotalMinutes)));
        var count = snapshot.IsLatchedSchedule
            ? remaining.TotalHours >= 1
                ? $"{Math.Round(remaining.TotalHours, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture)} 小时"
                : $"{Math.Max(1, Math.Ceiling(remaining.TotalMinutes)).ToString("0", CultureInfo.InvariantCulture)} 分钟"
            : remaining.TotalDays >= 2 ? $"{Math.Round(remaining.TotalDays, 1, MidpointRounding.AwayFromZero).ToString("0.#", CultureInfo.InvariantCulture)} 天"
            : remaining.TotalHours >= 1 ? $"{Math.Round(remaining.TotalHours, 1, MidpointRounding.AwayFromZero).ToString("0.#", CultureInfo.InvariantCulture)} 小时"
            : $"{remaining.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} 分钟";
        // Display changes are minute-grained at most; exact time and source stay out of the compact row.
        var checkedText = snapshot.LastUpdatedAt is { } check ? TimeZoneInfo.ConvertTime(check, zone).ToString("M月d日 HH:mm:ss", Chinese) : "暂未知";
        var tooltip = $"Codex Resets 第三方公告预告，并非本账号额度到账确认。\n{(snapshot.IsLatchedSchedule ? "首次锁定预计时间" : "预计最晚")}：{local.ToString("yyyy年M月d日 HH:mm", Chinese)}（本地 {zone.DisplayName}）\n数据检查：{checkedText}\n来源：{CodexResetFeedClient.SiteUri}\n公告：{snapshot.Schedule!.SourceUri}";
        return new(true, snapshot.IsLatchedSchedule && remaining <= TimeSpan.Zero
            ? "预计时间已到，等待额度恢复" : $"约 {count}后重置", snapshot.IsLatchedSchedule ? string.Empty : $" · {localTime}", tooltip);
    }
}
