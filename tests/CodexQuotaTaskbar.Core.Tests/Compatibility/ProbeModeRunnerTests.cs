using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CodexQuotaTaskbar.CompatibilityProbe;
using CodexQuotaTaskbar.CompatibilityProbe.Reporting;
using CodexQuotaTaskbar.CompatibilityProbe.Safety;
using CodexQuotaTaskbar.CompatibilityProbe.Windows;
using CodexQuotaTaskbar.Core.Compatibility;

namespace CodexQuotaTaskbar.Core.Tests.Compatibility;

public sealed class ProbeModeRunnerTests
{
    [Fact]
    public void Missing_packaged_bridge_resources_are_reported_as_unavailable()
    {
        var source = new EmbeddedBridgePayloadSource(typeof(ProbeModeRunnerTests).Assembly);

        var found = source.TryGetPayload(out var descriptor);

        Assert.False(found);
        Assert.Null(descriptor);
    }

    [Fact]
    public void Live_success_samples_responsiveness_detaches_and_writes_compatible_report()
    {
        var runtime = new FakeProbeRuntime();
        var clock = new FakeProbeClock();
        var storage = new FakeReportStorage();
        var runner = CreateRunner(runtime, clock, storage);

        var exitCode = runner.Run(new ProbeOptions(
            ProbeMode.Live,
            @"C:\reports\live.json",
            ExplicitRetry: false,
            DisplaySeconds: 2));

        Assert.Equal(ProbeExitCode.Success, exitCode);
        Assert.Equal(1, runtime.ActivateCount);
        Assert.Equal(3, runtime.Session.SampleCount);
        Assert.Equal(2, clock.Delays.Count);
        Assert.Equal(1, runtime.Session.DetachCount);
        Assert.Equal(0, runtime.Session.MarkUnsafeCount);
        Assert.True(runtime.Session.Disposed);
        using var report = JsonDocument.Parse(storage.Content!);
        Assert.Equal("live", report.RootElement.GetProperty("mode").GetString());
        Assert.Equal("Compatible", report.RootElement.GetProperty("result").GetString());
        var lifecycle = report.RootElement.GetProperty("lifecycle");
        Assert.Equal("Ready", lifecycle.GetProperty("activation").GetString());
        Assert.Equal("Responsive", lifecycle.GetProperty("responsiveness").GetString());
        Assert.Equal("Clean", lifecycle.GetProperty("detach").GetString());
        Assert.Equal(2, lifecycle.GetProperty("displaySeconds").GetInt32());
    }

    [Fact]
    public void Three_responsiveness_failures_stop_display_detach_and_report_unsafe()
    {
        var runtime = new FakeProbeRuntime();
        runtime.Session.Responsiveness.Enqueue(ExplorerResponsivenessStatus.TransientFailure);
        runtime.Session.Responsiveness.Enqueue(ExplorerResponsivenessStatus.TransientFailure);
        runtime.Session.Responsiveness.Enqueue(ExplorerResponsivenessStatus.Unsafe);
        var clock = new FakeProbeClock();
        var storage = new FakeReportStorage();
        var runner = CreateRunner(runtime, clock, storage);

        var exitCode = runner.Run(new ProbeOptions(
            ProbeMode.Live,
            @"C:\reports\live.json",
            ExplicitRetry: false,
            DisplaySeconds: 5));

        Assert.Equal(ProbeExitCode.Unsafe, exitCode);
        Assert.Equal(3, runtime.Session.SampleCount);
        Assert.Equal(2, clock.Delays.Count);
        Assert.Equal(1, runtime.Session.MarkUnsafeCount);
        Assert.Equal(1, runtime.Session.DetachCount);
        using var report = JsonDocument.Parse(storage.Content!);
        Assert.Equal("Unsafe", report.RootElement.GetProperty("result").GetString());
        Assert.Equal(
            "Unsafe",
            report.RootElement.GetProperty("lifecycle")
                .GetProperty("responsiveness").GetString());
    }

    [Fact]
    public void Rejected_activation_writes_unsafe_report_without_waiting_or_detaching()
    {
        var runtime = new FakeProbeRuntime
        {
            Activation = ProbeActivationResult.Failed(ProbeActivationStatus.PayloadUnavailable),
        };
        var clock = new FakeProbeClock();
        var storage = new FakeReportStorage();
        var runner = CreateRunner(runtime, clock, storage);

        var exitCode = runner.Run(new ProbeOptions(
            ProbeMode.Live,
            @"C:\reports\live.json",
            ExplicitRetry: true,
            DisplaySeconds: 5));

        Assert.Equal(ProbeExitCode.Unsafe, exitCode);
        Assert.True(runtime.LastExplicitRetry);
        Assert.Empty(clock.Delays);
        Assert.Equal(0, runtime.Session.DetachCount);
        using var report = JsonDocument.Parse(storage.Content!);
        Assert.Equal(
            "PayloadUnavailable",
            report.RootElement.GetProperty("lifecycle")
                .GetProperty("activation").GetString());
    }

    [Fact]
    public void Detach_mode_maps_the_recorded_detach_outcome()
    {
        foreach (var testCase in new[]
                 {
                     (ProbeRecordedDetachStatus.Clean, ProbeExitCode.Success),
                     (ProbeRecordedDetachStatus.AlreadyClean, ProbeExitCode.Success),
                     (ProbeRecordedDetachStatus.Unsafe, ProbeExitCode.Unsafe),
                 })
        {
            var runtime = new FakeProbeRuntime { RecordedDetach = testCase.Item1 };
            var runner = CreateRunner(runtime, new FakeProbeClock(), new FakeReportStorage());

            var exitCode = runner.Run(new ProbeOptions(
                ProbeMode.Detach,
                OutputPath: null,
                ExplicitRetry: false,
                DisplaySeconds: 5));

            Assert.Equal(testCase.Item2, exitCode);
            Assert.Equal(1, runtime.RecordedDetachCount);
            Assert.Equal(0, runtime.RestartCount);
        }
    }

    [Fact]
    public void Restart_mode_detaches_before_requesting_a_graceful_restart_and_writes_report()
    {
        var runtime = new FakeProbeRuntime();
        var storage = new FakeReportStorage();
        var runner = CreateRunner(runtime, new FakeProbeClock(), storage);

        var exitCode = runner.Run(new ProbeOptions(
            ProbeMode.RestartExplorerTest,
            @"C:\reports\restart.json",
            ExplicitRetry: false,
            DisplaySeconds: 5));

        Assert.Equal(ProbeExitCode.Success, exitCode);
        Assert.Equal(["DetachRecorded", "Restart"], runtime.Trace);
        using var report = JsonDocument.Parse(storage.Content!);
        Assert.Equal("restartExplorerTest", report.RootElement.GetProperty("mode").GetString());
        Assert.Equal("Compatible", report.RootElement.GetProperty("result").GetString());
    }

    private static GuardedProbeModeRunner CreateRunner(
        FakeProbeRuntime runtime,
        FakeProbeClock clock,
        FakeReportStorage storage) =>
        new(
            new FakeEvidenceCollector(),
            runtime,
            new CompatibilityReportWriter(storage),
            clock,
            new StringWriter());

    private sealed class FakeEvidenceCollector : IProbeEvidenceCollector
    {
        public ProbeEvidence Collect() => new(
            new WindowsBuildIdentity(10, 0, 26200, 1, Architecture.X64),
            TaskbarModuleEvidence.Unknown(EvidenceCollectionStatus.NoDiscoveryModules),
            CompatibilityDecision.ProbeRequired);
    }

    private sealed class FakeProbeRuntime : IProbeRuntime
    {
        internal FakeActiveProbeSession Session { get; } = new();

        internal ProbeActivationResult Activation { get; init; }

        internal ProbeRecordedDetachStatus RecordedDetach { get; init; } =
            ProbeRecordedDetachStatus.Clean;

        internal bool RestartSucceeded { get; init; } = true;

        internal int ActivateCount { get; private set; }

        internal bool LastExplicitRetry { get; private set; }

        internal int RecordedDetachCount { get; private set; }

        internal int RestartCount { get; private set; }

        internal List<string> Trace { get; } = [];

        internal FakeProbeRuntime() => Activation = ProbeActivationResult.Ready(Session);

        public ProbeActivationResult Activate(ProbeEvidence evidence, bool explicitRetry)
        {
            _ = evidence;
            ActivateCount++;
            LastExplicitRetry = explicitRetry;
            return Activation;
        }

        public ProbeRecordedDetachStatus DetachRecorded()
        {
            Trace.Add("DetachRecorded");
            RecordedDetachCount++;
            return RecordedDetach;
        }

        public bool RestartExplorerGracefully()
        {
            Trace.Add("Restart");
            RestartCount++;
            return RestartSucceeded;
        }
    }

    private sealed class FakeActiveProbeSession : IActiveProbeSession
    {
        internal Queue<ExplorerResponsivenessStatus> Responsiveness { get; } = new();

        internal int SampleCount { get; private set; }

        internal int DetachCount { get; private set; }

        internal int MarkUnsafeCount { get; private set; }

        internal bool Disposed { get; private set; }

        public ExplorerResponsivenessStatus SampleResponsiveness()
        {
            SampleCount++;
            return Responsiveness.TryDequeue(out var status)
                ? status
                : ExplorerResponsivenessStatus.Responsive;
        }

        public ProbeRecordedDetachStatus Detach()
        {
            DetachCount++;
            return ProbeRecordedDetachStatus.Clean;
        }

        public bool MarkUnsafe()
        {
            MarkUnsafeCount++;
            return true;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeProbeClock : IProbeClock
    {
        internal List<TimeSpan> Delays { get; } = [];

        public DateTimeOffset UtcNow =>
            new(2026, 8, 2, 1, 2, 3, TimeSpan.Zero);

        public void Delay(TimeSpan delay) => Delays.Add(delay);
    }

    private sealed class FakeReportStorage : ICompatibilityReportStorage
    {
        internal byte[]? Content { get; private set; }

        public void WriteAtomically(string path, ReadOnlyMemory<byte> content)
        {
            Assert.True(Path.IsPathFullyQualified(path));
            Content = content.ToArray();
            Assert.DoesNotContain(
                @"C:\",
                Encoding.UTF8.GetString(Content),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
