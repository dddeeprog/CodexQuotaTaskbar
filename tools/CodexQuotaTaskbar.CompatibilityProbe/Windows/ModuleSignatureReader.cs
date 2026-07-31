using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using CodexQuotaTaskbar.Core.Compatibility;

namespace CodexQuotaTaskbar.CompatibilityProbe.Windows;

public sealed record ModuleFileEvidence(string FileName, string? FileVersion, string Sha256);

public readonly record struct MonitorDpi(int MonitorIndex, int DpiX, int DpiY);

public enum EvidenceCollectionStatus
{
    Complete,
    NoDiscoveryModules,
    ExplorerSelectionUnknown,
    ExplorerIdentityInvalid,
    ModuleInventoryInvalid,
    ExplorerFileSnapshotInvalid,
    ModuleFileSnapshotInvalid,
    DisplayTopologyChanged,
    ProcessChanged,
    ModuleInventoryChanged,
}

public enum EvidenceFailureReason
{
    None,
    BoundaryException,
    FileIdentityChanged,
    SourcePathMismatch,
    FileNameInvalid,
    FileVersionInvalid,
    HashInvalid,
    FileOpenFailed,
    FinalPathReadFailed,
    FileIdentityReadFailed,
    FileVersionReadFailed,
    FileHashReadFailed,
}

public sealed class TaskbarModuleEvidence
{
    private TaskbarModuleEvidence(
        ModuleFileEvidence? explorer,
        IReadOnlyList<ModuleFileEvidence> modules,
        bool moduleListKnown,
        IReadOnlyList<MonitorDpi> monitorDpiValues,
        bool displayEvidenceKnown,
        int? monitorCount,
        int? taskbarHostCount,
        EvidenceCollectionStatus collectionStatus,
        EvidenceFailureReason failureReason,
        ObservedTaskbarSnapshot? observation)
    {
        Explorer = explorer;
        Modules = modules;
        ModuleListKnown = moduleListKnown;
        MonitorDpiValues = monitorDpiValues;
        DisplayEvidenceKnown = displayEvidenceKnown;
        MonitorCount = monitorCount;
        TaskbarHostCount = taskbarHostCount;
        CollectionStatus = collectionStatus;
        FailureReason = failureReason;
        Observation = observation;
    }

    public ModuleFileEvidence? Explorer { get; }

    public IReadOnlyList<ModuleFileEvidence> Modules { get; }

    public bool ModuleListKnown { get; }

    public string? XamlTypeParentSignature => null;

    public IReadOnlyList<MonitorDpi> MonitorDpiValues { get; }

    public bool DisplayEvidenceKnown { get; }

    public int? MonitorCount { get; }

    public int? TaskbarHostCount { get; }

    public EvidenceCollectionStatus CollectionStatus { get; }

    public EvidenceFailureReason FailureReason { get; }

    internal ObservedTaskbarSnapshot? Observation { get; }

    internal TaskbarSignature Signature => TaskbarSignature.Unknown;

    internal static TaskbarModuleEvidence Unknown(
        EvidenceCollectionStatus status,
        EvidenceFailureReason reason = EvidenceFailureReason.None) =>
        new(
            null,
            Array.Empty<ModuleFileEvidence>(),
            moduleListKnown: false,
            Array.Empty<MonitorDpi>(),
            displayEvidenceKnown: false,
            monitorCount: null,
            taskbarHostCount: null,
            status,
            reason,
            observation: null);

    internal static TaskbarModuleEvidence Create(
        ModuleFileEvidence explorer,
        IReadOnlyList<ModuleFileEvidence> modules,
        bool moduleListKnown,
        IReadOnlyList<MonitorDpi> monitorDpiValues,
        int monitorCount,
        int taskbarHostCount,
        bool displayEvidenceKnown,
        EvidenceCollectionStatus collectionStatus,
        ObservedTaskbarSnapshot? observation) =>
        new(
            explorer,
            Copy(modules),
            moduleListKnown,
            displayEvidenceKnown ? Copy(monitorDpiValues) : Array.Empty<MonitorDpi>(),
            displayEvidenceKnown,
            displayEvidenceKnown ? monitorCount : null,
            displayEvidenceKnown ? taskbarHostCount : null,
            collectionStatus,
            EvidenceFailureReason.None,
            observation);

    internal TaskbarSignature Approve(
        WindowsBuildIdentity identity,
        TrustedStructureProof structureProof,
        ApprovedBaselineCatalog catalog) =>
        Observation is null
            ? TaskbarSignature.Unknown
            : TaskbarSignature.Approve(identity, Observation, structureProof, catalog);

    private static ReadOnlyCollection<T> Copy<T>(IReadOnlyList<T> values) =>
        Array.AsReadOnly(values.ToArray());
}

internal readonly record struct RawWindowBounds(int Left, int Top, int Right, int Bottom)
{
    internal bool IsValid => Right > Left && Bottom > Top;
}

internal sealed record RawTaskbarHost(
    long WindowToken,
    string WindowClass,
    int OwnerProcessId,
    long MonitorToken,
    RawWindowBounds Bounds = default);

internal sealed record RawDisplayMonitor(
    long MonitorToken,
    RawWindowBounds Bounds,
    bool IsPrimary);

internal sealed record RawExplorerProcess(
    int ProcessId,
    long StartTimeUtcTicks,
    int SessionId,
    string UserSid,
    Architecture Architecture,
    string ImagePath,
    bool HasExited);

internal readonly record struct StableFileIdentity(
    ulong VolumeSerialNumber,
    ulong FileId,
    long Length,
    long LastWriteTimeUtcTicks);

internal sealed record StableFileCapture(
    string SourcePath,
    string FileName,
    string? FileVersion,
    string Sha256,
    StableFileIdentity Before,
    StableFileIdentity VersionPathIdentity,
    StableFileIdentity After);

internal sealed class FileEvidenceBoundaryException : Exception
{
    internal FileEvidenceBoundaryException(
        EvidenceFailureReason failureReason,
        Exception? innerException = null)
        : base("A file-evidence boundary failed.", innerException)
    {
        if (failureReason is not (
                EvidenceFailureReason.FileOpenFailed or
                EvidenceFailureReason.FinalPathReadFailed or
                EvidenceFailureReason.FileIdentityReadFailed or
                EvidenceFailureReason.FileVersionReadFailed or
                EvidenceFailureReason.FileHashReadFailed))
        {
            throw new ArgumentOutOfRangeException(nameof(failureReason));
        }

        FailureReason = failureReason;
    }

    internal EvidenceFailureReason FailureReason { get; }
}

internal interface IExplorerProcessLease : IDisposable
{
    RawExplorerProcess Describe();

    IReadOnlyList<string> EnumerateModulePaths();
}

internal interface IStableFileCaptureLease : IDisposable
{
    StableFileCapture Complete();
}

internal interface IDpiAwarenessScope : IDisposable
{
    bool RestoreSucceeded { get; }
}

internal interface IModuleEvidencePlatform
{
    int CurrentSessionId { get; }

    string CurrentUserSid { get; }

    string ExpectedExplorerPath { get; }

    int GetShellWindowOwnerProcessId();

    IReadOnlyList<RawTaskbarHost> EnumerateTaskbarHosts();

    IReadOnlyList<RawDisplayMonitor> EnumerateDisplayMonitors();

    IExplorerProcessLease OpenExplorer(int processId);

    IStableFileCaptureLease OpenStableFile(string path);

    IDpiAwarenessScope EnterPerMonitorV2DpiAwareness();

    int GetDpiForWindow(long windowToken);
}

public sealed class ModuleSignatureReader
{
    private const int MaxTaskbarHosts = 16;
    private const int MaxDisplayMonitors = 32;
    private const int MaxDiscoveryModules = 256;
    private const int MaxFileNameLength = 128;
    private const int MaxVersionLength = 128;
    private static readonly string[] DiscoveryModuleFragments =
    [
        "taskbar",
        "xaml",
        "twinui",
        "windows.ui",
        "microsoft.ui",
    ];

    private readonly IModuleEvidencePlatform platform;

    public ModuleSignatureReader()
        : this(new NativeModuleEvidencePlatform())
    {
    }

    internal ModuleSignatureReader(IModuleEvidencePlatform platform)
    {
        this.platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    public TaskbarModuleEvidence Read()
    {
        var exceptionStatus = EvidenceCollectionStatus.ExplorerSelectionUnknown;
        try
        {
            var initialHosts = ReadValidatedHosts();
            if (initialHosts is null)
            {
                return TaskbarModuleEvidence.Unknown(EvidenceCollectionStatus.ExplorerSelectionUnknown);
            }

            var ownerProcessId = initialHosts[0].OwnerProcessId;
            if (platform.GetShellWindowOwnerProcessId() != ownerProcessId)
            {
                return TaskbarModuleEvidence.Unknown(EvidenceCollectionStatus.ExplorerSelectionUnknown);
            }

            exceptionStatus = EvidenceCollectionStatus.ExplorerIdentityInvalid;
            using var explorer = platform.OpenExplorer(ownerProcessId);
            var initialProcess = explorer.Describe();
            if (!IsExpectedExplorer(initialProcess, ownerProcessId))
            {
                return TaskbarModuleEvidence.Unknown(EvidenceCollectionStatus.ExplorerIdentityInvalid);
            }

            exceptionStatus = EvidenceCollectionStatus.ModuleInventoryInvalid;
            var initialModulePaths = ReadDiscoveryModulePaths(explorer);
            if (initialModulePaths is null)
            {
                return TaskbarModuleEvidence.Unknown(EvidenceCollectionStatus.ModuleInventoryInvalid);
            }

            using var stableFiles = new StableFileLeaseSet();
            exceptionStatus = EvidenceCollectionStatus.ExplorerFileSnapshotInvalid;
            var explorerFile = stableFiles.Add(platform.OpenStableFile(initialProcess.ImagePath));

            exceptionStatus = EvidenceCollectionStatus.ModuleFileSnapshotInvalid;
            var moduleFiles = new List<(string Path, IStableFileCaptureLease Lease)>(
                initialModulePaths.Count);
            foreach (var path in initialModulePaths)
            {
                moduleFiles.Add((path, stableFiles.Add(platform.OpenStableFile(path))));
            }

            var captureToken = new CaptureLeaseToken();
            var explorerIdentity = new ExplorerInstanceIdentity(
                initialProcess.ProcessId,
                initialProcess.StartTimeUtcTicks,
                initialProcess.SessionId,
                initialProcess.UserSid,
                CanonicalPath(initialProcess.ImagePath));
            exceptionStatus = EvidenceCollectionStatus.DisplayTopologyChanged;
            var initialDisplay = ReadDisplaySnapshot(ownerProcessId, explorer, initialProcess);
            if (initialDisplay is not null &&
                !HostTopologiesEqual(initialHosts, initialDisplay.Hosts))
            {
                initialDisplay = null;
            }

            exceptionStatus = EvidenceCollectionStatus.ExplorerSelectionUnknown;
            if (platform.GetShellWindowOwnerProcessId() != ownerProcessId)
            {
                return TaskbarModuleEvidence.Unknown(EvidenceCollectionStatus.ExplorerSelectionUnknown);
            }

            exceptionStatus = EvidenceCollectionStatus.ProcessChanged;
            var finalProcess = explorer.Describe();
            if (finalProcess != initialProcess)
            {
                return TaskbarModuleEvidence.Unknown(EvidenceCollectionStatus.ProcessChanged);
            }

            exceptionStatus = EvidenceCollectionStatus.ModuleInventoryChanged;
            var finalModulePaths = ReadDiscoveryModulePaths(explorer);
            if (finalModulePaths is null ||
                !PathSetsEqual(initialModulePaths, finalModulePaths))
            {
                return TaskbarModuleEvidence.Unknown(EvidenceCollectionStatus.ModuleInventoryChanged);
            }

            exceptionStatus = EvidenceCollectionStatus.ExplorerFileSnapshotInvalid;
            var explorerCaptureResult = ValidateCapture(
                explorerFile.Complete(),
                initialProcess.ImagePath,
                expectedFileName: "explorer.exe");
            if (explorerCaptureResult.Evidence is null)
            {
                return TaskbarModuleEvidence.Unknown(
                    EvidenceCollectionStatus.ExplorerFileSnapshotInvalid,
                    explorerCaptureResult.FailureReason);
            }

            var explorerEvidence = explorerCaptureResult.Evidence;
            exceptionStatus = EvidenceCollectionStatus.ModuleFileSnapshotInvalid;
            var modules = new List<ModuleFileEvidence>(moduleFiles.Count);
            foreach (var moduleFile in moduleFiles)
            {
                var moduleCaptureResult = ValidateCapture(
                    moduleFile.Lease.Complete(),
                    moduleFile.Path,
                    expectedFileName: null);
                if (moduleCaptureResult.Evidence is null)
                {
                    return TaskbarModuleEvidence.Unknown(
                        EvidenceCollectionStatus.ModuleFileSnapshotInvalid,
                        moduleCaptureResult.FailureReason);
                }

                modules.Add(moduleCaptureResult.Evidence);
            }

            exceptionStatus = EvidenceCollectionStatus.ExplorerSelectionUnknown;
            if (platform.GetShellWindowOwnerProcessId() != ownerProcessId)
            {
                return TaskbarModuleEvidence.Unknown(EvidenceCollectionStatus.ExplorerSelectionUnknown);
            }

            exceptionStatus = EvidenceCollectionStatus.ProcessChanged;
            if (explorer.Describe() != initialProcess)
            {
                return TaskbarModuleEvidence.Unknown(EvidenceCollectionStatus.ProcessChanged);
            }

            exceptionStatus = EvidenceCollectionStatus.ModuleInventoryChanged;
            finalModulePaths = ReadDiscoveryModulePaths(explorer);
            if (finalModulePaths is null ||
                !PathSetsEqual(initialModulePaths, finalModulePaths))
            {
                return TaskbarModuleEvidence.Unknown(EvidenceCollectionStatus.ModuleInventoryChanged);
            }

            exceptionStatus = EvidenceCollectionStatus.DisplayTopologyChanged;
            var finalDisplay = ReadDisplaySnapshot(ownerProcessId, explorer, initialProcess);
            var display = initialDisplay is not null &&
                finalDisplay is not null &&
                DisplaySnapshotsEqual(initialDisplay, finalDisplay)
                    ? finalDisplay
                    : null;

            modules.Sort((left, right) => StringComparer.Ordinal.Compare(left.FileName, right.FileName));
            ObservedTaskbarSnapshot? observation = null;
            if (display is not null && initialModulePaths.Count > 0)
            {
                var bindings = display.Hosts
                    .Select(host => new TaskbarHostBinding(host.WindowToken, host.WindowClass))
                    .ToArray();
                observation = new ObservedTaskbarSnapshot(
                    explorerIdentity,
                    captureToken,
                    ToTrusted(explorerEvidence),
                    modules.Select(ToTrusted).ToArray(),
                    new TrustedDisplayProof(explorerIdentity, captureToken, bindings));
            }

            var monitorIndexes = display?.Monitors
                .Select((monitor, index) => (monitor.MonitorToken, index))
                .ToDictionary(item => item.MonitorToken, item => item.index);
            var dpiValues = display?.DpiBindings
                .Select(binding => new MonitorDpi(
                    monitorIndexes![binding.MonitorToken],
                    binding.Dpi,
                    binding.Dpi))
                .ToArray() ?? Array.Empty<MonitorDpi>();

            return TaskbarModuleEvidence.Create(
                explorerEvidence,
                modules,
                initialModulePaths.Count > 0,
                dpiValues,
                display?.Monitors.Count ?? 0,
                display?.Hosts.Count ?? 0,
                display is not null,
                display is null
                    ? EvidenceCollectionStatus.DisplayTopologyChanged
                    : initialModulePaths.Count == 0
                        ? EvidenceCollectionStatus.NoDiscoveryModules
                        : EvidenceCollectionStatus.Complete,
                observation);
        }
        catch (FileEvidenceBoundaryException exception)
        {
            return TaskbarModuleEvidence.Unknown(exceptionStatus, exception.FailureReason);
        }
        catch (Exception)
        {
            return TaskbarModuleEvidence.Unknown(
                exceptionStatus,
                EvidenceFailureReason.BoundaryException);
        }
    }

    private IReadOnlyList<RawTaskbarHost>? ReadValidatedHosts()
    {
        var hosts = platform.EnumerateTaskbarHosts()
            .Where(host => host.WindowClass is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            .OrderBy(host => host.WindowToken)
            .ToArray();
        if (hosts.Length == 0 ||
            hosts.Length > MaxTaskbarHosts ||
            hosts.Count(host => host.WindowClass == "Shell_TrayWnd") != 1 ||
            hosts.Select(host => host.OwnerProcessId).Distinct().Count() != 1 ||
            hosts.Any(host =>
                host.WindowToken == 0 ||
                host.MonitorToken == 0 ||
                !host.Bounds.IsValid) ||
            hosts.Select(host => host.WindowToken).Distinct().Count() != hosts.Length ||
            hosts.Select(host => host.MonitorToken).Distinct().Count() != hosts.Length)
        {
            return null;
        }

        return hosts;
    }

    private IReadOnlyList<RawDisplayMonitor>? ReadValidatedMonitors()
    {
        var monitors = platform.EnumerateDisplayMonitors()
            .OrderBy(monitor => monitor.MonitorToken)
            .ToArray();
        if (monitors.Length == 0 ||
            monitors.Length > MaxDisplayMonitors ||
            monitors.Count(monitor => monitor.IsPrimary) != 1 ||
            monitors.Any(monitor => monitor.MonitorToken == 0 || !monitor.Bounds.IsValid) ||
            monitors.Select(monitor => monitor.MonitorToken).Distinct().Count() != monitors.Length)
        {
            return null;
        }

        return monitors;
    }

    private bool IsExpectedExplorer(RawExplorerProcess process, int expectedProcessId)
    {
        if (process.HasExited ||
            process.ProcessId != expectedProcessId ||
            process.StartTimeUtcTicks <= 0 ||
            process.SessionId != platform.CurrentSessionId ||
            process.Architecture != Architecture.X64 ||
            !string.Equals(process.UserSid, platform.CurrentUserSid, StringComparison.Ordinal) ||
            !string.Equals(
                CanonicalPath(process.ImagePath),
                CanonicalPath(platform.ExpectedExplorerPath),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(
            Path.GetFileName(process.ImagePath),
            "explorer.exe",
            StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyList<string>? ReadDiscoveryModulePaths(IExplorerProcessLease explorer)
    {
        var paths = explorer.EnumerateModulePaths()
            .Where(IsDiscoveryModule)
            .Select(CanonicalPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return paths.Length <= MaxDiscoveryModules ? paths : null;
    }

    private DynamicDisplaySnapshot? ReadDisplaySnapshot(
        int expectedOwnerProcessId,
        IExplorerProcessLease explorer,
        RawExplorerProcess expectedExplorer)
    {
        var initialHosts = ReadValidatedHosts();
        var initialMonitors = ReadValidatedMonitors();
        if (initialHosts is null ||
            initialMonitors is null ||
            initialHosts.Any(host => host.OwnerProcessId != expectedOwnerProcessId) ||
            initialHosts.Any(host =>
                !initialMonitors.Any(monitor => monitor.MonitorToken == host.MonitorToken)))
        {
            return null;
        }

        var dpiBindings = new List<TaskbarDpiBinding>(initialHosts.Count);
        IDpiAwarenessScope? scope = null;
        try
        {
            scope = platform.EnterPerMonitorV2DpiAwareness();
            foreach (var host in initialHosts)
            {
                var dpi = platform.GetDpiForWindow(host.WindowToken);
                if (dpi is <= 0 or > 960)
                {
                    return null;
                }

                dpiBindings.Add(new TaskbarDpiBinding(
                    host.WindowToken,
                    host.MonitorToken,
                    dpi));
            }
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            scope?.Dispose();
        }

        if (scope is null || !scope.RestoreSucceeded)
        {
            return null;
        }

        var finalHosts = ReadValidatedHosts();
        var finalMonitors = ReadValidatedMonitors();
        if (finalHosts is null ||
            finalMonitors is null ||
            !HostTopologiesEqual(initialHosts, finalHosts) ||
            !MonitorTopologiesEqual(initialMonitors, finalMonitors) ||
            platform.GetShellWindowOwnerProcessId() != expectedOwnerProcessId ||
            explorer.Describe() != expectedExplorer)
        {
            return null;
        }

        return new DynamicDisplaySnapshot(
            expectedExplorer,
            initialHosts,
            initialMonitors,
            dpiBindings.ToArray());
    }

    private static FileCaptureValidationResult ValidateCapture(
        StableFileCapture capture,
        string expectedPath,
        string? expectedFileName)
    {
        if (capture.Before != capture.VersionPathIdentity ||
            capture.Before != capture.After ||
            capture.Before.Length < 0 ||
            capture.Before.LastWriteTimeUtcTicks <= 0)
        {
            return FileCaptureValidationResult.Failed(EvidenceFailureReason.FileIdentityChanged);
        }

        if (!string.Equals(
                CanonicalPath(capture.SourcePath),
                CanonicalPath(expectedPath),
                StringComparison.OrdinalIgnoreCase))
        {
            return FileCaptureValidationResult.Failed(EvidenceFailureReason.SourcePathMismatch);
        }

        if (!IsSafeFileName(capture.FileName) ||
            !string.Equals(
                capture.FileName,
                Path.GetFileName(expectedPath),
                StringComparison.OrdinalIgnoreCase) ||
            (expectedFileName is not null &&
                !string.Equals(capture.FileName, expectedFileName, StringComparison.OrdinalIgnoreCase)))
        {
            return FileCaptureValidationResult.Failed(EvidenceFailureReason.FileNameInvalid);
        }

        if (capture.FileVersion is not null && !IsSafeVersion(capture.FileVersion))
        {
            return FileCaptureValidationResult.Failed(EvidenceFailureReason.FileVersionInvalid);
        }

        if (!IsNormalizedSha256(capture.Sha256))
        {
            return FileCaptureValidationResult.Failed(EvidenceFailureReason.HashInvalid);
        }

        return FileCaptureValidationResult.Succeeded(
            new ModuleFileEvidence(capture.FileName, capture.FileVersion, capture.Sha256));
    }

    private static bool IsSafeFileName(string value) =>
        value.Length is > 0 and <= MaxFileNameLength &&
        string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) &&
        value.All(character =>
            character is >= '0' and <= '9' or
                >= 'A' and <= 'Z' or
                >= 'a' and <= 'z' or
                '.' or ' ' or '(' or ')' or '-' or '_');

    private static bool IsSafeVersion(string value) =>
        value.Length is > 0 and <= MaxVersionLength &&
        value.Any(character =>
            character is >= '0' and <= '9' or
                >= 'A' and <= 'Z' or
                >= 'a' and <= 'z') &&
        value.All(character =>
            character is >= '0' and <= '9' or
                >= 'A' and <= 'Z' or
                >= 'a' and <= 'z' or
                '.' or ' ' or '(' or ')' or '-' or '_');

    private static bool IsNormalizedSha256(string value) =>
        value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsDiscoveryModule(string path)
    {
        var fileName = Path.GetFileName(path);
        return DiscoveryModuleFragments.Any(
            fragment => fileName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HostTopologiesEqual(
        IReadOnlyList<RawTaskbarHost> first,
        IReadOnlyList<RawTaskbarHost> second) =>
        first.SequenceEqual(second);

    private static bool MonitorTopologiesEqual(
        IReadOnlyList<RawDisplayMonitor> first,
        IReadOnlyList<RawDisplayMonitor> second) =>
        first.SequenceEqual(second);

    private static bool DisplaySnapshotsEqual(
        DynamicDisplaySnapshot first,
        DynamicDisplaySnapshot second) =>
        first.Explorer == second.Explorer &&
        HostTopologiesEqual(first.Hosts, second.Hosts) &&
        MonitorTopologiesEqual(first.Monitors, second.Monitors) &&
        first.DpiBindings.SequenceEqual(second.DpiBindings);

    private static bool PathSetsEqual(
        IReadOnlyList<string> first,
        IReadOnlyList<string> second) =>
        first.Count == second.Count && first.SequenceEqual(second, StringComparer.OrdinalIgnoreCase);

    private static string CanonicalPath(string path) => Path.GetFullPath(path);

    private static TrustedFileSignature ToTrusted(ModuleFileEvidence evidence) =>
        new(evidence.FileName, evidence.FileVersion, evidence.Sha256);

    private readonly record struct FileCaptureValidationResult(
        ModuleFileEvidence? Evidence,
        EvidenceFailureReason FailureReason)
    {
        internal static FileCaptureValidationResult Succeeded(ModuleFileEvidence evidence) =>
            new(evidence, EvidenceFailureReason.None);

        internal static FileCaptureValidationResult Failed(EvidenceFailureReason failureReason) =>
            new(null, failureReason);
    }

    private readonly record struct TaskbarDpiBinding(
        long WindowToken,
        long MonitorToken,
        int Dpi);

    private sealed record DynamicDisplaySnapshot(
        RawExplorerProcess Explorer,
        IReadOnlyList<RawTaskbarHost> Hosts,
        IReadOnlyList<RawDisplayMonitor> Monitors,
        IReadOnlyList<TaskbarDpiBinding> DpiBindings);

    private sealed class StableFileLeaseSet : IDisposable
    {
        private readonly List<IStableFileCaptureLease> leases = [];

        internal IStableFileCaptureLease Add(IStableFileCaptureLease lease)
        {
            ArgumentNullException.ThrowIfNull(lease);
            leases.Add(lease);
            return lease;
        }

        public void Dispose()
        {
            Exception? firstFailure = null;
            for (var index = leases.Count - 1; index >= 0; index--)
            {
                try
                {
                    leases[index].Dispose();
                }
                catch (Exception exception) when (firstFailure is null)
                {
                    firstFailure = exception;
                }
                catch (Exception)
                {
                    // Continue closing the remaining held file handles.
                }
            }

            if (firstFailure is not null)
            {
                throw firstFailure;
            }
        }
    }
}
