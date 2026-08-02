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

        if (selectedWindows.Count == 1 && selectedWindows[0].WindowDurationMinutes == 10080)
        {
            selected.Insert(0, selected[0] with { Label = "5 小时" });
        }

        if (selected.Count == 0)
        {
            selected.Add(new QuotaCapsuleRow("额度", "—", null, "Neutral", null));
        }

        return new QuotaCapsuleProjection(snapshot.Availability, snapshot.StatusText, selected);
    }

    private static QuotaCapsuleRow ToRow(QuotaWindowSnapshot window)
    {
        var label = window.WindowDurationMinutes switch
        {
            < 60 => $"{window.WindowDurationMinutes} 分钟",
            < 1440 when window.WindowDurationMinutes % 60 == 0 => $"{window.WindowDurationMinutes / 60} 小时",
            _ when window.WindowDurationMinutes % 1440 == 0 => $"{window.WindowDurationMinutes / 1440} 天",
            _ => $"{window.WindowDurationMinutes} 分钟",
        };
        var token = window.RemainingPercent switch
        {
            < 10 => "Critical",
            < 30 => "Amber",
            _ => "Cool",
        };

        return new QuotaCapsuleRow(label, $"{Math.Round(window.RemainingPercent):0}%", window.RemainingPercent, token, window.ResetsAt);
    }
}
