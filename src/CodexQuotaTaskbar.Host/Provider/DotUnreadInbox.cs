namespace CodexQuotaTaskbar.Host.Provider;

internal sealed record DotUnreadItem(string Id, string Text, DateTimeOffset? Time, bool IsUnread);
internal sealed record DotUnreadSnapshot(int Schema, DotUnreadItem[] Messages, string[] KnownIds, int EarlierUnreadCount,
    string[]? EarlierUnreadIds = null);

// A receiver-owned, ephemeral target. It contains no text and is never persisted.
internal sealed record DotUnreadAcknowledgement(string[] MessageIds, int AnonymousEarlierCount, long ReadVersion);

// Presentation is deliberately absent here. Showing or timing out a bubble never acknowledges a message.
internal sealed class DotUnreadInbox
{
    internal const int MaximumMessages = 64;
    internal const int MaximumKnownIds = 128;
    internal const int MaximumTextCharacters = 8000;
    internal const int MaximumIdCharacters = 256;
    internal const int MaximumEarlierUnreadIds = 128;
    private readonly List<DotUnreadItem> messages = [];
    private readonly HashSet<string> known = new(StringComparer.Ordinal);
    private readonly Queue<string> knownOrder = new();
    private readonly HashSet<string> earlierUnreadIds = new(StringComparer.Ordinal);
    private readonly Queue<string> earlierOrder = new();
    private readonly object gate = new();
    private int earlierUnreadCount;
    private long readVersion;

    internal IReadOnlyList<DotUnreadItem> Messages { get { lock (gate) return messages.ToArray(); } }
    internal int EarlierUnreadCount { get { lock (gate) return earlierUnreadCount; } }
    internal int UnreadCount { get { lock (gate) return (int)Math.Min(int.MaxValue,
        (long)earlierUnreadCount + messages.Count(message => message.IsUnread)); } }

    // Call only after the receiver's baseline/tracker has identified a notification.
    // A repeated stable ID may refresh its streaming text, but cannot notify or become unread again.
    internal bool ObserveNotification(DotMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!ValidId(message.Id) || message.Role != "assistant" || string.IsNullOrWhiteSpace(message.Text)) return false;
        var text = message.Text;
        if (text.Length > MaximumTextCharacters)
        {
            var length = MaximumTextCharacters - 1;
            if (char.IsHighSurrogate(text[length - 1])) length--;
            text = text[..length] + "…";
        }
        lock (gate)
        {
            var index = messages.FindIndex(item => item.Id == message.Id);
            if (earlierUnreadIds.Contains(message.Id) || !known.Add(message.Id))
            {
                if (index >= 0)
                {
                    var prior = messages[index];
                    messages[index] = prior with { Text = text, Time = message.Time ?? prior.Time };
                }
                return false;
            }
            knownOrder.Enqueue(message.Id);
            while (knownOrder.Count > MaximumKnownIds) known.Remove(knownOrder.Dequeue());
            messages.Add(new(message.Id, text, message.Time, true));
            if (messages.Count > MaximumMessages)
            {
                if (messages[0].IsUnread && earlierUnreadCount < int.MaxValue - MaximumMessages)
                {
                    earlierUnreadCount++;
                    earlierUnreadIds.Add(messages[0].Id); earlierOrder.Enqueue(messages[0].Id);
                    while (earlierOrder.Count > MaximumEarlierUnreadIds) earlierUnreadIds.Remove(earlierOrder.Dequeue());
                }
                messages.RemoveAt(0);
            }
            return true;
        }
    }

    internal bool MarkRead(string id)
    {
        lock (gate)
        {
            var index = messages.FindIndex(item => item.Id == id);
            if (index < 0)
            {
                if (!earlierUnreadIds.Remove(id)) return false;
                earlierUnreadCount--; readVersion++;
                return true;
            }
            if (!messages[index].IsUnread) return false;
            messages[index] = messages[index] with { IsUnread = false }; readVersion++;
            return true;
        }
    }

    internal DotUnreadAcknowledgement CaptureAcknowledgement()
    {
        lock (gate) return new(messages.Where(item => item.IsUnread).Select(item => item.Id)
            .Concat(earlierOrder.Where(earlierUnreadIds.Contains)).Distinct(StringComparer.Ordinal).ToArray(),
            earlierUnreadCount - earlierUnreadIds.Count, readVersion);
    }

    // Explicit acknowledgement of the issued target only. Later arrivals do not
    // enter this set. If another reader changed anonymous history, leave that
    // ambiguous remainder unread instead of subtracting from newer messages.
    internal bool Acknowledge(DotUnreadAcknowledgement target, string? singleId = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (gate)
        {
            if (singleId is not null && !target.MessageIds.Contains(singleId, StringComparer.Ordinal)) return false;
            string[] ids = singleId is null ? target.MessageIds : [singleId];
            var missing = 0; var changed = false;
            foreach (var id in ids)
            {
                var index = messages.FindIndex(item => item.Id == id);
                if (index >= 0)
                {
                    if (!messages[index].IsUnread) continue;
                    messages[index] = messages[index] with { IsUnread = false }; changed = true;
                }
                else if (earlierUnreadIds.Remove(id)) { earlierUnreadCount--; changed = true; }
                else missing++;
            }
            if (readVersion == target.ReadVersion)
            {
                var anonymous = earlierUnreadCount - earlierUnreadIds.Count;
                var budget = (long)missing + (singleId is null ? target.AnonymousEarlierCount : 0);
                var acknowledged = (int)Math.Min(anonymous, budget);
                if (acknowledged > 0) { earlierUnreadCount -= acknowledged; changed = true; }
            }
            if (changed) readVersion++;
            return changed;
        }
    }

    // Only call after a separate, explicit user acknowledgement of that many earlier messages.
    // Bubble timers, page presentation, and ordinary MarkRead must never call this operation.
    internal bool AcknowledgeEarlier(int count)
    {
        lock (gate)
        {
            if (count <= 0 || count > earlierUnreadCount) return false;
            var knownToAcknowledge = Math.Max(0, count - (earlierUnreadCount - earlierUnreadIds.Count));
            while (knownToAcknowledge > 0 && earlierOrder.Count > 0)
                if (earlierUnreadIds.Remove(earlierOrder.Dequeue())) knownToAcknowledge--;
            earlierUnreadCount -= count; readVersion++;
            return true;
        }
    }

    internal DotUnreadSnapshot Capture()
    {
        lock (gate) return new(1, messages.ToArray(), knownOrder.ToArray(), earlierUnreadCount,
            earlierOrder.Where(earlierUnreadIds.Contains).ToArray());
    }

    internal void Restore(DotUnreadSnapshot snapshot)
    {
        if (!IsValid(snapshot)) throw new ArgumentException("Invalid unread snapshot.", nameof(snapshot));
        lock (gate)
        {
            messages.Clear(); messages.AddRange(snapshot.Messages);
            known.Clear(); knownOrder.Clear();
            foreach (var id in snapshot.KnownIds) { known.Add(id); knownOrder.Enqueue(id); }
            earlierUnreadCount = snapshot.EarlierUnreadCount;
            readVersion++;
            earlierUnreadIds.Clear(); earlierOrder.Clear();
            foreach (var id in snapshot.EarlierUnreadIds ?? []) { earlierUnreadIds.Add(id); earlierOrder.Enqueue(id); }
        }
    }

    internal static bool IsValid(DotUnreadSnapshot? snapshot)
    {
        if (snapshot is not { Schema: 1, Messages: not null, KnownIds: not null }
            || snapshot.Messages.Length > MaximumMessages || snapshot.KnownIds.Length > MaximumKnownIds
            || snapshot.EarlierUnreadCount is < 0 or > int.MaxValue - MaximumMessages
            || snapshot.EarlierUnreadIds is { Length: > MaximumEarlierUnreadIds }
            || (snapshot.EarlierUnreadIds?.Length ?? 0) > snapshot.EarlierUnreadCount) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in snapshot.KnownIds) if (!ValidId(id) || !ids.Add(id)) return false;
        var messageIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in snapshot.Messages)
            if (item is null || !ValidId(item.Id) || !ids.Contains(item.Id) || !messageIds.Add(item.Id)
                || string.IsNullOrWhiteSpace(item.Text) || item.Text.Length > MaximumTextCharacters) return false;
        var earlierIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in snapshot.EarlierUnreadIds ?? [])
            if (!ValidId(id) || messageIds.Contains(id) || !earlierIds.Add(id)) return false;
        return true;
    }

    private static bool ValidId(string? id) => !string.IsNullOrWhiteSpace(id)
        && id.Length <= MaximumIdCharacters && !id.Any(char.IsControl);
}
