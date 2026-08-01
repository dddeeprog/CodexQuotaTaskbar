using System.Runtime.InteropServices;
using CodexQuotaTaskbar.CompatibilityProbe;
using CodexQuotaTaskbar.CompatibilityProbe.Reporting;
using CodexQuotaTaskbar.CompatibilityProbe.Windows;
using CodexQuotaTaskbar.Core.Compatibility;

namespace CodexQuotaTaskbar.Core.Tests.Compatibility;

public sealed class ProbeApplicationTests
{
    [Fact]
    public void Collect_only_writes_the_report_without_calling_a_mutating_mode()
    {
        var evidence = new FakeEvidenceCollector();
        var modes = new FakeProbeModeRunner();
        var storage = new FakeReportStorage();
        var app = new ProbeApplication(
            evidence,
            modes,
            new CompatibilityReportWriter(storage),
            new StringWriter());
        var paths = new FakePathInspector(@"C:\reports");

        var exitCode = app.Run(
            ["--collect-only", "--output", @"C:\reports\probe.json"],
            @"C:\work",
            paths);

        Assert.Equal(ProbeExitCode.Success, exitCode);
        Assert.Equal(1, evidence.CollectionCount);
        Assert.Equal(0, modes.RunCount);
        Assert.Equal(@"C:\reports\probe.json", storage.Path);
    }

    [Fact]
    public void Invalid_arguments_fail_before_evidence_or_mode_services_run()
    {
        var evidence = new FakeEvidenceCollector();
        var modes = new FakeProbeModeRunner();
        var storage = new FakeReportStorage();
        var errors = new StringWriter();
        var app = new ProbeApplication(
            evidence,
            modes,
            new CompatibilityReportWriter(storage),
            errors);

        var exitCode = app.Run(
            ["--collect-only"],
            @"C:\work",
            new FakePathInspector(@"C:\work"));

        Assert.Equal(ProbeExitCode.InvalidArguments, exitCode);
        Assert.Equal(0, evidence.CollectionCount);
        Assert.Equal(0, modes.RunCount);
        Assert.Null(storage.Path);
        Assert.Contains("OutputRequired", errors.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--live")]
    [InlineData("--restart-explorer-test")]
    public void Non_collect_report_modes_are_delegated_without_precollecting(string mode)
    {
        var evidence = new FakeEvidenceCollector();
        var modes = new FakeProbeModeRunner { ExitCode = ProbeExitCode.Unsafe };
        var app = new ProbeApplication(
            evidence,
            modes,
            new CompatibilityReportWriter(new FakeReportStorage()),
            new StringWriter());

        var exitCode = app.Run(
            [mode, "--output", @"C:\reports\probe.json"],
            @"C:\work",
            new FakePathInspector(@"C:\reports"));

        Assert.Equal(ProbeExitCode.Unsafe, exitCode);
        Assert.Equal(0, evidence.CollectionCount);
        Assert.Equal(1, modes.RunCount);
    }

    [Fact]
    public void Detach_is_delegated_without_output_path_access()
    {
        var paths = new FakePathInspector();
        var modes = new FakeProbeModeRunner();
        var app = new ProbeApplication(
            new FakeEvidenceCollector(),
            modes,
            new CompatibilityReportWriter(new FakeReportStorage()),
            new StringWriter());

        var exitCode = app.Run(["--detach"], @"C:\work", paths);

        Assert.Equal(ProbeExitCode.Success, exitCode);
        Assert.Equal(1, modes.RunCount);
        Assert.Equal(0, paths.ReadCount);
    }

    private sealed class FakeEvidenceCollector : IProbeEvidenceCollector
    {
        internal int CollectionCount { get; private set; }

        public ProbeEvidence Collect()
        {
            CollectionCount++;
            return new ProbeEvidence(
                new WindowsBuildIdentity(10, 0, 26200, 1, Architecture.X64),
                TaskbarModuleEvidence.Unknown(EvidenceCollectionStatus.NoDiscoveryModules),
                CompatibilityDecision.ProbeRequired);
        }
    }

    private sealed class FakeProbeModeRunner : IProbeModeRunner
    {
        internal int RunCount { get; private set; }

        internal ProbeExitCode ExitCode { get; init; } = ProbeExitCode.Success;

        public ProbeExitCode Run(ProbeOptions options)
        {
            RunCount++;
            return ExitCode;
        }
    }

    private sealed class FakeReportStorage : ICompatibilityReportStorage
    {
        internal string? Path { get; private set; }

        public void WriteAtomically(string path, ReadOnlyMemory<byte> content) => Path = path;
    }

    private sealed class FakePathInspector : IProbePathInspector
    {
        private readonly HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);

        internal FakePathInspector(params string[] directories)
        {
            foreach (var directory in directories)
            {
                this.directories.Add(Path.GetFullPath(directory));
            }
        }

        internal int ReadCount { get; private set; }

        public bool TryGetAttributes(string path, out FileAttributes attributes)
        {
            ReadCount++;
            if (directories.Contains(Path.GetFullPath(path)))
            {
                attributes = FileAttributes.Directory;
                return true;
            }

            attributes = default;
            return false;
        }
    }
}
