using System.IO;
using CodexQuotaTaskbar.Host.Lifetime;

namespace CodexQuotaTaskbar.Host.Tests.Lifetime;

public sealed class DemoIntegrationContextTests : IDisposable
{
    private readonly string root = Path.Combine(DemoIntegrationContext.BaseDirectory, Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("1")]
    [InlineData("60")]
    public void Isolated_demo_requires_a_valid_root_and_bounded_explicit_lifetime(string seconds)
    {
        var options = HostOptions.Parse(["--demo", "--demo-data-root", root, "--exit-after-seconds", seconds]);
        Assert.True(options.Demo);
        Assert.Equal(root, options.DemoDataRoot);
        Assert.Equal(TimeSpan.FromSeconds(int.Parse(seconds)), options.ExitAfter);
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("no_demo")]
    [InlineData("no_exit")]
    [InlineData("long_exit")]
    [InlineData("dot")]
    [InlineData("login")]
    [InlineData("duplicate_root")]
    public void Rejects_incomplete_or_live_mixed_isolated_startup(string kind)
    {
        var args = new List<string> { "--demo", "--demo-data-root", root, "--exit-after-seconds", "8" };
        switch (kind)
        {
            case "no_demo": args.RemoveAt(0); break;
            case "no_exit": args.RemoveRange(3, 2); break;
            case "long_exit": args[^1] = "61"; break;
            case "dot": args.Add("--dot-realtime"); break;
            case "login": args.Add("--subscription-login"); break;
            case "duplicate_root": args.AddRange(["--demo-data-root", root]); break;
        }
        Assert.Throws<ArgumentException>(() => HostOptions.Parse(args));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Isolated_root_cannot_point_to_real_AppData_or_an_arbitrary_directory()
    {
        foreach (var path in new[] { Path.GetTempPath(), Path.Combine(DemoIntegrationContext.BaseDirectory, "not-a-guid"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaTaskbar"), "relative-proof" })
            Assert.Throws<ArgumentException>(() => HostOptions.Parse(["--demo", "--demo-data-root", path, "--exit-after-seconds", "8"]));
    }

    [Fact]
    public void Marked_instance_reopens_with_same_private_paths_mutex_and_unchanged_settings()
    {
        var first = DemoIntegrationContext.CreateOrOpen(root);
        File.WriteAllText(first.SettingsPath, "synthetic preserved settings");
        var marker = File.ReadAllBytes(Path.Combine(root, ".diandao-demo-instance.json"));
        var second = DemoIntegrationContext.CreateOrOpen(root);

        Assert.Equal(first.ContextRoot, second.ContextRoot);
        Assert.Equal(first.MutexName, second.MutexName);
        Assert.StartsWith("Local\\CodexQuotaTaskbar.Host.Demo.", second.MutexName, StringComparison.Ordinal);
        Assert.NotEqual("Local\\CodexQuotaTaskbar.Host", second.MutexName);
        Assert.Equal(Path.Combine(root, "Bongo"), second.BongoCachePath);
        Assert.Equal("synthetic preserved settings", File.ReadAllText(second.SettingsPath));
        Assert.Equal(marker, File.ReadAllBytes(Path.Combine(root, ".diandao-demo-instance.json")));
    }

    [Fact]
    public void Unmarked_nonempty_directory_is_never_adopted_or_overwritten()
    {
        Directory.CreateDirectory(root);
        var sentinel = Path.Combine(root, "preserve.txt");
        File.WriteAllText(sentinel, "preserve");
        Assert.Throws<InvalidDataException>(() => DemoIntegrationContext.CreateOrOpen(root));
        Assert.Equal("preserve", File.ReadAllText(sentinel));
        Assert.Single(Directory.EnumerateFiles(root));
    }

    [Theory]
    [InlineData("{invalid")]
    [InlineData("{\"schema\":2,\"purpose\":\"diandao-isolated-demo\",\"instance\":\"wrong\"}")]
    [InlineData("{\"schema\":1,\"purpose\":\"not-a-demo\",\"instance\":\"wrong\"}")]
    public void Damaged_or_foreign_marker_is_rejected_without_repair(string marker)
    {
        DemoIntegrationContext.CreateOrOpen(root);
        var path = Path.Combine(root, ".diandao-demo-instance.json");
        File.WriteAllText(path, marker);
        Assert.Throws<InvalidDataException>(() => DemoIntegrationContext.CreateOrOpen(root));
        Assert.Equal(marker, File.ReadAllText(path));
    }

    public void Dispose()
    {
        var resolved = Path.GetFullPath(root);
        if (!string.Equals(Path.GetDirectoryName(resolved), DemoIntegrationContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
            throw new InvalidOperationException("Refusing cleanup outside this isolated demo instance.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
