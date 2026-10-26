using System.Globalization;
using CodexQuotaTaskbar.Host.Resets;

namespace CodexQuotaTaskbar.Host.UI;

internal sealed record ResetScheduleRowPresentation(bool IsVisible, string Countdown, string LocalTime, string Tooltip)
{
    private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");
    private static readonly ResetScheduleRowPresentation Hidden = new(false, string.Empty, string.Empty, string.Empty);

    internal static ResetScheduleRowPresentation Create(ResetScheduleSnapshot snapshot, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        // Revalidation refreshes LastUpdatedAt, even when the source body keeps its original generation time.
        if (snapshot.IsLoading || snapshot.IsStale || !string.IsNullOrEmpty(snapshot.ErrorMessage)
            || snapshot.LastUpdatedAt is not { } checkedAt || now - checkedAt >= ResetScheduleClient.FreshnessLifetime
            || checkedAt > now.AddMinutes(1) || snapshot.Schedule?.ScheduledFor is not { } deadline || deadline <= now)
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
        var remaining = TimeSpan.FromMinutes(Math.Max(1, Math.Ceiling((deadline - now).TotalMinutes)));
        var count = remaining.TotalDays >= 2 ? $"{Math.Round(remaining.TotalDays, 1, MidpointRounding.AwayFromZero).ToString("0.#", CultureInfo.InvariantCulture)} 天"
            : remaining.TotalHours >= 1 ? $"{Math.Round(remaining.TotalHours, 1, MidpointRounding.AwayFromZero).ToString("0.#", CultureInfo.InvariantCulture)} 小时"
            : $"{remaining.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} 分钟";
        // Display changes are minute-grained at most; exact time and source stay out of the compact row.
        var tooltip = $"Codex Resets 第三方公告预告，并非本账号额度到账确认。\n预计最晚：{local.ToString("yyyy年M月d日 HH:mm", Chinese)}（本地 {zone.DisplayName}）\n数据检查：{TimeZoneInfo.ConvertTime(checkedAt, zone).ToString("M月d日 HH:mm:ss", Chinese)}\n来源：{CodexResetFeedClient.SiteUri}\n公告：{snapshot.Schedule!.SourceUri}";
        return new(true, $"约 {count}后重置", $" · {localTime}", tooltip);
    }
}
