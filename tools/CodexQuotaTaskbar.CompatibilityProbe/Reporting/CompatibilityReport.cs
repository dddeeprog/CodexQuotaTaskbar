using System.Runtime.InteropServices;
using CodexQuotaTaskbar.CompatibilityProbe.Windows;
using CodexQuotaTaskbar.Core.Compatibility;

namespace CodexQuotaTaskbar.CompatibilityProbe.Reporting;

internal sealed class CompatibilityReport
{
    private CompatibilityReport(
        DateTimeOffset generatedAtUtc,
        string mode,
        CompatibilityReportWindows windows,
        CompatibilityReportFile? explorer,
        IReadOnlyList<CompatibilityReportFile> modules,
        bool moduleListKnown,
        string? xamlTypeParentSignature,
        int? monitorCount,
        int? taskbarHostCount,
        IReadOnlyList<CompatibilityReportMonitorDpi> monitorDpiValues,
        bool displayEvidenceKnown,
        string collectionStatus,
        string failureReason,
        CompatibilityReportLifecycle? lifecycle,
        string result)
    {
        GeneratedAtUtc = generatedAtUtc;
        Mode = mode;
        Windows = windows;
        Explorer = explorer;
        Modules = modules;
        ModuleListKnown = moduleListKnown;
        XamlTypeParentSignature = xamlTypeParentSignature;
        MonitorCount = monitorCount;
        TaskbarHostCount = taskbarHostCount;
        MonitorDpiValues = monitorDpiValues;
        DisplayEvidenceKnown = displayEvidenceKnown;
        CollectionStatus = collectionStatus;
        FailureReason = failureReason;
        Lifecycle = lifecycle;
        Result = result;
    }

    public int SchemaVersion => 1;

    public DateTimeOffset GeneratedAtUtc { get; }

    public string Mode { get; }

    public CompatibilityReportWindows Windows { get; }

    public CompatibilityReportFile? Explorer { get; }

    public IReadOnlyList<CompatibilityReportFile> Modules { get; }

    public bool ModuleListKnown { get; }

    public string? XamlTypeParentSignature { get; }

    public int? MonitorCount { get; }

    public int? TaskbarHostCount { get; }

    public IReadOnlyList<CompatibilityReportMonitorDpi> MonitorDpiValues { get; }

    public bool DisplayEvidenceKnown { get; }

    public string CollectionStatus { get; }

    public string FailureReason { get; }

    public CompatibilityReportLifecycle? Lifecycle { get; }

    public string Result { get; }

    internal static CompatibilityReport CreateCollectOnly(
        WindowsBuildIdentity identity,
        TaskbarModuleEvidence evidence,
        CompatibilityDecision decision,
        DateTimeOffset generatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(evidence);

        return new CompatibilityReport(
            generatedAtUtc.ToUniversalTime(),
            "collectOnly",
            new CompatibilityReportWindows(
                identity.MajorVersion,
                identity.MinorVersion,
                identity.BuildNumber,
                identity.UpdateBuildRevision,
                ArchitectureName(identity.Architecture),
                identity.IsComplete),
            evidence.Explorer is null ? null : CompatibilityReportFile.Create(evidence.Explorer),
            Array.AsReadOnly(evidence.Modules.Select(CompatibilityReportFile.Create).ToArray()),
            evidence.ModuleListKnown,
            SanitizeOptionalSignature(evidence.XamlTypeParentSignature),
            evidence.MonitorCount,
            evidence.TaskbarHostCount,
            Array.AsReadOnly(evidence.MonitorDpiValues
                .Select(value => new CompatibilityReportMonitorDpi(
                    value.MonitorIndex,
                    value.DpiX,
                    value.DpiY))
                .ToArray()),
            evidence.DisplayEvidenceKnown,
            evidence.CollectionStatus.ToString(),
            evidence.FailureReason.ToString(),
            lifecycle: null,
            decision.ToString());
    }

    internal static CompatibilityReport CreateLifecycle(
        string mode,
        ProbeEvidence evidence,
        CompatibilityReportLifecycle lifecycle,
        string result,
        DateTimeOffset generatedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentException.ThrowIfNullOrWhiteSpace(result);

        var collectOnly = CreateCollectOnly(
            evidence.Identity,
            evidence.Taskbar,
            evidence.Decision,
            generatedAtUtc);
        return new CompatibilityReport(
            collectOnly.GeneratedAtUtc,
            mode,
            collectOnly.Windows,
            collectOnly.Explorer,
            collectOnly.Modules,
            collectOnly.ModuleListKnown,
            collectOnly.XamlTypeParentSignature,
            collectOnly.MonitorCount,
            collectOnly.TaskbarHostCount,
            collectOnly.MonitorDpiValues,
            collectOnly.DisplayEvidenceKnown,
            collectOnly.CollectionStatus,
            collectOnly.FailureReason,
            lifecycle,
            result);
    }

    private static string ArchitectureName(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm",
        _ => throw new ArgumentOutOfRangeException(nameof(architecture)),
    };

    private static string? SanitizeOptionalSignature(string? signature)
    {
        if (signature is null)
        {
            return null;
        }

        if (signature.Length is 0 or > 512 || signature.Any(char.IsControl) ||
            signature.Contains('\\') || signature.Contains('/'))
        {
            throw new ArgumentException("The type signature is not sanitized.", nameof(signature));
        }

        return signature;
    }
}

internal sealed record CompatibilityReportWindows(
    int? MajorVersion,
    int? MinorVersion,
    int? BuildNumber,
    int? UpdateBuildRevision,
    string Architecture,
    bool IsComplete);

internal sealed record CompatibilityReportFile(
    string FileName,
    string? FileVersion,
    string Sha256)
{
    internal static CompatibilityReportFile Create(ModuleFileEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (string.IsNullOrWhiteSpace(evidence.FileName) ||
            !string.Equals(evidence.FileName, Path.GetFileName(evidence.FileName), StringComparison.Ordinal) ||
            evidence.FileName.Any(char.IsControl))
        {
            throw new ArgumentException("The evidence file name is not sanitized.", nameof(evidence));
        }

        if (evidence.FileVersion is { } version &&
            (version.Length is 0 or > 128 || version.Any(char.IsControl) ||
             version.Contains('\\') || version.Contains('/')))
        {
            throw new ArgumentException("The evidence file version is not sanitized.", nameof(evidence));
        }

        if (evidence.Sha256.Length != 64 || !evidence.Sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("The evidence digest is invalid.", nameof(evidence));
        }

        return new CompatibilityReportFile(
            evidence.FileName,
            evidence.FileVersion,
            evidence.Sha256.ToLowerInvariant());
    }
}

internal readonly record struct CompatibilityReportMonitorDpi(
    int MonitorIndex,
    int DpiX,
    int DpiY);

internal sealed record CompatibilityReportLifecycle(
    string Activation,
    string Responsiveness,
    string Detach,
    string Restart,
    int DisplaySeconds);
