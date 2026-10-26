using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexQuotaTaskbar.Host.Provider;

// Received messages only, in the application's private data directory. Never use diagnostics for this state.
internal sealed class DotUnreadStore
{
    internal const int MaximumFileBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12,
    };
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    internal bool LastLoadInvalid { get; private set; }

    internal DotUnreadStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
        if (this.path.StartsWith("\\\\", StringComparison.Ordinal)
            || string.Equals(this.path, Path.GetPathRoot(this.path), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Unread state must be a bounded local file.", nameof(path));
    }

    internal static DotUnreadStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexQuotaTaskbar", "dot-unread.json"));

    internal async Task<DotUnreadSnapshot?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LastLoadInvalid = false;
            AssertRegularPath(path);
            if (!File.Exists(path)) return null;
            var bytes = await ReadBoundedAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var snapshot = JsonSerializer.Deserialize<DotUnreadSnapshot>(bytes, Json);
                if (DotUnreadInbox.IsValid(snapshot)) return snapshot;
                LastLoadInvalid = true;
                return null;
            }
            catch (JsonException) { LastLoadInvalid = true; return null; }
        }
        catch (InvalidDataException) { LastLoadInvalid = true; throw; }
        finally { gate.Release(); }
    }

    internal async Task SaveAsync(DotUnreadSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (!DotUnreadInbox.IsValid(snapshot)) throw new ArgumentException("Invalid unread snapshot.", nameof(snapshot));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, Json);
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("Unread state exceeds its size limit.");
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssertRegularPath(path);
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            AssertRegularPath(path);
            // If existing content is corrupt, retain an exact bounded recovery copy before replacement.
            if (File.Exists(path)) await PreserveInvalidExistingAsync(cancellationToken).ConfigureAwait(false);
            temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            AssertRegularPath(path);
            File.Move(temporary, path, overwrite: true);
            temporary = null;
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
            gate.Release();
        }
    }

    private async Task PreserveInvalidExistingAsync(CancellationToken cancellationToken)
    {
        var existing = await ReadBoundedAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (DotUnreadInbox.IsValid(JsonSerializer.Deserialize<DotUnreadSnapshot>(existing, Json))) return;
        }
        catch (JsonException) { }
        var digest = Convert.ToHexString(SHA256.HashData(existing)).ToLowerInvariant();
        var recovery = path + ".invalid-" + digest + ".bak";
        AssertRegularPath(recovery);
        if (File.Exists(recovery))
        {
            if (new FileInfo(recovery).Length > MaximumFileBytes) throw new InvalidDataException("Unread recovery copy exceeds its size limit.");
            var prior = await File.ReadAllBytesAsync(recovery, cancellationToken).ConfigureAwait(false);
            if (!prior.AsSpan().SequenceEqual(existing)) throw new IOException("Unread recovery copy mismatch.");
            return;
        }
        var temporary = recovery + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await output.WriteAsync(existing, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            AssertRegularPath(recovery);
            File.Move(temporary, recovery, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<byte[]> ReadBoundedAsync(CancellationToken cancellationToken)
    {
        AssertRegularPath(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.Asynchronous);
        if (input.Length > MaximumFileBytes) throw new InvalidDataException("Unread file exceeds its size limit; original retained.");
        using var memory = new MemoryStream((int)input.Length);
        var buffer = new byte[4096];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + count > MaximumFileBytes) throw new InvalidDataException("Unread file exceeds its size limit; original retained.");
            memory.Write(buffer, 0, count);
        }
        return memory.ToArray();
    }

    private static void AssertRegularPath(string path)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        var parts = full[current.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var index = 0; index < parts.Length; index++)
        {
            current = Path.Combine(current, parts[index]);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Unread path contains a reparse point.");
            if (index == parts.Length - 1 && (attributes & FileAttributes.Directory) != 0)
                throw new IOException("Unread destination is a directory.");
            if (index < parts.Length - 1 && (attributes & FileAttributes.Directory) == 0)
                throw new IOException("Unread parent is not a directory.");
        }
    }
}
