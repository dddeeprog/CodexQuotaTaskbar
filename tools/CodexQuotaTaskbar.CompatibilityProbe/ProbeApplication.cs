using CodexQuotaTaskbar.CompatibilityProbe.Reporting;
using CodexQuotaTaskbar.CompatibilityProbe.Windows;
using CodexQuotaTaskbar.Core.Compatibility;
using System.Security;

namespace CodexQuotaTaskbar.CompatibilityProbe;

internal enum ProbeExitCode
{
    Success = 0,
    InvalidArguments = 2,
    CollectionFailed = 3,
    Unsafe = 4,
}

internal sealed record ProbeEvidence(
    WindowsBuildIdentity Identity,
    TaskbarModuleEvidence Taskbar,
    CompatibilityDecision Decision);

internal interface IProbeEvidenceCollector
{
    ProbeEvidence Collect();
}

internal interface IProbeModeRunner
{
    ProbeExitCode Run(ProbeOptions options);
}

internal sealed class ProbeApplication
{
    private readonly IProbeEvidenceCollector evidenceCollector;
    private readonly IProbeModeRunner modeRunner;
    private readonly CompatibilityReportWriter reportWriter;
    private readonly TextWriter standardError;

    internal ProbeApplication(
        IProbeEvidenceCollector evidenceCollector,
        IProbeModeRunner modeRunner,
        CompatibilityReportWriter reportWriter,
        TextWriter standardError)
    {
        this.evidenceCollector = evidenceCollector ??
            throw new ArgumentNullException(nameof(evidenceCollector));
        this.modeRunner = modeRunner ?? throw new ArgumentNullException(nameof(modeRunner));
        this.reportWriter = reportWriter ?? throw new ArgumentNullException(nameof(reportWriter));
        this.standardError = standardError ?? throw new ArgumentNullException(nameof(standardError));
    }

    internal static ProbeApplication CreateDefault()
    {
        var evidence = new WindowsProbeEvidenceCollector();
        var reports = new CompatibilityReportWriter();
        return new ProbeApplication(
            evidence,
            new GuardedProbeModeRunner(
                evidence,
                new PackagedProbeRuntime(),
                reports,
                SystemProbeClock.Instance,
                Console.Error),
            reports,
            Console.Error);
    }

    internal ProbeExitCode Run(
        IReadOnlyList<string> arguments,
        string currentDirectory,
        IProbePathInspector? pathInspector = null)
    {
        var parsed = ProbeOptions.Parse(arguments, currentDirectory, pathInspector);
        if (!parsed.Succeeded)
        {
            standardError.WriteLine($"Invalid probe arguments: {parsed.Error}");
            return ProbeExitCode.InvalidArguments;
        }

        var options = parsed.Options!;
        if (options.Mode != ProbeMode.CollectOnly)
        {
            return modeRunner.Run(options);
        }

        try
        {
            var evidence = evidenceCollector.Collect();
            var report = CompatibilityReport.CreateCollectOnly(
                evidence.Identity,
                evidence.Taskbar,
                evidence.Decision,
                DateTimeOffset.UtcNow);
            var write = reportWriter.Write(report, options.OutputPath!);
            if (!write.Succeeded)
            {
                standardError.WriteLine($"Probe report write failed: {write.Error}");
                return ProbeExitCode.CollectionFailed;
            }

            return ProbeExitCode.Success;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or UnauthorizedAccessException or
                SecurityException or ArgumentException or NotSupportedException)
        {
            standardError.WriteLine("Probe evidence collection failed.");
            return ProbeExitCode.CollectionFailed;
        }
    }
}

internal sealed class WindowsProbeEvidenceCollector : IProbeEvidenceCollector
{
    public ProbeEvidence Collect()
    {
        var identity = new WindowsIdentityReader().Read();
        var evidence = new ModuleSignatureReader().Read();
        var compatibility = CompatibilityPolicy.EvaluatePreflight(identity, evidence.Signature);
        return new ProbeEvidence(identity, evidence, compatibility.Decision);
    }
}
