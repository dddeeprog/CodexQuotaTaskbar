using System.IO;
using System.Text;

namespace CodexQuotaTaskbar.Host.Diagnostics;

// Only the three diagnostic producers own these names. Receipts and unknown files are never managed.
internal static class DiagnosticLogStore
{
    internal const int MaxFileBytes = 256 * 1024;
    internal const int MaxFiles = 8; // Includes temporary snapshots; total payload <= 2 MiB plus a zero-byte lock.
    internal static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaTaskbar", "Diagnostics");
    internal static bool IsOwned(string name, bool temporary = true)
    {
        if (temporary && name.EndsWith(".tmp", StringComparison.Ordinal)) name = name[..^4];
        if (!name.EndsWith(".json", StringComparison.Ordinal)) return false;
        var stem = name[..^5]; var split = stem.LastIndexOf('-');
        return split > 0 && stem[..split] is "companion" or "overlay" or "reliability"
            && stem[(split + 1)..] is { Length: > 0 and <= 10 } pid && pid.All(char.IsAsciiDigit) && int.TryParse(pid, out var id) && id > 0;
    }

    internal static bool SafePath(string path)
    {
        var full = Path.GetFullPath(path);
        for (string? item = full; item is not null; item = Path.GetDirectoryName(item))
        {
            try { if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) return false; }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return true;
    }

    // Explicit demo/fixture paths remain injectable; only IsOwned names participate in retention/export.
    internal static bool WriteSnapshot(string path, string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxFileBytes) return false;
        try
        {
            path = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(path)!;
            if (!SafePath(path) || !SafePath(path + ".tmp")) return false;
            Directory.CreateDirectory(directory);
            var lockPath = Path.Combine(directory, ".diagnostics.lock");
            if (!SafePath(lockPath)) return false;
            using var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (held.Length != 0) return false; // Never truncate unexpected material at the lock name.
            if (!Prune(directory, MaxFiles - 1)) return false; // Reserve space for the temporary file.
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path, overwrite: true);
            return Prune(directory, MaxFiles);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    private static bool Prune(string directory, int capacity)
    {
        var retained = new List<FileInfo>(MaxFiles + 1);
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            if (!IsOwned(Path.GetFileName(path)) || !SafePath(path)) continue;
            var file = new FileInfo(path);
            if (file.Length > MaxFileBytes || file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7)) { file.Delete(); continue; }
            retained.Add(file);
            if (retained.Count <= capacity) continue;
            var oldest = retained.MinBy(f => f.LastWriteTimeUtc)!;
            oldest.Delete(); retained.Remove(oldest);
        }
        return true;
    }
}
