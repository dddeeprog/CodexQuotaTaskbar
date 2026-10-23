namespace CodexQuotaTaskbar.Host.Provider;

internal sealed record DotMessage(string Id, string Role, string Text, DateTimeOffset? Time,
    bool Confirmable = false, string IdSource = "unknown", string RoleSource = "unknown", string TextSource = "unknown");

// Keep only IDs and the current message. Initial history and dismissed streaming updates stay quiet.
internal sealed class DotMessageTracker(DateTimeOffset connectedAt)
{
    private readonly HashSet<string> known = new(StringComparer.Ordinal);
    private readonly Queue<string> order = new();
    private string? dismissed;
    internal DotMessage? Latest { get; private set; }

    internal DotMessage? Observe(IEnumerable<DotMessage> messages)
    {
        DotMessage? changed = null;
        foreach (var message in messages)
        {
            if (string.IsNullOrEmpty(message.Id) || message.Role != "assistant" || string.IsNullOrWhiteSpace(message.Text)) continue;
            if (message.Time is not { } time || time < connectedAt) continue;
            if (known.Add(message.Id))
            {
                order.Enqueue(message.Id);
                while (order.Count > 128) known.Remove(order.Dequeue());
                Latest = changed = message;
            }
            else if (Latest?.Id == message.Id && Latest.Text != message.Text)
                Latest = changed = message;
        }
        return changed?.Id == dismissed ? null : changed;
    }

    internal void Dismiss() => dismissed = Latest?.Id;
}
