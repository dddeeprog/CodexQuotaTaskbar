namespace CodexQuotaTaskbar.Host.Resets;

internal enum ResetQueryPhase { Idle, Checking, Failed }

// Transport health is separate from the last usable announcement. Revision also
// rejects dispatcher notifications superseded before the UI gets to them.
internal sealed record ResetQueryState(long Revision, ResetQueryPhase Phase, DateTimeOffset? StartedAt = null)
{
    internal static readonly ResetQueryState Idle = new(0, ResetQueryPhase.Idle);
    internal bool HasUnresolvedFailure { get; init; }
    internal ResetVisualState Select(bool noticeVisible, DateTimeOffset? confirmedUntil, DateTimeOffset now)
    {
        var baseline = QuotaResetTracker.Select(noticeVisible, confirmedUntil, now);
        if (baseline == ResetVisualState.Confirmed) return baseline;
        if (Phase == ResetQueryPhase.Failed || HasUnresolvedFailure) return ResetVisualState.Failed;
        // Checking remains transport state, not a visible blue-light mode.
        // A request must not interrupt the normal RGB base or a valid gold notice.
        return baseline;
    }
}

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
