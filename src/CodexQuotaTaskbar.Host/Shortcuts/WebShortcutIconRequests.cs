namespace CodexQuotaTaskbar.Host.Shortcuts;

// Each edit gets a distinct request even if an item goes auto -> manual -> auto.
// A previously pending download must not replace the current selection.
internal sealed class WebShortcutIconRequests
{
    internal sealed record Request(string Id, string Target, string? ManualKey, long Revision);
    private readonly Dictionary<string, Request> active = new(StringComparer.Ordinal);
    private long revision;

    internal bool TryBegin(WebShortcut item, out Request request)
    {
        if (active.TryGetValue(item.Id, out var existing) && Matches(existing, item))
        {
            request = existing;
            return false;
        }
        request = new(item.Id, item.TargetUri.AbsoluteUri, item.ManualIconKey, ++revision);
        active[item.Id] = request;
        return true;
    }

    internal void Retain(IReadOnlyList<WebShortcut> items)
    {
        foreach (var entry in active.Values.ToArray())
            if (!items.Any(item => Matches(entry, item))) active.Remove(entry.Id);
    }

    internal bool IsCurrent(Request request, IReadOnlyList<WebShortcut> items) =>
        active.GetValueOrDefault(request.Id) == request && items.Any(item => Matches(request, item));

    private static bool Matches(Request request, WebShortcut item) => request.Id == item.Id
        && request.Target == item.TargetUri.AbsoluteUri && request.ManualKey == item.ManualIconKey;
}
