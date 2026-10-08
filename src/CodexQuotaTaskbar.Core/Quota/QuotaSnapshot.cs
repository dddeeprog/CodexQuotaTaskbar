namespace CodexQuotaTaskbar.Core.Quota;

public enum QuotaAvailability
{
    Available,
    Stale,
    Unavailable,
}

public sealed record QuotaSnapshot
{
    private QuotaSnapshot(
        IReadOnlyList<QuotaWindowSnapshot> windows,
        DateTimeOffset? capturedAt,
        QuotaAvailability availability,
        string statusText,
        string? subscriptionPlan,
        SubscriptionExpirationSnapshot subscriptionExpiration,
        QuotaCreditsSnapshot? extraCredits)
    {
        Windows = windows;
        CapturedAt = capturedAt;
        Availability = availability;
        StatusText = statusText;
        SubscriptionPlan = subscriptionPlan;
        SubscriptionExpiration = subscriptionExpiration;
        ExtraCredits = extraCredits;
    }

    public IReadOnlyList<QuotaWindowSnapshot> Windows { get; }
    public DateTimeOffset? CapturedAt { get; }
    public QuotaAvailability Availability { get; }
    public string StatusText { get; }
    public string? SubscriptionPlan { get; }
    public SubscriptionExpirationSnapshot SubscriptionExpiration { get; }
    public DateTimeOffset? MembershipExpiresAt => SubscriptionExpiration.ExpiresAt;
    public QuotaCreditsSnapshot? ExtraCredits { get; }

    public static QuotaSnapshot Available(
        IEnumerable<QuotaWindowSnapshot> windows,
        DateTimeOffset capturedAt,
        string? subscriptionPlan = null,
        SubscriptionExpirationSnapshot? subscriptionExpiration = null,
        QuotaCreditsSnapshot? extraCredits = null) =>
        new(
            windows.ToArray(),
            capturedAt,
            QuotaAvailability.Available,
            "额度已更新",
            subscriptionPlan,
            subscriptionExpiration ?? SubscriptionExpirationSnapshot.NotProvided(capturedAt, SubscriptionExpirationSource.None),
            extraCredits);

    public static QuotaSnapshot Stale(
        IEnumerable<QuotaWindowSnapshot> windows,
        DateTimeOffset capturedAt,
        string message,
        string? subscriptionPlan = null,
        SubscriptionExpirationSnapshot? subscriptionExpiration = null,
        QuotaCreditsSnapshot? extraCredits = null) =>
        new(
            windows.ToArray(),
            capturedAt,
            QuotaAvailability.Stale,
            message,
            subscriptionPlan,
            subscriptionExpiration ?? SubscriptionExpirationSnapshot.NotProvided(capturedAt, SubscriptionExpirationSource.None),
            extraCredits);

    public static QuotaSnapshot Unavailable(string message) =>
        new(
            [],
            null,
            QuotaAvailability.Unavailable,
            message,
            null,
            SubscriptionExpirationSnapshot.Unavailable(DateTimeOffset.Now, SubscriptionExpirationSource.None),
            null);

    public QuotaSnapshot WithSubscriptionExpiration(SubscriptionExpirationSnapshot subscriptionExpiration)
    {
        ArgumentNullException.ThrowIfNull(subscriptionExpiration);
        return new(
            Windows,
            CapturedAt,
            Availability,
            StatusText,
            SubscriptionPlan,
            subscriptionExpiration,
            ExtraCredits);
    }
}
