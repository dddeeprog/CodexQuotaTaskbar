using System.Collections.ObjectModel;

namespace CodexQuotaTaskbar.Core.Compatibility;

internal sealed class TaskbarSignature
{
    private const int MaxTaskbarHostBindings = 16;
    internal const string RequiredXamlTypeParentSignature = "SystemTray.SystemTrayFrame>Grid";

    private TaskbarSignature(
        bool isApproved,
        bool isProductionApproved,
        WindowsBuildIdentity? approvedIdentity,
        ExplorerInstanceIdentity? explorerIdentity,
        CaptureLeaseToken? captureToken)
    {
        IsApproved = isApproved;
        IsProductionApproved = isProductionApproved;
        ApprovedIdentity = approvedIdentity;
        ExplorerIdentity = explorerIdentity;
        CaptureToken = captureToken;
    }

    internal static TaskbarSignature Unknown { get; } = new(false, false, null, null, null);

    internal bool IsApproved { get; }

    internal bool IsProductionApproved { get; }

    internal WindowsBuildIdentity? ApprovedIdentity { get; }

    internal ExplorerInstanceIdentity? ExplorerIdentity { get; }

    internal CaptureLeaseToken? CaptureToken { get; }

    internal static TaskbarSignature Approve(
        WindowsBuildIdentity windowsIdentity,
        ObservedTaskbarSnapshot observation,
        TrustedStructureProof structureProof,
        ApprovedBaselineCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(windowsIdentity);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(structureProof);
        ArgumentNullException.ThrowIfNull(catalog);

        if (!windowsIdentity.IsComplete ||
            observation.DisplayProof is null ||
            observation.ExplorerIdentity != structureProof.ExplorerIdentity ||
            observation.ExplorerIdentity != observation.DisplayProof.ExplorerIdentity ||
            !ReferenceEquals(observation.CaptureToken, structureProof.CaptureToken) ||
            !ReferenceEquals(observation.CaptureToken, observation.DisplayProof.CaptureToken) ||
            !HostBindingsEqual(structureProof.HostBindings, observation.DisplayProof.HostBindings) ||
            !string.Equals(
                structureProof.XamlTypeParentSignature,
                RequiredXamlTypeParentSignature,
                StringComparison.Ordinal) ||
            !catalog.IsApproved(windowsIdentity, observation, structureProof))
        {
            return Unknown;
        }

        return new TaskbarSignature(
            true,
            catalog.HasProductionProvenance,
            windowsIdentity,
            observation.ExplorerIdentity,
            observation.CaptureToken);
    }

    private static bool HostBindingsEqual(
        IReadOnlyList<TaskbarHostBinding> first,
        IReadOnlyList<TaskbarHostBinding> second)
    {
        if (!HostBindingsAreValid(first) ||
            !HostBindingsAreValid(second) ||
            first.Count != second.Count)
        {
            return false;
        }

        return CanonicalHosts(first).SequenceEqual(CanonicalHosts(second));
    }

    private static bool HostBindingsAreValid(IReadOnlyList<TaskbarHostBinding> hosts) =>
        hosts.Count is > 0 and <= MaxTaskbarHostBindings &&
        hosts.Count(host => host.WindowClass == "Shell_TrayWnd") == 1 &&
        hosts.All(host =>
            host.WindowToken != 0 &&
            host.WindowClass is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") &&
        hosts.Select(host => host.WindowToken).Distinct().Count() == hosts.Count;

    private static IEnumerable<TaskbarHostBinding> CanonicalHosts(
        IReadOnlyList<TaskbarHostBinding> hosts) =>
        hosts.OrderBy(host => host.WindowToken)
            .ThenBy(host => host.WindowClass, StringComparer.Ordinal);
}

internal sealed record ExplorerInstanceIdentity(
    int ProcessId,
    long StartTimeUtcTicks,
    int SessionId,
    string UserSid,
    string CanonicalImagePath);

internal sealed class CaptureLeaseToken
{
}

internal sealed record TaskbarHostBinding(long WindowToken, string WindowClass);

internal sealed record TrustedFileSignature(string FileName, string? FileVersion, string Sha256);

internal sealed class TrustedDisplayProof
{
    internal TrustedDisplayProof(
        ExplorerInstanceIdentity explorerIdentity,
        CaptureLeaseToken captureToken,
        IReadOnlyList<TaskbarHostBinding> hostBindings)
    {
        ExplorerIdentity = explorerIdentity ?? throw new ArgumentNullException(nameof(explorerIdentity));
        CaptureToken = captureToken ?? throw new ArgumentNullException(nameof(captureToken));
        HostBindings = Copy(hostBindings);
    }

    internal ExplorerInstanceIdentity ExplorerIdentity { get; }

    internal CaptureLeaseToken CaptureToken { get; }

    internal IReadOnlyList<TaskbarHostBinding> HostBindings { get; }

    private static ReadOnlyCollection<TaskbarHostBinding> Copy(
        IReadOnlyList<TaskbarHostBinding> values) =>
        Array.AsReadOnly(values?.ToArray() ?? throw new ArgumentNullException(nameof(values)));
}

internal sealed class TrustedStructureProof
{
    internal TrustedStructureProof(
        ExplorerInstanceIdentity explorerIdentity,
        CaptureLeaseToken captureToken,
        IReadOnlyList<TaskbarHostBinding> hostBindings,
        string xamlTypeParentSignature)
    {
        ExplorerIdentity = explorerIdentity ?? throw new ArgumentNullException(nameof(explorerIdentity));
        CaptureToken = captureToken ?? throw new ArgumentNullException(nameof(captureToken));
        HostBindings = Array.AsReadOnly(
            hostBindings?.ToArray() ?? throw new ArgumentNullException(nameof(hostBindings)));
        XamlTypeParentSignature = xamlTypeParentSignature ??
            throw new ArgumentNullException(nameof(xamlTypeParentSignature));
    }

    internal ExplorerInstanceIdentity ExplorerIdentity { get; }

    internal CaptureLeaseToken CaptureToken { get; }

    internal IReadOnlyList<TaskbarHostBinding> HostBindings { get; }

    internal string XamlTypeParentSignature { get; }
}

internal sealed record ObservedTaskbarSnapshot
{
    private IReadOnlyList<TrustedFileSignature> modules = Array.Empty<TrustedFileSignature>();

    internal ObservedTaskbarSnapshot(
        ExplorerInstanceIdentity explorerIdentity,
        CaptureLeaseToken captureToken,
        TrustedFileSignature explorer,
        IReadOnlyList<TrustedFileSignature> modules,
        TrustedDisplayProof displayProof)
    {
        ExplorerIdentity = explorerIdentity ?? throw new ArgumentNullException(nameof(explorerIdentity));
        CaptureToken = captureToken ?? throw new ArgumentNullException(nameof(captureToken));
        Explorer = explorer ?? throw new ArgumentNullException(nameof(explorer));
        Modules = modules;
        DisplayProof = displayProof ?? throw new ArgumentNullException(nameof(displayProof));
    }

    internal ExplorerInstanceIdentity ExplorerIdentity { get; init; }

    internal CaptureLeaseToken CaptureToken { get; init; }

    internal TrustedFileSignature Explorer { get; init; }

    internal IReadOnlyList<TrustedFileSignature> Modules
    {
        get => modules;
        init => modules = Array.AsReadOnly(
            value?.ToArray() ?? throw new ArgumentNullException(nameof(value)));
    }

    internal TrustedDisplayProof DisplayProof { get; init; }
}

internal sealed class LiveGateReceipt
{
    // Task 3 intentionally has no product issuer. The Task 4/5 lifecycle must add one that
    // issues and consumes while its validated Explorer/process lease is still live.
    private readonly TaskbarSignature approvedSignature;
    private readonly ExplorerInstanceIdentity explorerIdentity;
    private readonly CaptureLeaseToken captureToken;
    private int consumed;

    private LiveGateReceipt(
        TaskbarSignature approvedSignature,
        ExplorerInstanceIdentity explorerIdentity,
        CaptureLeaseToken captureToken)
    {
        this.approvedSignature = approvedSignature ??
            throw new ArgumentNullException(nameof(approvedSignature));
        this.explorerIdentity = explorerIdentity ??
            throw new ArgumentNullException(nameof(explorerIdentity));
        this.captureToken = captureToken ??
            throw new ArgumentNullException(nameof(captureToken));
    }

    internal bool TryConsume(TaskbarSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (!ReferenceEquals(approvedSignature, signature) ||
            explorerIdentity != signature.ExplorerIdentity ||
            !ReferenceEquals(captureToken, signature.CaptureToken))
        {
            return false;
        }

        return Interlocked.CompareExchange(ref consumed, 1, 0) == 0;
    }
}

internal sealed record ApprovedCompatibilityBaseline
{
    internal ApprovedCompatibilityBaseline(
        WindowsBuildIdentity windowsIdentity,
        TrustedFileSignature explorer,
        IReadOnlyList<TrustedFileSignature> modules,
        string xamlTypeParentSignature)
    {
        WindowsIdentity = windowsIdentity;
        Explorer = explorer;
        Modules = Array.AsReadOnly(modules.ToArray());
        XamlTypeParentSignature = xamlTypeParentSignature;
    }

    internal WindowsBuildIdentity WindowsIdentity { get; }

    internal TrustedFileSignature Explorer { get; }

    internal IReadOnlyList<TrustedFileSignature> Modules { get; }

    internal string XamlTypeParentSignature { get; }
}

internal sealed class ApprovedBaselineCatalog
{
    private readonly IReadOnlyList<ApprovedCompatibilityBaseline> baselines;
    private readonly bool hasProductionProvenance;

    internal ApprovedBaselineCatalog(IReadOnlyList<ApprovedCompatibilityBaseline> baselines)
        : this(baselines, hasProductionProvenance: false)
    {
    }

    private ApprovedBaselineCatalog(
        IReadOnlyList<ApprovedCompatibilityBaseline> baselines,
        bool hasProductionProvenance)
    {
        this.baselines = Array.AsReadOnly(
            baselines?.ToArray() ?? throw new ArgumentNullException(nameof(baselines)));
        this.hasProductionProvenance = hasProductionProvenance;
    }

    internal static ApprovedBaselineCatalog Production { get; } =
        new([], hasProductionProvenance: true);

    internal bool HasProductionProvenance => hasProductionProvenance;

    internal bool IsApproved(
        WindowsBuildIdentity windowsIdentity,
        ObservedTaskbarSnapshot observation,
        TrustedStructureProof structureProof)
    {
        foreach (var baseline in baselines)
        {
            if (baseline.WindowsIdentity == windowsIdentity &&
                FileEquals(baseline.Explorer, observation.Explorer) &&
                ModuleSetsEqual(baseline.Modules, observation.Modules) &&
                string.Equals(
                    baseline.XamlTypeParentSignature,
                    structureProof.XamlTypeParentSignature,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ModuleSetsEqual(
        IReadOnlyList<TrustedFileSignature> approved,
        IReadOnlyList<TrustedFileSignature> observed)
    {
        if (approved.Count == 0 || approved.Count != observed.Count ||
            HasDuplicateNames(approved) || HasDuplicateNames(observed))
        {
            return false;
        }

        return CanonicalModules(approved).SequenceEqual(CanonicalModules(observed));
    }

    private static bool HasDuplicateNames(IReadOnlyList<TrustedFileSignature> modules) =>
        modules.Select(module => module.FileName)
            .Distinct(StringComparer.Ordinal)
            .Count() != modules.Count;

    private static IEnumerable<TrustedFileSignature> CanonicalModules(
        IReadOnlyList<TrustedFileSignature> modules) =>
        modules.OrderBy(module => module.FileName, StringComparer.Ordinal);

    private static bool FileEquals(TrustedFileSignature approved, TrustedFileSignature observed) =>
        string.Equals(approved.FileName, observed.FileName, StringComparison.Ordinal) &&
        string.Equals(approved.FileVersion, observed.FileVersion, StringComparison.Ordinal) &&
        string.Equals(approved.Sha256, observed.Sha256, StringComparison.Ordinal);
}
