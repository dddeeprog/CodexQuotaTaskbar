using CodexQuotaTaskbar.Host.Provider;

namespace CodexQuotaTaskbar.Host.Tests.Provider;

public sealed class CodexLauncherTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CodexLauncherTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Prefers_native_binary_behind_npm_shim()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "codex.cmd"), "shim");
        var native = Path.Combine(root, "node_modules", "@openai", "codex", "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(native)!);
        File.WriteAllText(native, "binary");
        File.WriteAllText(Path.Combine(root, "codex.exe"), "store-alias");

        Assert.Equal(native, CodexLauncher.FindExecutable([root]));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}
