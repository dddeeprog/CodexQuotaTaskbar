namespace CodexQuotaTaskbar.Core.Quota;

public sealed record QuotaCapsuleRow(string Label, string Text, double? RemainingPercent, string ColorToken, DateTimeOffset? ResetsAt);

public sealed record QuotaCapsuleProjection(QuotaAvailability Availability, string StatusText, IReadOnlyList<QuotaCapsuleRow> Rows)
{
    public static QuotaCapsuleProjection Create(QuotaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var selectedWindows = snapshot.Windows
            .Where(window => string.Equals(window.LimitId, "codex", StringComparison.OrdinalIgnoreCase)
                             && window.Kind is QuotaWindowKind.Primary or QuotaWindowKind.Secondary)
            .OrderBy(window => window.WindowDurationMinutes)
            .ThenBy(window => window.Kind)
            .Take(2)
            .ToList();
        var selected = selectedWindows.Select(ToRow).ToList();

        if (snapshot.UnresolvedWindows.Any(window => string.Equals(window.LimitId, "codex", StringComparison.OrdinalIgnoreCase)))
        {
            selected.Add(new QuotaCapsuleRow("窗口未知", "—", null, "Neutral", null));
        }

        if (selected.Count == 0)
        {
            selected.Add(new QuotaCapsuleRow(snapshot.Availability == QuotaAvailability.Available ? "无 Codex 窗口" : "额度未知", "—", null, "Neutral", null));
        }
        if (snapshot.Availability == QuotaAvailability.Stale)
            selected = selected.Select(row => row with { Label = row.Label + " · 缓存" }).ToList();
        return new QuotaCapsuleProjection(snapshot.Availability, snapshot.StatusText, selected);
    }

    private static QuotaCapsuleRow ToRow(QuotaWindowSnapshot window)
    {
        var label = window.DurationLabel;
        var token = window.RemainingPercent switch
        {
            < 10 => "Critical",
            < 30 => "Amber",
            _ => "Cool",
        };

        return new QuotaCapsuleRow(label, $"{Math.Round(window.RemainingPercent):0}%", window.RemainingPercent, token, window.ResetsAt);
    }
}
