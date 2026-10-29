using CodexQuotaTaskbar.Host.Update;

namespace CodexQuotaTaskbar.Host.Tests.Update;

public sealed class UpdateApplierTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CodexQuotaTaskbar.ApplierTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Parses_bounded_apply_arguments()
    {
        var target = Path.Combine(root, "CodexQuotaTaskbar.exe");

        Assert.True(UpdateApplyOptions.TryParse(["--apply-update", "42", target], out var options));
        Assert.Equal(42, options!.ParentProcessId);
        Assert.Equal(Path.GetFullPath(target), options.TargetExecutable);
        Assert.False(UpdateApplyOptions.TryParse(["--apply-update", "0", target], out _));
        Assert.False(UpdateApplyOptions.TryParse(["--apply-update", "42", Path.Combine(root, "bad.dll")], out _));
    }

    [Fact]
    public void Replaces_only_the_existing_executable_target()
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "new.exe");
        var target = Path.Combine(root, "CodexQuotaTaskbar.exe");
        File.WriteAllText(source, "new version");
        File.WriteAllText(target, "old version");

        UpdateApplier.ReplaceExecutable(source, target);

        Assert.Equal("new version", File.ReadAllText(target));
        Assert.Throws<InvalidOperationException>(() => UpdateApplier.ReplaceExecutable(source, Path.Combine(root, "missing.exe")));
    }

    [Fact]
    public void Locked_destination_preserves_old_bytes_and_unrelated_data_then_can_retry()
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "new.exe"); var target = Path.Combine(root, "CodexQuotaTaskbar.exe");
        var settings = Path.Combine(root, "settings.json");
        File.WriteAllText(source, "new synthetic executable"); File.WriteAllText(target, "old synthetic executable");
        File.WriteAllText(settings, "keep synthetic preferences");
        using (File.Open(target, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = Record.Exception(() => UpdateApplier.ReplaceExecutable(source, target));
            // Windows can report a locked replace as sharing violation or access denied.
            Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        }
        Assert.Equal("old synthetic executable", File.ReadAllText(target));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        UpdateApplier.ReplaceExecutable(source, target);
        Assert.Equal("new synthetic executable", File.ReadAllText(target));
        Assert.Equal("keep synthetic preferences", File.ReadAllText(settings));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}
