using System.IO;
using CodexQuotaTaskbar.Host.Lifetime;

namespace CodexQuotaTaskbar.Host.Tests.Lifetime;

public sealed class DemoProofFileTests : IDisposable
{
    private static string TestRoot => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DianDao.DemoProofFileTests"));
    private readonly string root = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));

    public DemoProofFileTests() => Directory.CreateDirectory(root);

    [Fact]
    public void A_successful_write_atomically_replaces_only_its_own_target()
    {
        var path = Path.Combine(root, "proof.json");
        File.WriteAllText(path, "synthetic old proof");

        Assert.True(DemoProofFile.TryWrite(path, "synthetic new proof", out var failure));

        Assert.Null(failure);
        Assert.Equal("synthetic new proof", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Single(Directory.EnumerateFiles(root));
    }

    [Fact]
    public void A_locked_target_reports_the_actual_error_and_keeps_its_old_body()
    {
        var path = Path.Combine(root, "locked-proof.json");
        File.WriteAllText(path, "synthetic preserved proof");
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.False(DemoProofFile.TryWrite(path, "synthetic replacement", out var failure));

        var error = Assert.IsType<DemoProofFileFailure>(failure);
        Assert.Contains(error.Type, new[] { typeof(IOException).FullName, typeof(UnauthorizedAccessException).FullName });
        Assert.Equal(error.Type == typeof(UnauthorizedAccessException).FullName
            ? unchecked((int)0x80070005) : unchecked((int)0x80070020), error.HResult);
        Assert.Equal("synthetic preserved proof", File.ReadAllText(path));
        Assert.Equal("synthetic replacement", File.ReadAllText(path + ".tmp"));
    }

    [Fact]
    public void A_later_explicit_write_recovers_after_the_reader_is_released()
    {
        var path = Path.Combine(root, "recoverable-proof.json");
        File.WriteAllText(path, "synthetic original");
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(DemoProofFile.TryWrite(path, "synthetic blocked attempt", out var failure));
            Assert.NotNull(failure);
            Assert.Equal("synthetic original", File.ReadAllText(path));
        }

        // The helper itself never retries. This is a separate caller action
        // after the conflicting handle has deterministically been released.
        Assert.True(DemoProofFile.TryWrite(path, "synthetic later proof", out var recovered));
        Assert.Null(recovered);
        Assert.Equal("synthetic later proof", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void An_unavailable_directory_is_reported_without_creating_or_repairing_it()
    {
        var unavailable = Path.Combine(root, "not-a-directory");
        File.WriteAllText(unavailable, "synthetic directory blocker");

        Assert.False(DemoProofFile.TryWrite(Path.Combine(unavailable, "proof.json"), "synthetic proof", out var failure));

        var error = Assert.IsType<DemoProofFileFailure>(failure);
        Assert.Contains(error.Type, new[] { typeof(DirectoryNotFoundException).FullName,
            typeof(IOException).FullName, typeof(UnauthorizedAccessException).FullName });
        Assert.NotEqual(0, error.HResult);
        Assert.Equal("synthetic directory blocker", File.ReadAllText(unavailable));
        Assert.False(Directory.Exists(unavailable));
        Assert.Single(Directory.EnumerateFiles(root));
    }

    public void Dispose()
    {
        var resolved = Path.GetFullPath(root);
        if (!string.Equals(Path.GetDirectoryName(resolved), TestRoot, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing cleanup outside this own temporary test directory.");
        Directory.Delete(resolved, recursive: true);
    }
}
