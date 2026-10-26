using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexQuotaTaskbar.Host.Lifetime;

// A published-EXE proof may coexist with the real app, but only inside a marked,
// bounded temporary instance. It never redirects the process-wide AppData value.
internal sealed class DemoIntegrationContext
{
    private const string MarkerName = ".diandao-demo-instance.json";
    private const string Purpose = "diandao-isolated-demo";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4,
    };
    internal static string BaseDirectory => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DianDao.IntegrationProofs"));
    internal string ContextRoot { get; }
    internal string SettingsPath => Path.Combine(ContextRoot, "settings.json");
    internal string BongoCachePath => Path.Combine(ContextRoot, "Bongo");
    internal string MutexName { get; }

    private DemoIntegrationContext(string root)
    {
        ContextRoot = root;
        MutexName = "Local\\CodexQuotaTaskbar.Host.Demo." + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..24];
    }

    internal static string ValidateRootPath(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)
            || directory.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("Demo data root must be a fully qualified local path.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!string.Equals(Path.GetDirectoryName(full), BaseDirectory, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(full), "N", out _))
            throw new ArgumentException("Demo data root must be Temp/DianDao.IntegrationProofs/<32-digit GUID>.");
        AssertOrdinaryPath(full, allowMissing: true);
        return full;
    }

    internal static DemoIntegrationContext CreateOrOpen(string directory)
    {
        var full = ValidateRootPath(directory);
        Directory.CreateDirectory(full);
        AssertOrdinaryPath(full, allowMissing: false);
        var markerPath = Path.Combine(full, MarkerName);
        var instance = Path.GetFileName(full);
        if (!File.Exists(markerPath))
        {
            if (Directory.EnumerateFileSystemEntries(full).Any())
                throw new InvalidDataException("Unmarked nonempty directory cannot become a demo instance.");
            using var marker = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(marker, new Marker(1, Purpose, instance), Json);
        }
        else
        {
            AssertOrdinaryPath(markerPath, allowMissing: false);
            using var marker = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (marker.Length is <= 0 or > 1024) throw new InvalidDataException("Invalid demo instance marker.");
            Marker? value;
            try { value = JsonSerializer.Deserialize<Marker>(marker, Json); }
            catch (JsonException error) { throw new InvalidDataException("Invalid demo instance marker.", error); }
            if (value is not { Schema: 1, Purpose: Purpose } || !string.Equals(value.Instance, instance, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Demo instance marker belongs to another directory.");
        }
        return new(full);
    }

    private static void AssertOrdinaryPath(string path, bool allowMissing)
    {
        var current = path;
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Demo data root cannot traverse links or junctions.");
            }
            catch (FileNotFoundException) when (allowMissing) { }
            catch (DirectoryNotFoundException) when (allowMissing) { }
            current = Path.GetDirectoryName(current);
        }
    }

    private sealed record Marker(int Schema, string Purpose, string Instance);
}
