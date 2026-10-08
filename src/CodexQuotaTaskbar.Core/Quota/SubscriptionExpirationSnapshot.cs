namespace CodexQuotaTaskbar.Core.Quota;

public enum SubscriptionExpirationAvailability
{
    Available,
    NotProvided,
    Unavailable,
    Disabled,
}

public enum SubscriptionExpirationSource
{
    None,
    CodexLogin,
    ChatGptSubscription,
    ChatGptWeb,
}

public sealed record SubscriptionExpirationSnapshot(
    SubscriptionExpirationAvailability Availability,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? CheckedAt,
    SubscriptionExpirationSource Source,
    string StatusText,
    bool IsStale = false)
{
    public static SubscriptionExpirationSnapshot Available(
        DateTimeOffset expiresAt,
        DateTimeOffset checkedAt,
        SubscriptionExpirationSource source) =>
        new(SubscriptionExpirationAvailability.Available, expiresAt, checkedAt, source, string.Empty);

    public static SubscriptionExpirationSnapshot NotProvided(
        DateTimeOffset checkedAt,
        SubscriptionExpirationSource source) =>
        new(SubscriptionExpirationAvailability.NotProvided, null, checkedAt, source, "未提供");

    public static SubscriptionExpirationSnapshot Unavailable(
        DateTimeOffset checkedAt,
        SubscriptionExpirationSource source,
        string statusText = "暂不可用") =>
        new(SubscriptionExpirationAvailability.Unavailable, null, checkedAt, source, statusText);

    public static SubscriptionExpirationSnapshot Disabled() =>
        new(SubscriptionExpirationAvailability.Disabled, null, null, SubscriptionExpirationSource.None, "增强读取已关闭");

    public SubscriptionExpirationSnapshot AsStale() =>
        Availability == SubscriptionExpirationAvailability.Available ? this with { IsStale = true } : this;
}
