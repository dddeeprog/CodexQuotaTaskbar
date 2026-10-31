using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CodexQuotaTaskbar.Core.Quota;
using Button = System.Windows.Controls.Button;

namespace CodexQuotaTaskbar.Host.UI;

public partial class QuotaPopoverWindow
{
    // Account reset credits use the existing account-bound quota read, never the
    // public reset announcement or its locked deadline. Row actions only prepare
    // the existing confirmation; the server still selects which card to consume.
    internal void RenderResetCredits(DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var cards = quotaSnapshot.ResetCredits;
        var stale = cards.IsStale || quotaSnapshot.Availability == QuotaAvailability.Stale;
        var expiredDetails = cards.Credits?.Any(card => card.ResetType == "codexRateLimits"
            && card.Status == "available" && card.ExpiresAt <= now) == true;
        ResetCreditsRows.Children.Clear();
        resetCreditUseButtons.Clear();
        ResetCreditsCountText.Visibility = cards.AvailableCount == 0 && !stale ? Visibility.Collapsed : Visibility.Visible;
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
            foreach (var card in available.OrderBy(card => card.ExpiresAt ?? DateTimeOffset.MaxValue))
            {
                AddResetCreditRow(card.ExpiresAt is { } expiry ? $"{ResetCreditLocalTime(expiry, zone)} 到期"
                    : card.HasExpiryInformation ? "未设置到期时间" : "有效期未提供",
                    card.ExpiresAt is { } timestamp ? $"{TimeZoneInfo.ConvertTime(timestamp, zone):yyyy年M月d日 HH:mm:ss zzz}（{zone.DisplayName}）"
                    : card.HasExpiryInformation ? "账号接口未设置到期时间" : "账号接口未提供有效期");
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
                resetCreditUseButtons.Clear();
                notes.Add("数量与明细不一致，等待刷新");
            }
            else if (available.LongLength < cards.AvailableCount)
                AddResetCreditRow($"{(cards.AvailableCount.Value - available.LongLength).ToString(CultureInfo.InvariantCulture)} 张 · 有效期未提供");
            ResetCreditsStatusText.Text = string.Join("\n", notes);
        }
        else if (cards.AvailableCount is > 0)
        {
            // Do not fabricate per-card identities when only a total was returned.
            AddResetCreditRow($"{cards.AvailableCount.Value.ToString(CultureInfo.InvariantCulture)} 张 · 有效期未提供");
            ResetCreditsStatusText.Text = string.Empty;
        }
        if (stale && cards.AvailableCount is not null)
            ResetCreditsStatusText.Text = "更新失败，显示上次读取；当前数量及有效期请刷新确认"
                + (ResetCreditsStatusText.Text.Length > 0 ? "\n" + ResetCreditsStatusText.Text : string.Empty);
        ResetCreditsStatusText.Visibility = ResetCreditsStatusText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RenderResetCreditUse();
    }

    private void AddResetCreditRow(string label, string? tooltip = null)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock
        {
            Text = label, ToolTip = tooltip, FontSize = 11, Tag = "secondary", TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        });
        var button = new Button
        {
            Content = "使用重置卡", Style = (Style)FindResource("GlassButton"), Width = 88, Height = 28,
            FontSize = 11, IsEnabled = false, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "再次确认后使用1张；实际使用的卡由服务端选择",
        };
        Grid.SetColumn(button, 1);
        button.Click += async (_, _) => await PrepareResetCreditUseAsync(false);
        row.Children.Add(button);
        resetCreditUseButtons.Add(button);
        ResetCreditsRows.Children.Add(row);
    }

    private static string ResetCreditLocalTime(DateTimeOffset value, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(value, zone);
        return $"{local.ToString("M月d日", CultureInfo.GetCultureInfo("zh-CN"))}周{"日一二三四五六"[(int)local.DayOfWeek]} {local.ToString("HH:mm", CultureInfo.InvariantCulture)}";
    }
}
