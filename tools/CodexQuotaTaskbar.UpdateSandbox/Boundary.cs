using System.Security.Cryptography;
using System.Text.Json;
using CodexQuotaTaskbar.Host.Update;

namespace CodexQuotaTaskbar.UpdateSandbox;

internal sealed class Boundary
{
    internal string Root { get; }
    internal string[] Excluded { get; }
    internal static string AllowedParent => Path.Combine(Path.GetTempPath(), "CodexQuotaTaskbar.UpdateSandbox", "runs");
    internal Boundary(string root)
    {
        Root = Absolute(root);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Excluded = new[]
        {
            Path.Combine(user, "Documents", "CodexQuotaTaskbar"),
            Path.Combine(user, "Documents", "Codex"),
            Path.Combine(user, ".codex", "worktrees"),
            Path.Combine(user, ".codex", "visualizations"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaTaskbar"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CodexQuotaTaskbar"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CodexQuotaTaskbar"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows)
        }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Absolute).ToArray();
    }
    internal static string Absolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Length < 4
            || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\'
            || path.AsSpan(2).Contains(':') || path.Contains('/')
            || path.Split('\\').Skip(1).Any(part => part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.')))
            throw new ArgumentException("Only canonical absolute local drive paths are accepted (no UNC, device, ADS, traversal or ambiguous suffixes).");
        var full = Path.GetFullPath(path).TrimEnd('\\');
        if (!string.Equals(full, path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Path must already be canonical.");
        return full;
    }
    internal static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    internal static void NoLinks(string path)
    {
        var current = Absolute(path);
        while (!string.IsNullOrWhiteSpace(current))
        {
            // GetAttributes detects dangling reparse entries too; missing future components are allowed.
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Reparse points and links are forbidden."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) break;
            // Preserve a drive root's trailing separator for Absolute.
            current = parent;
        }
    }
    internal void CheckRoot(bool requireFresh)
    {
        var parent = Absolute(AllowedParent);
        if (!string.Equals(Path.GetDirectoryName(Root), parent, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(Root), "N", out _))
            throw new InvalidOperationException($"Sandbox root must be a fresh GUID directory directly below {parent}.");
        if (Excluded.Any(x => Within(Root, x) || Within(x, Root))) throw new InvalidOperationException("Production, configuration or repository path excluded.");
        NoLinks(Root);
        if (requireFresh && (Directory.Exists(Root) || File.Exists(Root))) throw new InvalidOperationException("Every driver invocation needs a new, absent root. Existing runs are evidence, never fresh inputs.");
        if (!requireFresh && !Directory.Exists(Root)) throw new InvalidOperationException("Child root does not exist.");
    }
    internal string InRoot(params string[] names) => Check(Path.Combine([Root, .. names]));
    internal string Check(string path)
    {
        var full = Absolute(path);
        if (!Within(full, Root) || full == Root || Excluded.Any(x => Within(full, x))) throw new InvalidOperationException("Path escaped sandbox.");
        NoLinks(full);
        return full;
    }
    internal void CreateFreshRoot()
    {
        CheckRoot(requireFresh: true);
        Directory.CreateDirectory(Root);
        NoLinks(Root);
        if (Directory.EnumerateFileSystemEntries(Root).Any()) throw new InvalidOperationException("Fresh-root race or unexpected contents.");
    }
}

internal sealed record Manifest(int Schema, string Kind, Dictionary<string, string> Files);
internal sealed class Package(string root, string manifestHash, Manifest manifest)
{
    internal const string Name = "CodexQuotaTaskbar.UpdateSandbox";
    internal const string ManifestName = "sandbox-package.json";
    internal static readonly string[] Names = [Name + ".exe", Name + ".dll", Name + ".deps.json", Name + ".runtimeconfig.json", "fixture-old.payload", "fixture-new.payload"];
    internal string Root => root;
    internal string ManifestHash => manifestHash;
    internal string FilePath(string name) => Path.Combine(root, name);
    internal string ExpectedHash(string name) => manifest.Files[name];
    internal static Package Load(string path, string expected)
    {
        path = Boundary.Absolute(path);
        Boundary.NoLinks(path);
        var manifestPath = Path.Combine(path, ManifestName);
        Boundary.NoLinks(manifestPath);
        if (Files.Hash(manifestPath) != expected) throw new InvalidDataException("Fixture package manifest SHA256 mismatch.");
        var value = Files.ReadJson<Manifest>(manifestPath);
        if (value.Schema != 1 || value.Kind != "untrusted-test-fixture-only" || value.Files.Count != Names.Length
            || !value.Files.Keys.Order().SequenceEqual(Names.Order())) throw new InvalidDataException("Unexpected fixture package allowlist.");
        foreach (var name in Names)
        {
            var file = Path.Combine(path, name);
            Boundary.NoLinks(file);
            if (Files.Hash(file) != value.Files[name]) throw new InvalidDataException("Fixture package file hash mismatch: " + name);
        }
        // A user-supplied manifest alone must never turn an arbitrary renamed app into an allowed child.
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Missing apphost path.");
        Boundary.NoLinks(self);
        if (!Path.GetFileName(self).Equals(Name + ".exe", StringComparison.OrdinalIgnoreCase)
            || Files.Hash(self) != value.Files[Name + ".exe"]
            || Files.Hash(typeof(Package).Assembly.Location) != value.Files[Name + ".dll"])
            throw new InvalidDataException("Package must contain this running sandbox apphost and assembly, not another executable.");
        foreach (var name in new[] { Name + ".deps.json", Name + ".runtimeconfig.json" })
            if (Files.Hash(Path.Combine(AppContext.BaseDirectory, name)) != value.Files[name]) throw new InvalidDataException("Runtime metadata differs from the running tool.");
        // These text payloads deliberately fail production PE identity checks.
        var old = File.ReadAllText(Path.Combine(path, "fixture-old.payload")).TrimEnd('\r', '\n');
        var next = File.ReadAllText(Path.Combine(path, "fixture-new.payload")).TrimEnd('\r', '\n');
        if (old != "UPDATE SANDBOX ONLY; NOT AN EXECUTABLE; FIXTURE VERSION 1.0.0"
            || next != "UPDATE SANDBOX ONLY; NOT AN EXECUTABLE; FIXTURE VERSION 2.0.0"
            || value.Files["fixture-old.payload"] == value.Files["fixture-new.payload"])
            throw new InvalidDataException("Unexpected fixed fixture payload.");
        return new(path, expected, value);
    }
    internal Package CopyTo(Boundary boundary, string destination)
    {
        boundary.Check(destination);
        Directory.CreateDirectory(destination);
        foreach (var name in Names.Append(ManifestName))
        {
            Boundary.NoLinks(FilePath(name));
            File.Copy(FilePath(name), boundary.Check(Path.Combine(destination, name)), overwrite: false);
        }
        return Load(destination, manifestHash);
    }
    internal UpdatePackageIdentity FixtureIdentity(string path)
    {
        var hash = Files.Hash(path);
        return hash == ExpectedHash("fixture-old.payload") ? new(new Version(1, 0, 0), hash)
            : hash == ExpectedHash("fixture-new.payload") ? new(new Version(2, 0, 0), hash)
            : throw new InvalidDataException("Not one of the two pinned non-PE fixture payloads.");
    }
}

internal static class Files
{
    internal static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    internal static T ReadJson<T>(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length > 64 * 1024) throw new InvalidDataException("Oversized fixture JSON.");
        return JsonSerializer.Deserialize<T>(input) ?? throw new InvalidDataException("Missing JSON object.");
    }
    internal static void WriteJson(Boundary boundary, string path, object value)
    {
        boundary.Check(path); boundary.Check(path + ".writing");
        new SystemUpdateFiles().Write(path, JsonSerializer.SerializeToUtf8Bytes(value, Program.Json));
    }
}

// Every production file-operation call, including receipt/stage/backup/lock and .writing, is fenced.
internal sealed class SandboxFiles(Boundary boundary, string target, string source) : IUpdateFiles
{
    private readonly SystemUpdateFiles inner = new();
    private string Checked(string path)
    {
        path = boundary.Check(path);
        if (!new[] { target, source, target + ".update.lock", UpdateTransaction.ReceiptPath(target), UpdateTransaction.BackupPath(target),
                UpdateTransaction.StagePath(target), UpdateStartup.ReadyPath(target) }.Contains(path, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Transaction path is not in the exact file allowlist.");
        return path;
    }
    public bool Exists(string path) => inner.Exists(Checked(path));
    public byte[] Read(string path) => inner.Read(Checked(path));
    public string Hash(string path) => inner.Hash(Checked(path));
    public void Write(string path, byte[] bytes) { Checked(path); boundary.Check(path + ".writing"); inner.Write(path, bytes); }
    public void Copy(string sourcePath, string targetPath) => inner.Copy(Checked(sourcePath), Checked(targetPath));
    public void Replace(string sourcePath, string targetPath, string? backup) => inner.Replace(Checked(sourcePath), Checked(targetPath), backup is null ? null : Checked(backup));
    public void Delete(string path) => inner.Delete(Checked(path));
    public IDisposable Lock(string path) => inner.Lock(Checked(path));
}
