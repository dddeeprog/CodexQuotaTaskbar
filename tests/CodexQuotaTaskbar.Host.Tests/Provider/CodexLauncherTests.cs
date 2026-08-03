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

        Assert.Equal(native, CodexLauncher.FindExecutable([root], [], Path.Combine(root, "cache")));
    }

    [Fact]
    public void Falls_back_to_packaged_desktop_binary()
    {
        var packageRoot = Path.Combine(root, "OpenAI.Codex_1.0.0.0_x64__test");
        var native = Path.Combine(packageRoot, "app", "resources", "codex.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(native)!);
        File.WriteAllText(native, "binary");
        var cacheRoot = Path.Combine(root, "cache");
        var expected = Path.Combine(cacheRoot, Path.GetFileName(packageRoot), "codex.exe");

        Assert.Equal(expected, CodexLauncher.FindExecutable([], [packageRoot], cacheRoot));
        Assert.Equal("binary", File.ReadAllText(expected));
    }

    [Fact]
    public void Prefers_path_binary_over_packaged_desktop_binary()
    {
        var pathRoot = Path.Combine(root, "path");
        var pathBinary = Path.Combine(pathRoot, "codex.exe");
        Directory.CreateDirectory(pathRoot);
        File.WriteAllText(pathBinary, "path binary");

        var packageRoot = Path.Combine(root, "package");
        var packageBinary = Path.Combine(packageRoot, "app", "resources", "codex.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(packageBinary)!);
        File.WriteAllText(packageBinary, "package binary");

        Assert.Equal(pathBinary, CodexLauncher.FindExecutable([pathRoot], [packageRoot], Path.Combine(root, "cache")));
    }

    [Fact]
    public void Builds_the_registered_codex_thread_uri()
    {
        var id = "019fb91e-5990-7c53-a448-c5287b8678af";

        Assert.Equal($"codex://threads/{id}", CodexLauncher.BuildThreadUri(id).AbsoluteUri);
        Assert.Throws<ArgumentException>(() => CodexLauncher.BuildThreadUri("not-a-thread"));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}
