namespace CodexQuotaTaskbar.Core.Quota;

public enum QuotaWindowKind
{
    Primary,
    Secondary,
    Other,
}

public sealed record QuotaWindowSnapshot
{
    public QuotaWindowSnapshot(
        QuotaWindowKind kind,
        double remainingPercent,
        int? windowDurationMinutes,
        DateTimeOffset? resetsAt,
        string limitId = "codex",
        string? limitName = null)
    {
        if (!double.IsFinite(remainingPercent) || remainingPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(remainingPercent));
        }

        if (windowDurationMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowDurationMinutes));
        }

        Kind = kind;
        RemainingPercent = remainingPercent;
        WindowDurationMinutes = windowDurationMinutes;
        ResetsAt = resetsAt;
        LimitId = string.IsNullOrWhiteSpace(limitId) ? "unknown" : limitId;
        LimitName = string.IsNullOrWhiteSpace(limitName) ? null : limitName;
    }

    public QuotaWindowKind Kind { get; }
    public double RemainingPercent { get; }
    public int? WindowDurationMinutes { get; }
    public DateTimeOffset? ResetsAt { get; }
    public string LimitId { get; }
    public string? LimitName { get; }

    public string DurationLabel => WindowDurationMinutes switch
    {
        null => "时长未知",
        var minutes when minutes % 1440 == 0 => $"{minutes / 1440} 天",
        var minutes when minutes % 60 == 0 => $"{minutes / 60} 小时",
        var minutes => $"{minutes} 分钟",
    };
}
