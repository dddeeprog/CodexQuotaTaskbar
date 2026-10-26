using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexQuotaTaskbar.Host.Lifetime;

// A crash guard stores no draft, message body, baseline or account information.
// It only prevents a new receiver from implying that an interrupted send failed.
internal sealed record DotInlineSendCheckpoint(bool Uncertain, string? RequestId, DateTimeOffset Utc,
    long DraftRevision = 0, string? DraftEpoch = null);

internal sealed class DotInlineSendGuard(string path)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        RespectRequiredConstructorParameters = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8,
    };
    internal static DotInlineSendGuard CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexQuotaTaskbar", "dot-send-guard.json"));

    internal async Task<DotInlineSendCheckpoint> LoadAsync(CancellationToken cancellationToken = default)
    {
        CheckPath(path);
        if (!File.Exists(path)) return new(false, null, DateTimeOffset.MinValue);
        var file = new FileInfo(path);
        if (file.Length > 4096) throw new InvalidDataException("Invalid Dot send guard size.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous);
        var value = await JsonSerializer.DeserializeAsync<DotInlineSendCheckpoint>(stream, Json, cancellationToken);
        return value is not null && value.DraftRevision >= 0
            && value.Utc != default && (value.Uncertain || value.RequestId is null)
            && (value.RequestId is null || Guid.TryParseExact(value.RequestId, "N", out _))
            && (!value.Uncertain || value.RequestId is not null && Guid.TryParseExact(value.DraftEpoch, "N", out _))
            ? value : throw new InvalidDataException("Invalid Dot send guard.");
    }

    internal async Task SaveAsync(DotInlineSendCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        CheckPath(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        CheckPath(path);
        var temporary = Path.Combine(directory, ".dot-send-guard-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous))
                await JsonSerializer.SerializeAsync(stream, checkpoint, Json, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            CheckPath(path);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void CheckPath(string value)
    {
        var full = Path.GetFullPath(value);
        if (full.StartsWith("\\\\", StringComparison.Ordinal)) throw new IOException("Dot send guard must be local.");
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Dot send guard cannot follow a link.");
            if (current == full && Directory.Exists(current)) throw new IOException("Dot send guard is not a file.");
        }
    }
}
