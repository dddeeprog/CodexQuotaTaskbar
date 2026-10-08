using CodexQuotaTaskbar.Host.Resets;

namespace CodexQuotaTaskbar.Host.UI;

internal static class ResetFeedPresentation
{
    internal static string KindLabel(ResetKind kind) => kind == ResetKind.Banked ? "备用重置" : "常规重置";

    internal static string AnnouncementText(ResetAnnouncement announcement) => announcement.ChineseText is { Length: > 0 } chinese
        ? ResetAnnouncementLocalization.WithoutLinks(chinese)
        : "中文译文暂不可用，以下为原文：\n" + ResetAnnouncementLocalization.WithoutLinks(announcement.Text);

    internal static string IslandSummary(ResetFeedSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.Latest is not { } latest)
        {
            return snapshot.IsLoading ? "最近重置 · 正在读取…"
                : snapshot.ErrorMessage is not null ? "最近重置 · 暂不可用" : "最近重置 · 暂无公告";
        }
        var elapsed = now - latest.AnnouncedAt;
        var age = elapsed < TimeSpan.Zero ? "时间待确认"
            : elapsed.TotalMinutes < 1 ? "刚刚"
            : elapsed.TotalHours < 1 ? $"{(int)elapsed.TotalMinutes}分钟前"
            : elapsed.TotalDays < 1 ? $"{(int)elapsed.TotalHours}小时前"
            : $"{(int)elapsed.TotalDays}天前";
        var kind = latest.Kind == ResetKind.Banked ? "备用" : "常规";
        return $"最近重置 · {age} · {kind}{(snapshot.IsStale ? " · 缓存" : string.Empty)}";
    }

    internal static string Status(ResetFeedSnapshot snapshot) => snapshot.IsLoading ? "正在获取公开公告…"
        : snapshot.ErrorMessage is not null ? snapshot.IsStale ? "更新失败，以下为上次获取的公告" : "暂时无法获取，点击刷新可重试"
        : snapshot.Announcements.Count == 0 ? "暂无重置公告"
        : string.Empty;
}
