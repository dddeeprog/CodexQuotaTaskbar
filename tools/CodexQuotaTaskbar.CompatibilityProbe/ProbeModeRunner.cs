using CodexQuotaTaskbar.CompatibilityProbe.Reporting;
using CodexQuotaTaskbar.CompatibilityProbe.Safety;
using CodexQuotaTaskbar.Core.Compatibility;
using System.Security;

namespace CodexQuotaTaskbar.CompatibilityProbe;

internal enum ProbeActivationStatus
{
    Ready,
    Unsupported,
    EvidenceUnavailable,
    PayloadUnavailable,
    Rejected,
    BoundaryFailure,
}

internal enum ProbeRecordedDetachStatus
{
    Clean,
    AlreadyClean,
    Unsafe,
}

internal sealed record ProbeActivationResult(
    ProbeActivationStatus Status,
    IActiveProbeSession? Session)
{
    internal static ProbeActivationResult Ready(IActiveProbeSession session) =>
        new(ProbeActivationStatus.Ready, session ?? throw new ArgumentNullException(nameof(session)));

    internal static ProbeActivationResult Failed(ProbeActivationStatus status)
    {
        if (status == ProbeActivationStatus.Ready)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new ProbeActivationResult(status, null);
    }
}

internal interface IActiveProbeSession : IDisposable
{
    ExplorerResponsivenessStatus SampleResponsiveness();

    bool MarkUnsafe();

    ProbeRecordedDetachStatus Detach();
}

internal interface IProbeRuntime
{
    ProbeActivationResult Activate(ProbeEvidence evidence, bool explicitRetry);

    ProbeRecordedDetachStatus DetachRecorded();

    bool RestartExplorerGracefully();
}

internal interface IProbeClock
{
    DateTimeOffset UtcNow { get; }

    void Delay(TimeSpan delay);
}

internal sealed class SystemProbeClock : IProbeClock
{
    internal static SystemProbeClock Instance { get; } = new();

    private SystemProbeClock()
    {
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public void Delay(TimeSpan delay) => Thread.Sleep(delay);
}

internal sealed class GuardedProbeModeRunner : IProbeModeRunner
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);
    private readonly IProbeEvidenceCollector evidenceCollector;
    private readonly IProbeRuntime runtime;
    private readonly CompatibilityReportWriter reportWriter;
    private readonly IProbeClock clock;
    private readonly TextWriter standardError;

    internal GuardedProbeModeRunner(
        IProbeEvidenceCollector evidenceCollector,
        IProbeRuntime runtime,
        CompatibilityReportWriter reportWriter,
        IProbeClock clock,
        TextWriter standardError)
    {
        this.evidenceCollector = evidenceCollector ??
            throw new ArgumentNullException(nameof(evidenceCollector));
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.reportWriter = reportWriter ?? throw new ArgumentNullException(nameof(reportWriter));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.standardError = standardError ?? throw new ArgumentNullException(nameof(standardError));
    }

    public ProbeExitCode Run(ProbeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Mode switch
        {
            ProbeMode.Live => RunLive(options),
            ProbeMode.Detach => RunDetach(),
            ProbeMode.RestartExplorerTest => RunRestart(options),
            _ => ProbeExitCode.InvalidArguments,
        };
    }

    private ProbeExitCode RunLive(ProbeOptions options)
    {
        if (!TryCollectEvidence(out var evidence))
        {
            return ProbeExitCode.CollectionFailed;
        }

        if (evidence.Decision == CompatibilityDecision.Unsupported)
        {
            return WriteLifecycle(
                options.OutputPath!,
                "live",
                evidence,
                new CompatibilityReportLifecycle(
                    ProbeActivationStatus.Unsupported.ToString(),
                    "NotRun",
                    "NotRun",
                    "NotRun",
                    options.DisplaySeconds),
                "Unsupported",
                ProbeExitCode.Unsafe);
        }

        ProbeActivationResult activation;
        try
        {
            activation = runtime.Activate(evidence, options.ExplicitRetry);
        }
        catch (Exception)
        {
            activation = ProbeActivationResult.Failed(ProbeActivationStatus.BoundaryFailure);
        }

        if (activation.Status != ProbeActivationStatus.Ready || activation.Session is null)
        {
            return WriteLifecycle(
                options.OutputPath!,
                "live",
                evidence,
                new CompatibilityReportLifecycle(
                    activation.Status.ToString(),
                    "NotRun",
                    "NotRun",
                    "NotRun",
                    options.DisplaySeconds),
                "Unsafe",
                ProbeExitCode.Unsafe);
        }

        var session = activation.Session;
        var responsiveness = ExplorerResponsivenessStatus.Unsafe;
        var detach = ProbeRecordedDetachStatus.Unsafe;
        try
        {
            for (var elapsedSeconds = 0; elapsedSeconds <= options.DisplaySeconds; elapsedSeconds++)
            {
                responsiveness = session.SampleResponsiveness();
                if (responsiveness == ExplorerResponsivenessStatus.Unsafe)
                {
                    break;
                }

                if (elapsedSeconds < options.DisplaySeconds)
                {
                    clock.Delay(SampleInterval);
                }
            }
        }
        catch (Exception)
        {
            responsiveness = ExplorerResponsivenessStatus.Unsafe;
        }
        finally
        {
            if (responsiveness == ExplorerResponsivenessStatus.Unsafe)
            {
                try
                {
                    _ = session.MarkUnsafe();
                }
                catch (Exception)
                {
                    // Detach is still required; the existing journal remains fail-closed.
                }
            }

            try
            {
                detach = session.Detach();
            }
            catch (Exception)
            {
                detach = ProbeRecordedDetachStatus.Unsafe;
            }

            try
            {
                session.Dispose();
            }
            catch (Exception)
            {
                detach = ProbeRecordedDetachStatus.Unsafe;
            }
        }

        var compatible = responsiveness == ExplorerResponsivenessStatus.Responsive &&
            detach is ProbeRecordedDetachStatus.Clean or ProbeRecordedDetachStatus.AlreadyClean;
        return WriteLifecycle(
            options.OutputPath!,
            "live",
            evidence,
            new CompatibilityReportLifecycle(
                ProbeActivationStatus.Ready.ToString(),
                responsiveness.ToString(),
                detach.ToString(),
                "NotRun",
                options.DisplaySeconds),
            compatible ? "Compatible" : "Unsafe",
            compatible ? ProbeExitCode.Success : ProbeExitCode.Unsafe);
    }

    private ProbeExitCode RunDetach()
    {
        try
        {
            return runtime.DetachRecorded() is
                ProbeRecordedDetachStatus.Clean or ProbeRecordedDetachStatus.AlreadyClean
                    ? ProbeExitCode.Success
                    : ProbeExitCode.Unsafe;
        }
        catch (Exception)
        {
            return ProbeExitCode.Unsafe;
        }
    }

    private ProbeExitCode RunRestart(ProbeOptions options)
    {
        if (!TryCollectEvidence(out var evidence))
        {
            return ProbeExitCode.CollectionFailed;
        }

        ProbeRecordedDetachStatus detach;
        var restarted = false;
        try
        {
            detach = runtime.DetachRecorded();
            if (detach is ProbeRecordedDetachStatus.Clean or ProbeRecordedDetachStatus.AlreadyClean)
            {
                restarted = runtime.RestartExplorerGracefully();
            }
        }
        catch (Exception)
        {
            detach = ProbeRecordedDetachStatus.Unsafe;
        }

        var compatible = restarted &&
            detach is ProbeRecordedDetachStatus.Clean or ProbeRecordedDetachStatus.AlreadyClean;
        return WriteLifecycle(
            options.OutputPath!,
            "restartExplorerTest",
            evidence,
            new CompatibilityReportLifecycle(
                "NotRun",
                "NotRun",
                detach.ToString(),
                restarted ? "Completed" : "Unsafe",
                0),
            compatible ? "Compatible" : "Unsafe",
            compatible ? ProbeExitCode.Success : ProbeExitCode.Unsafe);
    }

    private bool TryCollectEvidence(out ProbeEvidence evidence)
    {
        try
        {
            evidence = evidenceCollector.Collect();
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or UnauthorizedAccessException or
                SecurityException or ArgumentException or NotSupportedException)
        {
            standardError.WriteLine("Probe evidence collection failed.");
            evidence = null!;
            return false;
        }
    }

    private ProbeExitCode WriteLifecycle(
        string outputPath,
        string mode,
        ProbeEvidence evidence,
        CompatibilityReportLifecycle lifecycle,
        string result,
        ProbeExitCode successCode)
    {
        var report = CompatibilityReport.CreateLifecycle(
            mode,
            evidence,
            lifecycle,
            result,
            clock.UtcNow);
        var write = reportWriter.Write(report, outputPath);
        if (write.Succeeded)
        {
            return successCode;
        }

        standardError.WriteLine($"Probe report write failed: {write.Error}");
        return ProbeExitCode.CollectionFailed;
    }
}
