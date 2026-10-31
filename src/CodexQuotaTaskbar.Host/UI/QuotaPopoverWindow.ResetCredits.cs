using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CodexQuotaTaskbar.Core.Quota;

namespace CodexQuotaTaskbar.Host.UI;

public partial class QuotaPopoverWindow
{
    // Account reset credits use the existing account-bound quota read, never the
    // public reset announcement or its locked deadline. No redemption action here.
    internal void RenderResetCredits(DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var cards = quotaSnapshot.ResetCredits;
        var stale = cards.IsStale || quotaSnapshot.Availability == QuotaAvailability.Stale;
        var expiredDetails = cards.Credits?.Any(card => card.ResetType == "codexRateLimits"
            && card.Status == "available" && card.ExpiresAt <= now) == true;
        ResetCreditsRows.Children.Clear();
        ResetCreditsCountText.Text = cards.AvailableCount is { } count
            ? $"{count.ToString(CultureInfo.InvariantCulture)} 张{(stale ? "（缓存）" : expiredDetails ? "（读取时）" : "可用")}" : "未知";
        ResetCreditsStatusText.Text = cards.AvailableCount is null
            ? quotaSnapshot.Availability == QuotaAvailability.Unavailable
                ? "暂时无法读取重置卡信息" : "账号接口暂未提供重置卡信息"
            : cards.AvailableCount == 0 ? stale ? "上次读取时没有可用重置卡" : "当前没有可用重置卡"
            : cards.Credits is null ? "有效期明细未提供" : string.Empty;

        if (cards.AvailableCount is > 0 && cards.Credits is { } details)
        {
            var available = details.Where(card => card.ResetType == "codexRateLimits" && card.Status == "available"
                && (card.ExpiresAt is null || card.ExpiresAt > now)).ToArray();
            foreach (var group in available.OrderBy(card => card.ExpiresAt ?? DateTimeOffset.MaxValue)
                .GroupBy(card => card.ExpiresAt is { } expiry
                    ? $"{ResetCreditLocalTime(expiry, zone)} 到期"
                    : card.HasExpiryInformation ? "未设置到期时间" : "有效期未提供"))
            {
                ResetCreditsRows.Children.Add(new TextBlock
                {
                    Text = $"{group.Key} · {group.Count()} 张",
                    ToolTip = string.Join("\n", group.Select(card => card.ExpiresAt is { } expiry
                        ? $"{TimeZoneInfo.ConvertTime(expiry, zone):yyyy年M月d日 HH:mm:ss zzz}（{zone.DisplayName}）"
                        : card.HasExpiryInformation ? "账号接口未设置到期时间" : "账号接口未提供有效期").Distinct()),
                    FontSize = 11, Tag = "secondary", TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 3),
                });
            }
            var notes = new List<string>();
            if (expiredDetails)
                notes.Add("部分明细已到期，数量需刷新确认");
            else if (available.LongLength < cards.AvailableCount)
                notes.Add("有效期明细不全，数量以账号接口为准");
            if (details.Any(card => card.ExpiresAt <= now || card.Status != "available" || card.ResetType != "codexRateLimits"))
                notes.Add("已到期、已用或状态不明的明细未列为可用");
            if (available.LongLength > cards.AvailableCount)
            {
                ResetCreditsRows.Children.Clear();
                notes.Add("数量与明细不一致，等待刷新");
            }
            ResetCreditsStatusText.Text = string.Join("\n", notes);
        }
        if (stale && cards.AvailableCount is not null)
            ResetCreditsStatusText.Text = "更新失败，显示上次读取；当前数量及有效期请刷新确认"
                + (ResetCreditsStatusText.Text.Length > 0 ? "\n" + ResetCreditsStatusText.Text : string.Empty);
        ResetCreditsStatusText.Visibility = ResetCreditsStatusText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ResetCreditsSourceText.Text = cards.CapturedAt is { } captured
            ? $"账号额度接口 · 更新 {ResetCreditLocalTime(captured, zone)}" : "账号额度接口";
        ResetCreditsSourceText.ToolTip = cards.CapturedAt is { } timestamp
            ? $"{TimeZoneInfo.ConvertTime(timestamp, zone):yyyy年M月d日 HH:mm:ss}（{zone.DisplayName}）" : "重置卡信息未提供；未知不代表0张";
    }

    private static string ResetCreditLocalTime(DateTimeOffset value, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(value, zone);
        return $"{local.ToString("M月d日", CultureInfo.GetCultureInfo("zh-CN"))}周{"日一二三四五六"[(int)local.DayOfWeek]} {local.ToString("HH:mm", CultureInfo.InvariantCulture)}";
    }
}
