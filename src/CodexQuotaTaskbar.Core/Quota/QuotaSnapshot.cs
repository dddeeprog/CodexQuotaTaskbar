namespace CodexQuotaTaskbar.Core.Quota;

public enum QuotaAvailability
{
    Available,
    Stale,
    Unavailable,
}

public sealed record QuotaSnapshot
{
    private QuotaSnapshot(IReadOnlyList<QuotaWindowSnapshot> windows, DateTimeOffset? capturedAt, QuotaAvailability availability, string statusText, string? subscriptionPlan)
    {
        Windows = windows;
        CapturedAt = capturedAt;
        Availability = availability;
        StatusText = statusText;
        SubscriptionPlan = subscriptionPlan;
    }

    public IReadOnlyList<QuotaWindowSnapshot> Windows { get; }
    public DateTimeOffset? CapturedAt { get; }
    public QuotaAvailability Availability { get; }
    public string StatusText { get; }
    public string? SubscriptionPlan { get; }

    public static QuotaSnapshot Available(IEnumerable<QuotaWindowSnapshot> windows, DateTimeOffset capturedAt, string? subscriptionPlan = null) =>
        new(windows.ToArray(), capturedAt, QuotaAvailability.Available, "额度已更新", subscriptionPlan);

    public static QuotaSnapshot Stale(IEnumerable<QuotaWindowSnapshot> windows, DateTimeOffset capturedAt, string message, string? subscriptionPlan = null) =>
        new(windows.ToArray(), capturedAt, QuotaAvailability.Stale, message, subscriptionPlan);

    public static QuotaSnapshot Unavailable(string message) =>
        new([], null, QuotaAvailability.Unavailable, message, null);
}
