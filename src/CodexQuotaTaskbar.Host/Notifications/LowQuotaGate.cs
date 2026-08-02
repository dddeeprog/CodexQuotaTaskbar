using CodexQuotaTaskbar.Core.Quota;

namespace CodexQuotaTaskbar.Host.Notifications;

internal sealed class LowQuotaGate(double threshold)
{
    private readonly HashSet<string> notified = new(StringComparer.Ordinal);

    internal bool ShouldNotify(IEnumerable<QuotaWindowSnapshot> windows)
    {
        var shouldNotify = false;
        foreach (var window in windows)
        {
            var key = $"{window.LimitId}:{window.Kind}:{window.WindowDurationMinutes}";
            if (window.RemainingPercent < threshold)
            {
                shouldNotify |= notified.Add(key);
            }
            else
            {
                notified.Remove(key);
            }
        }

        return shouldNotify;
    }
}
