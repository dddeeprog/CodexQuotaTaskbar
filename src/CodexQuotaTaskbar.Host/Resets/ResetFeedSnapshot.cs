namespace CodexQuotaTaskbar.Host.Resets;

public enum ResetKind
{
    Regular,
    Banked,
}

// These are public announcements, never evidence of the current account's balance.
public sealed record ResetAnnouncement(
    string Id,
    ResetKind Kind,
    DateTimeOffset AnnouncedAt,
    string Text,
    Uri SourceUri,
    bool IsObserved)
{
    public string? ChineseText { get; init; }
}

public sealed record ResetFeedSnapshot(
    IReadOnlyList<ResetAnnouncement> Announcements,
    DateTimeOffset? LastUpdatedAt,
    bool IsStale,
    string? ErrorMessage,
    bool IsLoading = false)
{
    public ResetAnnouncement? Latest => Announcements.FirstOrDefault();

    public static ResetFeedSnapshot Loading { get; } = new([], null, false, null, true);

    public static ResetFeedSnapshot Unavailable(string message = "暂时无法获取重置公告") =>
        new([], null, false, message);
}
