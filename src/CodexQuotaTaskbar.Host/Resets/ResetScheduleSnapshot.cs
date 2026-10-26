namespace CodexQuotaTaskbar.Host.Resets;

// A third-party announcement awaiting execution evidence, never an account quota reset.
public sealed record ResetSchedule(
    string Id,
    ResetKind Kind,
    DateTimeOffset AnnouncedAt,
    DateTimeOffset? ScheduledFor,
    Uri SourceUri);

public sealed record ResetScheduleSnapshot(
    ResetSchedule? Schedule,
    DateTimeOffset? LastUpdatedAt,
    bool IsStale,
    string? ErrorMessage,
    bool IsLoading = false)
{
    public DateTimeOffset? SourceGeneratedAt { get; init; }

    public static ResetScheduleSnapshot Loading { get; } = new(null, null, false, null, true);

    public static ResetScheduleSnapshot Unavailable(string message = "暂时无法获取重置预告") =>
        new(null, null, false, message);
}
