namespace CodexQuotaTaskbar.Core.Quota;

public sealed record QuotaResetCredit(string ResetType, string Status, DateTimeOffset? ExpiresAt)
{
    public bool HasExpiryInformation { get; init; } = true;
}

public sealed record QuotaResetCreditsSnapshot(long? AvailableCount, IReadOnlyList<QuotaResetCredit>? Credits,
    DateTimeOffset? CapturedAt, bool IsStale = false)
{
    public static QuotaResetCreditsSnapshot Unknown { get; } = new(null, null, null);
}
