using CodexQuotaTaskbar.Host.Update;

namespace CodexQuotaTaskbar.Host.Tests.Update;

public sealed class UpdateApplierTests : IDisposable
{
    private readonly string root = Path.Combine(TestSupport.RepositoryPaths.Root, "artifacts", "reliability", "update", "arguments-" + Guid.NewGuid().ToString("N"));

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
    public void Parses_new_caller_identity_and_nonce_but_keeps_legacy_three_arguments()
    {
        var target = Path.Combine(root, "中文 空格", "CodexQuotaTaskbar.exe");
        var nonce = Guid.NewGuid().ToString("N");
        Assert.True(UpdateApplyOptions.TryParse(["--apply-update", "42", target, "638000000000000000", nonce], out var options));
        Assert.Equal(nonce, options!.Nonce);
        Assert.Equal(638000000000000000, options.ParentStartTicks);
        Assert.False(UpdateApplyOptions.TryParse(["--apply-update", "42", target, "638000000000000000", "wrong"], out _));
        Assert.False(UpdateApplyOptions.TryParse(["--apply-update", "42", target, "0", nonce], out _));
        Assert.False(UpdateApplyOptions.TryParse(["--apply-update", "42", target, "638000000000000000", nonce, "extra"], out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}
