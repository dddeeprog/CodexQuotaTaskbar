using System.Security.Cryptography;
using System.Text;
using CodexQuotaTaskbar.Core.Quota;

namespace CodexQuotaTaskbar.Host.Resets;

internal enum ResetVisualState { None, Upcoming, Confirmed, Checking, Failed }

// Observed quota recovery, NOT a server reset-completed notification.
internal sealed record ResetConfirmation(string ScopeId, string WindowId, string EventId,
    DateTimeOffset OccurredAt, DateTimeOffset ExpiresAt, DateTimeOffset SampledAt);
internal sealed record ConsumedResetNotice(string ScopeId, string NoticeId, DateTimeOffset ConsumedAt);

internal sealed class QuotaResetTracker
{
    internal static readonly TimeSpan ConfirmationLifetime = TimeSpan.FromHours(1);
    internal static readonly TimeSpan MaximumEvidenceGap = TimeSpan.FromMinutes(10);
    private readonly object gate = new();
    private readonly ResetConfirmationStore? store;
    private readonly List<ResetConfirmation> confirmations;
    private readonly List<ConsumedResetNotice> consumedNotices;
    private string? currentNotice;
    private ResetConfirmation[]? pendingConfirmations;
    private ConsumedResetNotice[]? pendingNotices;
    private QuotaSnapshot? previous;
    private string? activeScope;
    private DateTimeOffset? highWater;
    private readonly ResetNoticeLatch noticeLatch = new();
    private bool usesLatchedSchedule;
    private DateTimeOffset? pendingNoticeConsumption;
    internal bool UsesLatchedSchedule { get { lock (gate) return usesLatchedSchedule; } }

    internal QuotaResetTracker(ResetConfirmationStore? store = null)
    {
        this.store = store;
        confirmations = store?.Load().ToList() ?? [];
        consumedNotices = store?.LoadConsumedNotices().ToList() ?? [];
    }

    // Caller validates either a response-bound identity or a continuous transport/context.
    // Hash only the scope; never persist raw identity, email, plan or credentials.
    internal static string? ScopeForSource(string? source) =>
        !string.IsNullOrWhiteSpace(source) && source.Length <= 1024
            ? Hash($"quota-recovery-scope\n{source}") : null;

    internal static string? NoticeId(ResetSchedule? schedule) => schedule is { Id.Length: > 0 }
        ? Hash($"reset-notice\n{schedule.Id}") : null;

    internal void SetNotice(ResetSchedule? schedule) { lock (gate) currentNotice = NoticeId(schedule); }
    // Full freshness metadata is required for the public latch. Bare SetNotice remains ID-only compatibility.
    internal void SetSchedule(ResetScheduleSnapshot value, DateTimeOffset now)
    {
        lock (gate)
        {
            usesLatchedSchedule = true;
            noticeLatch.Update(value, now);
            currentNotice = NoticeId(noticeLatch.LockedNotice);
        }
    }
    internal ResetScheduleSnapshot ProjectSchedule(ResetScheduleSnapshot value, DateTimeOffset now)
    { lock (gate) return usesLatchedSchedule ? noticeLatch.Project(now) : value; }
    internal bool IsNoticeConsumed(ResetSchedule? schedule)
    {
        lock (gate) return NoticeId(schedule) is { } id
            // Public announcements are dismissed on this device, not account-specific.
            // This survives a new connection scope without restoring another scope's green.
            && (consumedNotices.Any(item => item.NoticeId == id)
                // Late delivery (including after expiry/restart) does not resurrect a
                // notice that was already published at the accepted recovery time.
                || confirmations.Any(item => schedule!.AnnouncedAt <= item.OccurredAt));
    }

    internal DateTimeOffset? Observe(QuotaSnapshot sample, string? scope, DateTimeOffset now)
    {
        lock (gate)
        {
            if (!IsDigest(scope))
            {
                activeScope = null; previous = null; highWater = null;
                pendingConfirmations = null; pendingNotices = null;
                noticeLatch.ResetContext(); pendingNoticeConsumption = null;
                if (usesLatchedSchedule) currentNotice = null;
                return null;
            }
            if (activeScope is not null && activeScope != scope)
            {
                activeScope = null; previous = null; highWater = null;
                pendingConfirmations = null; pendingNotices = null;
                noticeLatch.ResetContext(); pendingNoticeConsumption = null;
                if (usesLatchedSchedule) currentNotice = null;
            }
            if (sample.Availability != QuotaAvailability.Available || sample.CapturedAt is not { } captured
                || captured > now || now - captured > TimeSpan.FromSeconds(30))
            {
                previous = null;
                return activeScope == scope ? ActiveUntilCore(now) : null;
            }
            // An unrelated future meter must not disable known Codex/Spark windows.
            // Unknown/absent slots have no baseline. Other fully known windows
            // can still recover; never substitute a different slot or meter.
            var windows = sample.Windows.Where(IsSupportedWindow).ToArray();
            var valid = windows.Length is > 0 and <= 16
                && windows.All(IsSupportedWindow)
                && windows.Select(WindowKey).Distinct(StringComparer.Ordinal).Count() == windows.Length;
            if (!valid) { previous = null; return activeScope == scope ? ActiveUntilCore(now) : null; }
            if (activeScope != scope)
            {
                activeScope = scope; previous = null; highWater = null;
                pendingConfirmations = null; pendingNotices = null;
                noticeLatch.ResetContext(); noticeLatch.EstablishContext(); pendingNoticeConsumption = null;
                if (usesLatchedSchedule) currentNotice = null;
            }
            CommitPending(); // Retry the original event/time, never restart its hour.
            // Persisted source watermark rejects replay before/at the last accepted recovery, even after restart.
            var recordedSample = confirmations.Where(item => item.ScopeId == scope)
                .Select(item => (DateTimeOffset?)item.SampledAt).Max();
            if (recordedSample is { } recorded && captured <= recorded) return ActiveUntilCore(now);
            // Metadata-only refreshes, repeated reads and out-of-order samples cannot create transitions.
            if (highWater is { } last && captured <= last) return ActiveUntilCore(now);
            highWater = captured;

            var next = (pendingConfirmations ?? confirmations.ToArray()).ToList();
            var originalCount = next.Count;
            var episodeUntil = usesLatchedSchedule ? ActiveUntilCore(now) : null;
            var recovered = false;
            if (previous is { CapturedAt: { } before } prior
                && captured - before <= MaximumEvidenceGap)
            {
                // A changed/shorter next reset deadline does not invalidate a real rise.
                foreach (var window in windows)
                {
                    var old = prior.Windows.FirstOrDefault(item => WindowKey(item) == WindowKey(window));
                    if (old is null || window.RemainingPercent <= old.RemainingPercent)
                        continue;
                    recovered = true;
                    // All accepted changes within the active modern episode share its FIRST one-hour expiry.
                    if (episodeUntil is not null) continue;
                    var windowId = Hash(WindowKey(window));
                    var id = Hash($"{scope}\n{windowId}\n{captured.UtcTicks}");
                    if (next.Any(item => item.EventId == id)) continue;
                    next.Add(new(scope!, windowId, id, now, now + ConfirmationLifetime, captured));
                }
            }
            previous = QuotaSnapshot.Available(windows, captured);
            if (next.Count != originalCount || recovered && usesLatchedSchedule)
            {
                var bounded = next.OrderByDescending(item => item.OccurredAt).ThenBy(item => item.EventId, StringComparer.Ordinal)
                    .Take(32).ToArray();
                var notices = (pendingNotices ?? consumedNotices.ToArray()).ToList();
                if (currentNotice is { } notice && !notices.Any(item => item.NoticeId == notice))
                    notices.Add(new(scope!, notice, now));
                // Bounded explicit IDs; old immutable publication times remain covered
                // by the latest persisted recovery cutoff, so capacity cannot block green forever.
                pendingConfirmations = bounded;
                pendingNotices = notices.OrderByDescending(item => item.ConsumedAt).Take(256).ToArray();
                if (usesLatchedSchedule) pendingNoticeConsumption = now;
                CommitPending();
            }
            return ActiveUntilCore(now);
        }
    }

    private void CommitPending()
    {
        if (pendingConfirmations is not { } events || pendingNotices is not { } notices) return;
        if (store is not null && !store.TrySave(events, notices)) return;
        confirmations.Clear(); confirmations.AddRange(events);
        consumedNotices.Clear(); consumedNotices.AddRange(notices);
        pendingConfirmations = null; pendingNotices = null;
        if (pendingNoticeConsumption is { } consumed)
        {
            noticeLatch.Consume(consumed); currentNotice = null; pendingNoticeConsumption = null;
        }
    }

    internal void InvalidateSample(bool accountChanged = false)
    {
        lock (gate)
        {
            previous = null;
            if (accountChanged)
            {
                activeScope = null; highWater = null;
                pendingConfirmations = null; pendingNotices = null;
                noticeLatch.ResetContext(); pendingNoticeConsumption = null;
                if (usesLatchedSchedule) currentNotice = null;
            }
        }
    }

    internal DateTimeOffset? ActiveUntil(DateTimeOffset now) { lock (gate) return ActiveUntilCore(now); }

    private DateTimeOffset? ActiveUntilCore(DateTimeOffset now) => confirmations
        .Where(item => item.ScopeId == activeScope && item.OccurredAt <= now && now < item.ExpiresAt)
        .Select(item => (DateTimeOffset?)item.ExpiresAt).Max();

    internal static ResetVisualState Select(bool upcomingVisible, DateTimeOffset? confirmedUntil, DateTimeOffset now) =>
        confirmedUntil is { } until && now < until && until <= now + ConfirmationLifetime
            ? ResetVisualState.Confirmed : upcomingVisible ? ResetVisualState.Upcoming : ResetVisualState.None;

    private static bool IsSupportedWindow(QuotaWindowSnapshot window) =>
        window.LimitId is "codex" or "codex_bengalfox"
        && window.Kind is QuotaWindowKind.Primary or QuotaWindowKind.Secondary
        && window.ResetsAt is not null
        && window.WindowDurationMinutes is > 0 and <= 44640;
    private static string WindowKey(QuotaWindowSnapshot window) => $"{window.LimitId}:{window.Kind}:{window.WindowDurationMinutes}";
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static bool IsDigest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}
