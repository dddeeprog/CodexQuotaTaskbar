namespace CodexQuotaTaskbar.Host.Resets;

// A public announcement held within one verified running context, not an account/window reset cycle.
// Nothing here is persisted or restored after loss of that context.
internal sealed class ResetNoticeLatch
{
    private ResetScheduleSnapshot latest = ResetScheduleSnapshot.Loading;
    private ResetScheduleSnapshot? locked;
    private DateTimeOffset? consumedAt;
    private DateTimeOffset? lastChecked;
    private DateTimeOffset? sourceWatermark;
    private bool contextAvailable;

    internal ResetSchedule? LockedNotice => locked?.Schedule;

    internal void ResetContext()
    {
        latest = ResetScheduleSnapshot.Loading; locked = null; consumedAt = null;
        lastChecked = sourceWatermark = null; contextAvailable = false;
    }
    internal void EstablishContext() => contextAvailable = true;

    internal void Update(ResetScheduleSnapshot snapshot, DateTimeOffset now)
    {
        if (!IsFresh(snapshot, now))
        {
            if (locked is null && consumedAt is null) latest = snapshot;
            return;
        }
        var checkedAt = snapshot.LastUpdatedAt!.Value;
        if (lastChecked is { } prior && checkedAt <= prior
            || snapshot.SourceGeneratedAt is { } generated && sourceWatermark is { } watermark && generated < watermark)
            return;
        lastChecked = checkedAt;
        if (snapshot.SourceGeneratedAt is { } source) sourceWatermark = source;
        latest = snapshot;
        if (!contextAvailable || locked is not null) return;
        if (snapshot.Schedule is not { ScheduledFor: { } due } notice || due <= now || notice.AnnouncedAt > now
            || due < notice.AnnouncedAt) return;
        // ID churn, revalidation time and deadline drift alone cannot resurrect a consumed announcement.
        // A clearly later publication plus a future absolute time is evidence of a new PUBLIC notice only.
        if (consumedAt is { } consumed && notice.AnnouncedAt <= consumed) return;
        locked = snapshot with { IsLatchedSchedule = true, IsTimingUnknown = false };
    }

    internal ResetScheduleSnapshot Project(DateTimeOffset now)
    {
        if (locked is { } value) return value;
        if (consumedAt is not null) return new(null, latest.LastUpdatedAt, false, null);
        return latest.Schedule is not null
            ? latest with { IsTimingUnknown = true, IsLatchedSchedule = false }
            : latest;
    }

    internal void Consume(DateTimeOffset now)
    {
        locked = null; consumedAt = now;
    }

    private static bool IsFresh(ResetScheduleSnapshot value, DateTimeOffset now) =>
        !value.IsLoading && !value.IsStale && string.IsNullOrEmpty(value.ErrorMessage)
        && value.LastUpdatedAt is { } checkedAt && checkedAt <= now.AddMinutes(1)
        && now - checkedAt < ResetScheduleClient.FreshnessLifetime
        && (value.SourceGeneratedAt is not { } generated || generated <= now.AddMinutes(5));
}
