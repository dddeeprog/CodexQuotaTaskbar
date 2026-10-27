using System.IO;
using System.Text.Json;

namespace CodexQuotaTaskbar.Host.Resets;

// Only opaque event/account/window/notice digests and times; never raw IDs, balances, credentials or messages.
internal sealed class ResetConfirmationStore(string path)
{
    private const int MaximumBytes = 131072;
    internal static ResetConfirmationStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaTaskbar", "reset-confirmations.json"));

    internal IReadOnlyList<ResetConfirmation> Load() => Read()?.Events ?? [];
    internal IReadOnlyList<ConsumedResetNotice> LoadConsumedNotices() => Read()?.Notices ?? [];

    private StoredState? Read()
    {
        try
        {
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaximumBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var state = JsonSerializer.Deserialize<StoredState>(stream, new JsonSerializerOptions { MaxDepth = 8 });
            return state is { Schema: 2, Events.Length: <= 32, Notices.Length: <= 256 } && state.Events.All(IsValid)
                && state.Notices.All(IsValidNotice)
                && state.Notices.Select(item => item.ScopeId + item.NoticeId).Distinct(StringComparer.Ordinal).Count() == state.Notices.Length
                && state.Events.Select(item => item.EventId).Distinct(StringComparer.Ordinal).Count() == state.Events.Length
                ? state : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    internal bool TrySave(IReadOnlyList<ResetConfirmation> events, IReadOnlyList<ConsumedResetNotice> notices)
    {
        if (events.Count > 32 || events.Any(item => !IsValid(item))
            || notices.Count > 256 || notices.Any(item => !IsValidNotice(item))) return false;
        string? temporary = null;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
                || File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            temporary = Path.Combine(directory, $".reset-confirmations-{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new StoredState(2, events.ToArray(), notices.ToArray()));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
        finally
        {
            if (temporary is not null)
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool IsValid(ResetConfirmation? item) => item is not null
        && QuotaResetTracker.IsDigest(item.ScopeId) && QuotaResetTracker.IsDigest(item.WindowId) && QuotaResetTracker.IsDigest(item.EventId)
        && item.SampledAt <= item.OccurredAt && item.OccurredAt - item.SampledAt <= TimeSpan.FromSeconds(30)
        && item.ExpiresAt > item.OccurredAt && item.ExpiresAt - item.OccurredAt == QuotaResetTracker.ConfirmationLifetime;
    private static bool IsValidNotice(ConsumedResetNotice? item) => item is not null
        && QuotaResetTracker.IsDigest(item.ScopeId) && QuotaResetTracker.IsDigest(item.NoticeId);
    private sealed record StoredState(int Schema, ResetConfirmation[] Events, ConsumedResetNotice[] Notices);
}
