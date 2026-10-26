namespace CodexQuotaTaskbar.Host.Provider;

// Tokens are issued by the receiver and stay in memory for this pipe session.
// The UI never chooses a count or supplies the set of messages to clear.
internal sealed class DotUnreadAcknowledgements(DotUnreadInbox inbox)
{
    internal const int MaximumSnapshots = 16;
    private sealed record Entry(DotUnreadAcknowledgement Target, long FirstSequence, long LastSequence);
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Queue<string> order = new();
    private string? latestToken;

    internal string? Issue(long sequence)
    {
        var target = inbox.CaptureAcknowledgement();
        if (target.MessageIds.Length == 0 && target.AnonymousEarlierCount == 0) { latestToken = null; return null; }
        if (latestToken is not null && entries.TryGetValue(latestToken, out var current)
            && current.Target.ReadVersion == target.ReadVersion
            && current.Target.AnonymousEarlierCount == target.AnonymousEarlierCount
            && current.Target.MessageIds.SequenceEqual(target.MessageIds))
        {
            entries[latestToken] = current with { LastSequence = sequence };
            return latestToken;
        }
        latestToken = Guid.NewGuid().ToString("N");
        entries.Add(latestToken, new(target, sequence, sequence)); order.Enqueue(latestToken);
        while (order.Count > MaximumSnapshots) entries.Remove(order.Dequeue());
        return latestToken;
    }

    internal bool Acknowledge(string token, long sequence, string? singleId = null)
    {
        if (!entries.TryGetValue(token, out var entry) || sequence < entry.FirstSequence || sequence > entry.LastSequence
            || singleId is not null && !entry.Target.MessageIds.Contains(singleId, StringComparer.Ordinal)) return false;
        entries.Remove(token);
        if (latestToken == token) latestToken = null;
        return inbox.Acknowledge(entry.Target, singleId);
    }
}
