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
        int windowDurationMinutes,
        DateTimeOffset resetsAt,
        string limitId = "codex")
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
        LimitId = string.IsNullOrWhiteSpace(limitId) ? "codex" : limitId;
    }

    public QuotaWindowKind Kind { get; }
    public double RemainingPercent { get; }
    public int WindowDurationMinutes { get; }
    public DateTimeOffset ResetsAt { get; }
    public string LimitId { get; }
}
