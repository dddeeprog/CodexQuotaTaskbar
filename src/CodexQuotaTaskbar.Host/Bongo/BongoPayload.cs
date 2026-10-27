using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexQuotaTaskbar.Host.Bongo;

internal sealed record BongoPayloadPaths(string HelperPath, string ModelDirectory);

// The updater still replaces one Core-free EXE. Only the fixed manifest's exact
// optional renderer can be downloaded after consent; arbitrary payloads are rejected.
internal sealed class BongoPayload
{
    internal const string ManifestResourceName = "CodexQuotaTaskbar.Host.Assets.Bongo.payload-manifest.json";
    internal const string ArchiveResourceName = "CodexQuotaTaskbar.Host.Assets.Bongo.payload.zip";
    internal const string HelperRelativePath = "DianDao.BongoRuntime.exe";
    internal const string ModelRelativeDirectory = "standard";
    internal const int MaximumPayloadBytes = 8 * 1024 * 1024;
    internal const int MaximumFiles = 1024;
    private const int MaximumManifestBytes = 512 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };
    private readonly string cacheRoot;
    private readonly Func<string, Stream?> openResource;
    private readonly Func<CancellationToken, Task<byte[]>>? downloadArchive;
    private readonly SemaphoreSlim gate = new(1, 1);

    internal BongoPayload(string? cacheRoot = null) : this(cacheRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexQuotaTaskbar", "Bongo"), name => typeof(BongoPayload).Assembly.GetManifestResourceStream(name),
        BongoOptionalDownload.ReadAfterConsentAsync) { }

    internal BongoPayload(string cacheRoot, Func<string, Stream?> openResource,
        Func<CancellationToken, Task<byte[]>>? downloadArchive = null)
    {
        this.cacheRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheRoot));
        if (this.cacheRoot.StartsWith("\\\\", StringComparison.Ordinal)
            || this.cacheRoot.Equals(Path.GetPathRoot(this.cacheRoot), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Bongo cache must be a bounded local directory.", nameof(cacheRoot));
        this.openResource = openResource ?? throw new ArgumentNullException(nameof(openResource));
        this.downloadArchive = downloadArchive;
    }

    internal async Task<BongoPayloadPaths> EnsureAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? staging = null;
        try
        {
            var manifestBytes = await ReadResourceAsync(ManifestResourceName, MaximumManifestBytes, cancellationToken).ConfigureAwait(false);
            PayloadManifest manifest;
            try { manifest = JsonSerializer.Deserialize<PayloadManifest>(manifestBytes, Json)
                ?? throw new InvalidDataException("Missing Bongo payload manifest."); }
            catch (JsonException error) { throw new InvalidDataException("Invalid Bongo payload manifest.", error); }
            var expected = ValidateManifest(manifest);
            AssertLocalRegularPath(cacheRoot, allowMissing: true);
            var target = ContainedPath(cacheRoot, manifest.ArchiveSha256!.ToLowerInvariant());
            if (Directory.Exists(target))
            {
                await ValidateDirectoryAsync(target, expected, cancellationToken).ConfigureAwait(false);
                return Paths(target);
            }
            if (File.Exists(target)) throw new InvalidDataException("Bongo cache target is not a directory.");

            var archiveBytes = downloadArchive is null
                ? await ReadResourceAsync(ArchiveResourceName, MaximumPayloadBytes, cancellationToken).ConfigureAwait(false)
                : await downloadArchive(cancellationToken).ConfigureAwait(false);
            if (archiveBytes.Length != manifest.ArchiveBytes || !Hash(archiveBytes).Equals(manifest.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Bongo optional archive hash does not match its manifest.");
            Directory.CreateDirectory(cacheRoot);
            AssertLocalRegularPath(cacheRoot, allowMissing: false);

            staging = ContainedPath(cacheRoot, ".extract-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            AssertLocalRegularPath(staging, allowMissing: false);
            using (var archive = new ZipArchive(new MemoryStream(archiveBytes, writable: false), ZipArchiveMode.Read))
            {
                if (archive.Entries.Count != expected.Count) throw new InvalidDataException("Bongo archive file count changed.");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsSafeRelativePath(entry.FullName) || !seen.Add(entry.FullName)
                        || !expected.TryGetValue(entry.FullName, out var file) || entry.Length != file.Bytes
                        || (entry.ExternalAttributes & 0x400) != 0
                        || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                        throw new InvalidDataException("Bongo archive contains an unexpected file or link.");
                    var destination = ContainedPath(staging, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    AssertLocalRegularPath(destination, allowMissing: true);
                    await using (var input = entry.Open())
                    await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 16 * 1024, FileOptions.Asynchronous))
                    {
                        await CopyBoundedAsync(input, output, file.Bytes, cancellationToken).ConfigureAwait(false);
                    }
                    await VerifyFileAsync(destination, file, cancellationToken).ConfigureAwait(false);
                }
            }
            await ValidateDirectoryAsync(staging, expected, cancellationToken).ConfigureAwait(false);
            AssertLocalRegularPath(cacheRoot, allowMissing: false);
            AssertLocalRegularPath(target, allowMissing: true);
            try { Directory.Move(staging, target); staging = null; }
            catch (IOException) when (Directory.Exists(target))
            {
                // A second instance may have published the same immutable payload.
                // It still has to pass exactly the same checks before it is used.
            }
            await ValidateDirectoryAsync(target, expected, cancellationToken).ConfigureAwait(false);
            return Paths(target);
        }
        finally
        {
            try { if (staging is not null) RemoveOwnStagingBestEffort(staging); }
            finally { gate.Release(); }
        }
    }

    private async Task<byte[]> ReadResourceAsync(string name, int maximum, CancellationToken cancellationToken)
    {
        await using var stream = openResource(name) ?? throw new InvalidDataException("Bongo runtime is not included in this build.");
        using var bytes = new MemoryStream();
        await CopyBoundedAsync(stream, bytes, maximum, cancellationToken, exact: false).ConfigureAwait(false);
        if (bytes.Length == 0) throw new InvalidDataException("Bongo resource is empty.");
        return bytes.ToArray();
    }

    private static Dictionary<string, PayloadFile> ValidateManifest(PayloadManifest manifest)
    {
        if (manifest.Schema != 1 || manifest.ArchiveBytes is <= 0 or > MaximumPayloadBytes
            || !IsHash(manifest.ArchiveSha256) || manifest.Files is not { Length: > 0 and <= MaximumFiles })
            throw new InvalidDataException("Invalid Bongo payload manifest.");
        var expected = new Dictionary<string, PayloadFile>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            if (file is null || !IsSafeRelativePath(file.Path) || file.Bytes is <= 0 or > MaximumPayloadBytes
                || !IsHash(file.Sha256) || !names.Add(file.Path!) || !expected.TryAdd(file.Path!, file)
                || (total += file.Bytes) > MaximumPayloadBytes)
                throw new InvalidDataException("Invalid or duplicate Bongo payload file.");
        }
        foreach (var required in new[] { HelperRelativePath, "standard/cat.model3.json", "standard/demomodel.moc3", "standard/LICENSE-MIT.txt" })
            if (!expected.ContainsKey(required)) throw new InvalidDataException("Incomplete fixed Bongo payload.");
        if (expected[HelperRelativePath].Bytes < 1024) throw new InvalidDataException("Invalid Bongo native helper.");
        return expected;
    }

    internal static bool IsSafeRelativePath(string? relative)
    {
        if (relative is not { Length: > 0 and <= 200 } || relative[0] == '/' || relative[^1] == '/') return false;
        foreach (var character in relative)
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-' or '/')) return false;
        foreach (var part in relative.Split('/'))
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.')) return false;
            var stem = part.Split('.')[0];
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                    && stem[3] is >= '1' and <= '9') return false;
        }
        return true;
    }

    private static async Task ValidateDirectoryAsync(string directory, Dictionary<string, PayloadFile> expected, CancellationToken cancellationToken)
    {
        AssertLocalRegularPath(directory, allowMissing: false);
        var pending = new Stack<string>(); pending.Push(directory);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = 0;
        while (pending.TryPop(out var current))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AssertLocalRegularPath(path, allowMissing: false);
                var relative = Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (++directories > MaximumFiles || !expected.Keys.Any(name => name.StartsWith(relative + "/", StringComparison.Ordinal)))
                        throw new InvalidDataException("Unexpected Bongo cache directory.");
                    pending.Push(path);
                }
                else
                {
                    if (!seen.Add(relative) || !expected.TryGetValue(relative, out var file))
                        throw new InvalidDataException("Unexpected Bongo cache file.");
                    await VerifyFileAsync(path, file, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        if (seen.Count != expected.Count) throw new InvalidDataException("Bongo cache file is missing.");
    }

    private static async Task VerifyFileAsync(string path, PayloadFile file, CancellationToken cancellationToken)
    {
        AssertLocalRegularPath(path, allowMissing: false);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous);
        if (stream.Length != file.Bytes || !Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Bongo cached file hash changed.");
        if (file.Path == HelperRelativePath)
        {
            stream.Position = 0;
            if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z') throw new InvalidDataException("Bongo helper is not a Windows executable.");
        }
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long maximum, CancellationToken cancellationToken, bool exact = true)
    {
        var buffer = new byte[16 * 1024];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if ((total += read) > maximum) throw new InvalidDataException("Bongo resource exceeds its size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        if (exact && total != maximum) throw new InvalidDataException("Bongo resource is truncated.");
    }

    private static string ContainedPath(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Bongo payload path escapes its cache.");
        return full;
    }

    private static void AssertLocalRegularPath(string path, bool allowMissing)
    {
        var current = Path.GetFullPath(path);
        if (current.StartsWith("\\\\", StringComparison.Ordinal)) throw new InvalidDataException("Bongo cache must be local.");
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Bongo cache cannot traverse links or junctions.");
            }
            catch (FileNotFoundException) when (allowMissing) { }
            catch (DirectoryNotFoundException) when (allowMissing) { }
            current = Path.GetDirectoryName(current);
        }
    }

    private void RemoveOwnStagingBestEffort(string staging)
    {
        try
        {
            if (!Path.GetFileName(staging).StartsWith(".extract-", StringComparison.Ordinal)
                || !Path.GetDirectoryName(staging)!.Equals(cacheRoot, StringComparison.OrdinalIgnoreCase)) return;
            AssertLocalRegularPath(staging, allowMissing: false);
            // Delete only ordinary entries under our fresh staging root. No
            // existing cache or unrelated user file is ever recursively removed.
            RemoveRegularTree(staging);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }
    }

    private static void RemoveRegularTree(string directory)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            AssertLocalRegularPath(path, allowMissing: false);
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) RemoveRegularTree(path);
            else File.Delete(path);
        }
        Directory.Delete(directory, recursive: false);
    }

    private static BongoPayloadPaths Paths(string directory) => new(Path.Combine(directory, HelperRelativePath), Path.Combine(directory, ModelRelativeDirectory));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private sealed record PayloadManifest(int Schema, int ArchiveBytes, string? ArchiveSha256, PayloadFile[]? Files);
    private sealed record PayloadFile(string? Path, int Bytes, string? Sha256);
}
