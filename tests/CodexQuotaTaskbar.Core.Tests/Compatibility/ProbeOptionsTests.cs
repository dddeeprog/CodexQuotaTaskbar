using CodexQuotaTaskbar.CompatibilityProbe;

namespace CodexQuotaTaskbar.Core.Tests.Compatibility;

public sealed class ProbeOptionsTests
{
    [Fact]
    public void Collect_only_normalizes_a_relative_json_path_once()
    {
        var paths = new FakeProbePathInspector();
        paths.AddDirectory(@"C:\work");
        paths.AddDirectory(@"C:\work\reports");

        var result = ProbeOptions.Parse(
            ["--collect-only", "--output", @"reports\.\probe.json"],
            @"C:\work",
            paths);

        Assert.True(result.Succeeded);
        Assert.Equal(ProbeMode.CollectOnly, result.Options!.Mode);
        Assert.Equal(@"C:\work\reports\probe.json", result.Options.OutputPath);
        Assert.False(result.Options.ExplicitRetry);
        Assert.Equal(5, result.Options.DisplaySeconds);
    }

    [Fact]
    public void Live_accepts_an_absolute_output_and_explicit_retry()
    {
        var paths = new FakeProbePathInspector();
        paths.AddDirectory(@"D:\evidence");

        var result = ProbeOptions.Parse(
            ["--live", "--display-seconds", "5", "--explicit-retry",
             "--output", @"D:\evidence\live.json"],
            @"C:\work",
            paths);

        Assert.True(result.Succeeded);
        Assert.Equal(ProbeMode.Live, result.Options!.Mode);
        Assert.True(result.Options.ExplicitRetry);
        Assert.Equal(5, result.Options.DisplaySeconds);
        Assert.Equal(@"D:\evidence\live.json", result.Options.OutputPath);
    }

    [Theory]
    [InlineData("--collect-only", "--live")]
    [InlineData("--detach", "--restart-explorer-test")]
    [InlineData("--live", "--live")]
    public void Rejects_multiple_or_repeated_modes(string first, string second)
    {
        var result = ProbeOptions.Parse(
            [first, second, "--output", @"C:\work\result.json"],
            @"C:\work",
            FakeProbePathInspector.WithDirectory(@"C:\work"));

        Assert.False(result.Succeeded);
        Assert.Equal(ProbeOptionsError.InvalidMode, result.Error);
    }

    [Theory]
    [InlineData("--collect-only")]
    [InlineData("--restart-explorer-test")]
    [InlineData("--live")]
    public void Report_modes_require_an_output_path(string mode)
    {
        var result = ProbeOptions.Parse(
            [mode], @"C:\work", FakeProbePathInspector.WithDirectory(@"C:\work"));

        Assert.False(result.Succeeded);
        Assert.Equal(ProbeOptionsError.OutputRequired, result.Error);
    }

    [Fact]
    public void Detach_rejects_output_and_live_only_switches()
    {
        var output = ProbeOptions.Parse(
            ["--detach", "--output", @"C:\work\result.json"],
            @"C:\work",
            FakeProbePathInspector.WithDirectory(@"C:\work"));
        var retry = ProbeOptions.Parse(
            ["--detach", "--explicit-retry"],
            @"C:\work",
            FakeProbePathInspector.WithDirectory(@"C:\work"));

        Assert.Equal(ProbeOptionsError.SwitchNotAllowed, output.Error);
        Assert.Equal(ProbeOptionsError.SwitchNotAllowed, retry.Error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("31")]
    [InlineData("NaN")]
    public void Live_rejects_invalid_display_seconds(string value)
    {
        var result = ProbeOptions.Parse(
            ["--live", "--display-seconds", value,
             "--output", @"C:\work\result.json"],
            @"C:\work",
            FakeProbePathInspector.WithDirectory(@"C:\work"));

        Assert.False(result.Succeeded);
        Assert.Equal(ProbeOptionsError.InvalidDisplaySeconds, result.Error);
    }

    [Theory]
    [InlineData(@"C:\work\result.txt")]
    [InlineData(@"C:\work\")]
    [InlineData(@"C:\work\existing")]
    public void Rejects_non_json_or_directory_targets(string output)
    {
        var paths = FakeProbePathInspector.WithDirectory(@"C:\work");
        paths.AddDirectory(@"C:\work\existing");

        var result = ProbeOptions.Parse(
            ["--collect-only", "--output", output], @"C:\work", paths);

        Assert.False(result.Succeeded);
        Assert.Equal(ProbeOptionsError.InvalidOutputPath, result.Error);
    }

    [Fact]
    public void Rejects_a_reparse_target_or_existing_ancestor()
    {
        var targetReparse = FakeProbePathInspector.WithDirectory(@"C:\work");
        targetReparse.AddFile(@"C:\work\result.json", FileAttributes.ReparsePoint);
        var ancestorReparse = FakeProbePathInspector.WithDirectory(@"C:\work");
        ancestorReparse.AddDirectory(@"C:\work\linked", FileAttributes.Directory | FileAttributes.ReparsePoint);

        var target = ProbeOptions.Parse(
            ["--collect-only", "--output", @"C:\work\result.json"],
            @"C:\work", targetReparse);
        var ancestor = ProbeOptions.Parse(
            ["--collect-only", "--output", @"C:\work\linked\result.json"],
            @"C:\work", ancestorReparse);

        Assert.Equal(ProbeOptionsError.ReparseOutputPath, target.Error);
        Assert.Equal(ProbeOptionsError.ReparseOutputPath, ancestor.Error);
    }

    [Theory]
    [InlineData(@"C:\work\reports\probe.json")]
    [InlineData(@"C:\work\reports")]
    [InlineData(@"C:\work")]
    public void Path_inspection_failure_at_the_target_parent_or_ancestor_is_typed(
        string failingPath)
    {
        var paths = new FakeProbePathInspector();
        paths.AddDirectory(@"C:\work");
        paths.AddDirectory(@"C:\work\reports");
        paths.FailAt(failingPath);
        ProbeOptionsParseResult? result = null;

        var exception = Record.Exception(() => result = ProbeOptions.Parse(
            ["--collect-only", "--output", @"C:\work\reports\probe.json"],
            @"C:\work",
            paths));

        Assert.Null(exception);
        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.Equal("PathInspectionFailure", result.Error.ToString());
    }

    [Fact]
    public void Rejects_unknown_or_missing_values_without_touching_the_filesystem()
    {
        var paths = new FakeProbePathInspector();

        var unknown = ProbeOptions.Parse(["--wat"], @"C:\work", paths);
        var missing = ProbeOptions.Parse(
            ["--collect-only", "--output"], @"C:\work", paths);

        Assert.Equal(ProbeOptionsError.UnknownArgument, unknown.Error);
        Assert.Equal(ProbeOptionsError.MissingValue, missing.Error);
        Assert.Equal(0, paths.AttributeReads);
    }

    private sealed class FakeProbePathInspector : IProbePathInspector
    {
        private readonly Dictionary<string, FileAttributes> entries =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> failingPaths =
            new(StringComparer.OrdinalIgnoreCase);

        internal int AttributeReads { get; private set; }

        internal static FakeProbePathInspector WithDirectory(string path)
        {
            var result = new FakeProbePathInspector();
            result.AddDirectory(path);
            return result;
        }

        internal void AddDirectory(
            string path,
            FileAttributes attributes = FileAttributes.Directory) =>
            entries[Path.GetFullPath(path)] = attributes | FileAttributes.Directory;

        internal void AddFile(
            string path,
            FileAttributes attributes = FileAttributes.Normal) =>
            entries[Path.GetFullPath(path)] = attributes & ~FileAttributes.Directory;

        internal void FailAt(string path) =>
            failingPaths.Add(Path.GetFullPath(path));

        public bool TryGetAttributes(string path, out FileAttributes attributes)
        {
            AttributeReads++;
            var fullPath = Path.GetFullPath(path);
            if (failingPaths.Contains(fullPath))
            {
                throw new IOException("fake path inspection failure");
            }

            return entries.TryGetValue(fullPath, out attributes);
        }
    }
}
