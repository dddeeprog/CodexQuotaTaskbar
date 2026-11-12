using System.Globalization;
using CodexQuotaTaskbar.Host.Resets;

namespace CodexQuotaTaskbar.Host.UI;

internal sealed record ResetScheduleRowPresentation(bool IsVisible, string Countdown, string LocalTime, string Tooltip)
{
    internal bool IsTimingKnown { get; init; } = true;
    internal string Deadline { get; init; } = string.Empty;
    internal string Title { get; init; } = "即将重置";
    private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");
    private static readonly ResetScheduleRowPresentation Hidden = new(false, string.Empty, string.Empty, string.Empty);

    internal static ResetScheduleRowPresentation Create(ResetScheduleSnapshot snapshot, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        if (snapshot.Schedule is null && snapshot.ActiveWatch is { } watch)
        {
            if (snapshot.IsLoading || snapshot.IsStale || !string.IsNullOrEmpty(snapshot.ErrorMessage)
                || snapshot.LastUpdatedAt is not { } watchCheckedAt || now - watchCheckedAt >= ResetScheduleClient.FreshnessLifetime
                || watchCheckedAt > now.AddMinutes(1) || watch.ExpiresAt <= now) return Hidden;
            var watchLocal = TimeZoneInfo.ConvertTime(watch.ExpiresAt, zone ?? TimeZoneInfo.Local);
            var window = $"观察至{watchLocal.ToString("M月d日 HH:mm", Chinese)}";
            return new(true, $"可能重置 {window}", string.Empty,
                $"Codex Resets 第三方观察信号，估计概率 {watch.ChancePercent}%；并非确定预告，也不代表本账号额度已恢复。\n观察截止：{watchLocal:yyyy-MM-dd HH:mm}（本地）\n来源：{watch.SourceUri}")
                { Title = "可能重置", Deadline = window };
        }
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
        var localTime = "本地 " + local.ToString("yyyy-MM-dd HH:mm", Chinese);
        // The locked estimate remains directly readable even after its deadline.
        var checkedText = snapshot.LastUpdatedAt is { } check ? TimeZoneInfo.ConvertTime(check, zone).ToString("M月d日 HH:mm:ss", Chinese) : "暂未知";
        var tooltip = $"Codex Resets 第三方公告预告，并非本账号额度到账确认。\n{(snapshot.IsLatchedSchedule ? "首次锁定预计时间" : "预计最晚")}：{local.ToString("yyyy年M月d日 HH:mm", Chinese)}（本地 {zone.DisplayName}）\n数据检查：{checkedText}\n来源：{CodexResetFeedClient.SiteUri}\n公告：{snapshot.Schedule!.SourceUri}";
        var overdue = snapshot.IsLatchedSchedule && deadline <= now;
        var deadlineText = overdue ? string.Empty : $"最晚{local.ToString("M月d日", Chinese)}周{"日一二三四五六"[(int)local.DayOfWeek]} {local.ToString("HH:mm", Chinese)}";
        return new(true, overdue ? "预计时间已到，等待额度恢复" : $"即将重置 {deadlineText}", localTime, tooltip)
            { Deadline = deadlineText };
    }
}
