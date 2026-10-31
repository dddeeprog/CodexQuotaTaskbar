using System.IO;
using System.Text.Json;

namespace CodexQuotaTaskbar.Host.Resets;

internal sealed record ResetCreditPending(string AccountFingerprint, string RequestKey, string State,
    string? Outcome, DateTimeOffset SentAt);

// A lifetime process lock and a durable write precede every potentially consuming request.
// Only an account digest, an opaque idempotency key and receipt metadata are persisted.
internal sealed class ResetCreditPendingStore : IDisposable
{
    private readonly string path;
    private FileStream? processLock;
    internal bool IsBlocked { get; private set; }
    internal ResetCreditPending? Current { get; private set; }

    internal ResetCreditPendingStore(string path)
    {
        this.path = path;
        try
        {
            this.path = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(this.path)!;
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException();
            RejectReparsePoint(this.path + ".lock");
            processLock = new FileStream(this.path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            FileStream? existing;
            try { existing = new FileStream(this.path, FileMode.Open, FileAccess.Read, FileShare.Read); }
            catch (FileNotFoundException) { existing = null; } // Only absence at open is a first run.
            using (existing)
            {
                if (existing is not null)
                {
                    if (existing.Length is <= 0 or > 8192 || (File.GetAttributes(this.path) & FileAttributes.ReparsePoint) != 0) throw new IOException();
                    var data = JsonSerializer.Deserialize<Stored>(existing);
                    if (data is not { Schema: 1 } || !Valid(data.Pending)) throw new IOException();
                    Current = data.Pending;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        { IsBlocked = true; }
    }

    internal bool Save(ResetCreditPending pending)
    {
        if (IsBlocked || processLock is null || !Valid(pending)) { IsBlocked = true; return false; }
        string? temporary = null;
        try
        {
            RejectReparsePoint(path);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new Stored(1, pending));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            Current = pending;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        { IsBlocked = true; return false; }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static bool IsFingerprint(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static void RejectReparsePoint(string target)
    {
        try { if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) throw new IOException(); }
        catch (FileNotFoundException) { }
    }
    private static bool Valid(ResetCreditPending? item) => item is not null && IsFingerprint(item.AccountFingerprint)
        && Guid.TryParseExact(item.RequestKey, "N", out _) && item.State is "pending" or "completed"
        && (item.State == "pending" ? item.Outcome is null : item.Outcome is "reset" or "alreadyRedeemed" or "noCredit" or "nothingToReset");
    public void Dispose() { processLock?.Dispose(); processLock = null; }
    private sealed record Stored(int Schema, ResetCreditPending Pending);
}
