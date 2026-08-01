using System.Text;
using CodexQuotaTaskbar.BridgeControl.Safety;

namespace CodexQuotaTaskbar.BridgeControl.Injection;

internal enum InjectorStage
{
    JournalGate,
    Artifact,
    OpenTarget,
    ValidateTarget,
    LoadLibrary,
    EnumerateBridge,
    StartProbe,
    StartReleased,
    Ready,
    EmergencyDetach,
    Detach,
    JournalCommit,
}

internal enum InjectorSafetyDisposition
{
    RejectedNoMutation,
    FailedClean,
    Ready,
    UnsafeRetained,
    CleanButJournalBlocked,
}

internal enum InjectorFailureCode
{
    PreviousActivationUnclean,
    ActivationSessionBusy,
    JournalUnavailable,
    ArtifactMaterializationFailed,
    BridgeContractInvalid,
    OpenProcessFailed,
    TargetIdentityMismatch,
    ModuleEnumerationFailed,
    ExistingBridgeMapped,
    PermitAlreadyConsumed,
    OperationCancelled,
    SystemExportResolutionFailed,
    SystemModuleMismatch,
    RemoteAllocationFailed,
    RemoteWriteFailed,
    RemoteThreadCreateFailed,
    LoadLibraryTimeout,
    LoadLibraryWaitFailed,
    BridgeModuleMissing,
    BridgeModuleAmbiguous,
    BridgeModuleImageMismatch,
    StartProbeTimeout,
    StartProbeWaitFailed,
    StartProbeRejected,
    StartReleasedOpenFailed,
    StartReleasedSignalFailed,
    ControlEventOpenFailed,
    ReadyTimeout,
    ReadyWaitFailed,
    FinalValidationFailed,
    JournalWriteFailed,
    ShutdownSignalFailed,
    QuiescedTimeout,
    QuiescedWaitFailed,
    ModuleUnloadTimeout,
    UnexpectedBoundaryFailure,
    SafetyBreakerLatched,
}

internal sealed record InjectorFailure(
    InjectorStage Stage,
    InjectorFailureCode Code,
    InjectorSafetyDisposition Disposition,
    int? NativeError = null,
    int? RemoteHResult = null);

internal sealed record ExplorerInjectionResult
{
    private ExplorerInjectionResult(
        ExplorerInjector.ExplorerProbeSession? session,
        InjectorFailure? failure)
    {
        Session = session;
        Failure = failure;
    }

    internal ExplorerInjector.ExplorerProbeSession? Session { get; }

    internal InjectorFailure? Failure { get; }

    internal bool Succeeded => Session is not null && Failure is null;

    internal static ExplorerInjectionResult Ready(
        ExplorerInjector.ExplorerProbeSession session) =>
        new(session, null);

    internal static ExplorerInjectionResult Failed(InjectorFailure failure) =>
        new(null, failure);
}

internal enum ExplorerDetachOutcome
{
    Clean,
    AlreadyClean,
    Unsafe,
    CleanButJournalBlocked,
}

internal sealed record ExplorerDetachResult(
    ExplorerDetachOutcome Outcome,
    InjectorFailure? Failure = null);

internal sealed class LiveActivationPermit
{
    private int consumed;

    private LiveActivationPermit(
        string appVersion,
        int windowsBuild,
        string explorerSignatureSha256,
        string taskbarSignatureSha256,
        int explorerProcessId,
        ulong explorerCreationTimeFileTime100Nanoseconds,
        string expectedExplorerPath,
        bool explicitRetry)
    {
        AppVersion = appVersion;
        WindowsBuild = windowsBuild;
        ExplorerSignatureSha256 = explorerSignatureSha256;
        TaskbarSignatureSha256 = taskbarSignatureSha256;
        ExplorerProcessId = explorerProcessId;
        ExplorerCreationTimeFileTime100Nanoseconds =
            explorerCreationTimeFileTime100Nanoseconds;
        ExpectedExplorerPath = expectedExplorerPath;
        ExplicitRetry = explicitRetry;
    }

    internal string AppVersion { get; }

    internal int WindowsBuild { get; }

    internal string ExplorerSignatureSha256 { get; }

    internal string TaskbarSignatureSha256 { get; }

    internal int ExplorerProcessId { get; }

    internal ulong ExplorerCreationTimeFileTime100Nanoseconds { get; }

    internal string ExpectedExplorerPath { get; }

    internal bool ExplicitRetry { get; }

    internal static LiveActivationPermit Issue(
        string appVersion,
        int windowsBuild,
        string explorerSignatureSha256,
        string taskbarSignatureSha256,
        int explorerProcessId,
        ulong explorerCreationTimeFileTime100Nanoseconds,
        string expectedExplorerPath,
        bool explicitRetry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(explorerSignatureSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskbarSignatureSha256);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowsBuild);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(explorerProcessId);
        if (explorerCreationTimeFileTime100Nanoseconds == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(explorerCreationTimeFileTime100Nanoseconds));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(expectedExplorerPath);
        return new LiveActivationPermit(
            appVersion,
            windowsBuild,
            explorerSignatureSha256,
            taskbarSignatureSha256,
            explorerProcessId,
            explorerCreationTimeFileTime100Nanoseconds,
            Path.GetFullPath(expectedExplorerPath),
            explicitRetry);
    }

    internal bool TryConsume() => Interlocked.Exchange(ref consumed, 1) == 0;
}

internal interface IActivationJournalStore
{
    ActivationSessionLeaseResult TryAcquireSessionLease();

    ActivationJournalResult ReadStatus();

    ActivationJournalResult TryBegin(
        ActivationStartRequest request,
        bool explicitRetry);

    ActivationJournalResult TryTransition(Guid activationId, ActivationState state);
}

internal sealed class ActivationJournalStore : IActivationJournalStore
{
    private readonly ActivationJournal journal;

    internal ActivationJournalStore(ActivationJournal journal) =>
        this.journal = journal ?? throw new ArgumentNullException(nameof(journal));

    public ActivationJournalResult ReadStatus() => journal.ReadStatus();

    public ActivationSessionLeaseResult TryAcquireSessionLease() =>
        journal.TryAcquireSessionLease();

    public ActivationJournalResult TryBegin(
        ActivationStartRequest request,
        bool explicitRetry) =>
        journal.TryBegin(request, explicitRetry);

    public ActivationJournalResult TryTransition(Guid activationId, ActivationState state) =>
        journal.TryTransition(activationId, state);
}

internal sealed class ExplorerInjector
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan EventRetryDelay = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan ModulePollDelay = TimeSpan.FromMilliseconds(50);
    private const int MaximumEventOpenAttempts = 400;
    private const int MaximumModulePollAttempts = 200;
    private readonly IExplorerProcessApi processApi;
    private readonly IBridgeArtifactStore artifactStore;
    private readonly IBridgeImageInspector imageInspector;
    private readonly IActivationJournalStore journal;
    private readonly TimeProvider timeProvider;

    internal ExplorerInjector(
        IExplorerProcessApi processApi,
        IBridgeArtifactStore artifactStore,
        IBridgeImageInspector imageInspector,
        IActivationJournalStore journal,
        TimeProvider? timeProvider = null)
    {
        this.processApi = processApi ?? throw new ArgumentNullException(nameof(processApi));
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        this.imageInspector = imageInspector ?? throw new ArgumentNullException(nameof(imageInspector));
        this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal async Task<ExplorerInjectionResult> InjectAsync(
        LiveActivationPermit permit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permit);
        ActivationSessionLeaseResult acquiredSessionLease;
        try
        {
            acquiredSessionLease = journal.TryAcquireSessionLease();
        }
        catch (Exception)
        {
            return Fail(
                InjectorStage.JournalGate,
                InjectorFailureCode.JournalUnavailable,
                InjectorSafetyDisposition.RejectedNoMutation);
        }

        if (!acquiredSessionLease.Succeeded || acquiredSessionLease.Lease is null)
        {
            return Fail(
                InjectorStage.JournalGate,
                acquiredSessionLease.Outcome == ActivationSessionLeaseOutcome.Busy
                    ? InjectorFailureCode.ActivationSessionBusy
                    : InjectorFailureCode.JournalUnavailable,
                InjectorSafetyDisposition.RejectedNoMutation);
        }

        IActivationSessionLease? sessionLease = acquiredSessionLease.Lease;
        ActivationJournalResult gate;
        try
        {
            gate = journal.ReadStatus();
        }
        catch (Exception)
        {
            DisposeBestEffort(sessionLease);
            return Fail(
                InjectorStage.JournalGate,
                InjectorFailureCode.JournalUnavailable,
                InjectorSafetyDisposition.RejectedNoMutation);
        }

        if (gate.Outcome != ActivationJournalOutcome.Allowed &&
            gate.Outcome != ActivationJournalOutcome.Blocked)
        {
            DisposeBestEffort(sessionLease);
            return Fail(
                InjectorStage.JournalGate,
                InjectorFailureCode.JournalUnavailable,
                InjectorSafetyDisposition.RejectedNoMutation);
        }

        if (gate.Outcome == ActivationJournalOutcome.Blocked && !permit.ExplicitRetry)
        {
            DisposeBestEffort(sessionLease);
            return Fail(
                InjectorStage.JournalGate,
                InjectorFailureCode.PreviousActivationUnclean,
                InjectorSafetyDisposition.RejectedNoMutation);
        }

        IBridgeArtifactLease? artifact = null;
        IExplorerMutationLease? process = null;
        INamedEvent? shutdown = null;
        INamedEvent? quiesced = null;
        var transferred = false;
        var mappingMayExist = false;
        ActivationJournalRecord? activation = null;
        BridgeImageContract? activeContract = null;
        try
        {
            BridgeImageContract contract;
            try
            {
                artifact = artifactStore.Materialize();
                contract = imageInspector.InspectAndValidate(artifact);
                activeContract = contract;
            }
            catch (BridgeArtifactException exception)
            {
                return Fail(
                    InjectorStage.Artifact,
                    InjectorFailureCode.ArtifactMaterializationFailed,
                    InjectorSafetyDisposition.RejectedNoMutation,
                    nativeError: (int)exception.Code);
            }
            catch (BridgeImageException exception)
            {
                return Fail(
                    InjectorStage.Artifact,
                    InjectorFailureCode.BridgeContractInvalid,
                    InjectorSafetyDisposition.RejectedNoMutation,
                    nativeError: (int)exception.Code);
            }

            if (contract.AbiVersion != BridgeStartRequest.AbiVersion)
            {
                return Fail(
                    InjectorStage.Artifact,
                    InjectorFailureCode.BridgeContractInvalid,
                    InjectorSafetyDisposition.RejectedNoMutation);
            }

            NativeResult<IExplorerMutationLease> opened;
            try
            {
                opened = processApi.OpenForInjection(permit.ExplorerProcessId);
            }
            catch (Exception)
            {
                return Fail(
                    InjectorStage.OpenTarget,
                    InjectorFailureCode.OpenProcessFailed,
                    InjectorSafetyDisposition.RejectedNoMutation);
            }

            if (!opened.Succeeded)
            {
                return Fail(
                    InjectorStage.OpenTarget,
                    InjectorFailureCode.OpenProcessFailed,
                    InjectorSafetyDisposition.RejectedNoMutation,
                    opened.ErrorCode);
            }

            process = opened.Value!;
            var initialIdentity = ValidateIdentity(process, permit);
            if (!initialIdentity.Succeeded)
            {
                return Fail(
                    InjectorStage.ValidateTarget,
                    InjectorFailureCode.TargetIdentityMismatch,
                    InjectorSafetyDisposition.RejectedNoMutation,
                    initialIdentity.ErrorCode);
            }

            var initialModules = process.EnumerateModules();
            if (!initialModules.Succeeded)
            {
                return Fail(
                    InjectorStage.ValidateTarget,
                    InjectorFailureCode.ModuleEnumerationFailed,
                    InjectorSafetyDisposition.RejectedNoMutation,
                    initialModules.ErrorCode);
            }

            if (gate.Outcome == ActivationJournalOutcome.Blocked &&
                (gate.Record is null ||
                    !CanRecoverPreviousActivation(
                        gate.Record,
                        process,
                        permit,
                        contract,
                        initialModules.Value!)))
            {
                return Fail(
                    InjectorStage.JournalGate,
                    InjectorFailureCode.PreviousActivationUnclean,
                    InjectorSafetyDisposition.RejectedNoMutation);
            }

            var existingBridgeModules = FindModulesByPath(
                initialModules.Value!,
                contract.CanonicalPath);
            if (existingBridgeModules.Count != 0)
            {
                return Fail(
                    InjectorStage.ValidateTarget,
                    HasUnexpectedImageSize(existingBridgeModules, contract.SizeOfImage)
                        ? InjectorFailureCode.BridgeModuleImageMismatch
                        : InjectorFailureCode.ExistingBridgeMapped,
                    InjectorSafetyDisposition.RejectedNoMutation);
            }

            var begin = journal.TryBegin(
                new ActivationStartRequest(
                    permit.AppVersion,
                    permit.WindowsBuild,
                    permit.ExplorerSignatureSha256,
                    permit.TaskbarSignatureSha256,
                    permit.ExplorerProcessId,
                    permit.ExplorerCreationTimeFileTime100Nanoseconds),
                permit.ExplicitRetry);
            if (begin.Outcome != ActivationJournalOutcome.PendingPersisted ||
                begin.Record is null)
            {
                return Fail(
                    InjectorStage.JournalCommit,
                    InjectorFailureCode.JournalWriteFailed,
                    InjectorSafetyDisposition.RejectedNoMutation);
            }

            activation = begin.Record;
            var finalPreMutationIdentity = ValidateIdentity(process, permit);
            if (!finalPreMutationIdentity.Succeeded)
            {
                return CommitCleanFailure(
                    activation,
                    InjectorStage.ValidateTarget,
                    InjectorFailureCode.TargetIdentityMismatch,
                    finalPreMutationIdentity.ErrorCode);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return CommitCleanFailure(
                    activation,
                    InjectorStage.ValidateTarget,
                    InjectorFailureCode.OperationCancelled);
            }

            if (!permit.TryConsume())
            {
                return CommitCleanFailure(
                    activation,
                    InjectorStage.ValidateTarget,
                    InjectorFailureCode.PermitAlreadyConsumed);
            }

            var preMutationModules = process.EnumerateModules();
            var preMutationBridgeModules = preMutationModules.Succeeded
                ? FindModulesByPath(preMutationModules.Value!, contract.CanonicalPath)
                : Array.Empty<RemoteModule>();
            if (!preMutationModules.Succeeded || preMutationBridgeModules.Count != 0)
            {
                return CommitCleanFailure(
                    activation,
                    InjectorStage.ValidateTarget,
                    preMutationModules.Succeeded
                        ? HasUnexpectedImageSize(
                            preMutationBridgeModules,
                            contract.SizeOfImage)
                            ? InjectorFailureCode.BridgeModuleImageMismatch
                            : InjectorFailureCode.ExistingBridgeMapped
                        : InjectorFailureCode.ModuleEnumerationFailed,
                    preMutationModules.ErrorCode);
            }

            var localLoadLibrary = processApi.ResolveLocalSystemExport(
                "kernel32.dll",
                "LoadLibraryW");
            if (!localLoadLibrary.Succeeded)
            {
                return CommitCleanFailure(
                    activation,
                    InjectorStage.LoadLibrary,
                    InjectorFailureCode.SystemExportResolutionFailed,
                    localLoadLibrary.ErrorCode);
            }

            var systemModule = FindSystemModule(
                preMutationModules.Value!,
                localLoadLibrary.Value!);
            if (systemModule is null ||
                !TryAddRva(
                    systemModule.BaseAddress,
                    systemModule.Size,
                    localLoadLibrary.Value!.RelativeVirtualAddress,
                    out var remoteLoadLibrary))
            {
                return CommitCleanFailure(
                    activation,
                    InjectorStage.LoadLibrary,
                    InjectorFailureCode.SystemModuleMismatch);
            }

            var pathBytes = Encoding.Unicode.GetBytes(contract.CanonicalPath + '\0');
            mappingMayExist = true;
            var loadResult = StartAndWait(
                process,
                remoteLoadLibrary,
                pathBytes,
                InjectorStage.LoadLibrary,
                mappingMayExist: false);
            if (loadResult.Failure is not null)
            {
                return loadResult.UnknownRemoteState
                    ? CommitUnsafeFailure(activation, loadResult.Failure)
                    : CommitCleanFailure(
                        activation,
                        loadResult.Failure.Stage,
                        loadResult.Failure.Code,
                        loadResult.Failure.NativeError);
            }

            if (!ValidateIdentity(process, permit).Succeeded)
            {
                return CommitUnsafeFailure(
                    activation,
                    Failure(
                        InjectorStage.ValidateTarget,
                        InjectorFailureCode.TargetIdentityMismatch,
                        InjectorSafetyDisposition.UnsafeRetained));
            }

            var loadedModules = process.EnumerateModules();
            if (!loadedModules.Succeeded)
            {
                return CommitUnsafeFailure(
                    activation,
                    Failure(
                        InjectorStage.EnumerateBridge,
                        InjectorFailureCode.ModuleEnumerationFailed,
                        InjectorSafetyDisposition.UnsafeRetained,
                        loadedModules.ErrorCode));
            }

            var bridgeMatches = FindModulesByPath(
                loadedModules.Value!,
                contract.CanonicalPath);
            if (bridgeMatches.Count == 0)
            {
                return loadResult.ExitCode != 0
                    ? CommitUnsafeFailure(
                        activation,
                        Failure(
                            InjectorStage.EnumerateBridge,
                            InjectorFailureCode.BridgeModuleMissing,
                            InjectorSafetyDisposition.UnsafeRetained))
                    : CommitCleanFailure(
                        activation,
                        InjectorStage.EnumerateBridge,
                        InjectorFailureCode.BridgeModuleMissing);
            }

            if (bridgeMatches.Count != 1)
            {
                return CommitUnsafeFailure(
                    activation,
                    Failure(
                        InjectorStage.EnumerateBridge,
                        InjectorFailureCode.BridgeModuleAmbiguous,
                        InjectorSafetyDisposition.UnsafeRetained));
            }

            if (bridgeMatches[0].Size != contract.SizeOfImage)
            {
                return CommitUnsafeFailure(
                    activation,
                    Failure(
                        InjectorStage.EnumerateBridge,
                        InjectorFailureCode.BridgeModuleImageMismatch,
                        InjectorSafetyDisposition.UnsafeRetained));
            }

            var binding = new ExplorerInstanceBinding(
                permit.ExplorerProcessId,
                permit.ExplorerCreationTimeFileTime100Nanoseconds,
                activation.ActivationId);
            if (!TryAddRva(
                    bridgeMatches[0].BaseAddress,
                    bridgeMatches[0].Size,
                    contract.StartProbeRva,
                    out var remoteStartProbe))
            {
                return CommitUnsafeFailure(
                    activation,
                    Failure(
                        InjectorStage.StartProbe,
                        InjectorFailureCode.BridgeContractInvalid,
                        InjectorSafetyDisposition.UnsafeRetained));
            }

            var startResult = StartAndWait(
                process,
                remoteStartProbe,
                BridgeStartRequest.Build(binding),
                InjectorStage.StartProbe,
                mappingMayExist: true);
            if (startResult.Failure is not null)
            {
                return CommitUnsafeFailure(activation, startResult.Failure);
            }

            if (startResult.ExitCode != 0)
            {
                return CommitUnsafeFailure(
                    activation,
                    Failure(
                        InjectorStage.StartProbe,
                        InjectorFailureCode.StartProbeRejected,
                        InjectorSafetyDisposition.UnsafeRetained,
                        remoteHResult: unchecked((int)startResult.ExitCode!.Value)));
            }

            var names = BridgeEventNames.For(binding);
            var startReleased = processApi.TryOpenEvent(
                names.StartReleased,
                NativeEventAccess.Signal);
            if (!startReleased.Succeeded)
            {
                return CommitUnsafeFailure(
                    activation,
                    Failure(
                        InjectorStage.StartReleased,
                        InjectorFailureCode.StartReleasedOpenFailed,
                        InjectorSafetyDisposition.UnsafeRetained,
                        startReleased.ErrorCode));
            }

            using (startReleased.Value!)
            {
                var signaled = startReleased.Value!.Signal();
                if (!signaled.Succeeded)
                {
                    return CommitUnsafeFailure(
                        activation,
                        Failure(
                            InjectorStage.StartReleased,
                            InjectorFailureCode.StartReleasedSignalFailed,
                            InjectorSafetyDisposition.UnsafeRetained,
                            signaled.ErrorCode));
                }
            }

            var readyDeadline = timeProvider.GetTimestamp();
            shutdown = await OpenEventUntilDeadlineAsync(
                names.Shutdown,
                NativeEventAccess.Signal,
                readyDeadline);
            quiesced = await OpenEventUntilDeadlineAsync(
                names.Quiesced,
                NativeEventAccess.Wait,
                readyDeadline);
            using var ready = await OpenEventUntilDeadlineAsync(
                names.Ready,
                NativeEventAccess.Wait,
                readyDeadline);
            if (shutdown is null || quiesced is null || ready is null)
            {
                await EmergencyDetachAsync(
                    process,
                    shutdown,
                    quiesced,
                    contract.CanonicalPath,
                    contract.SizeOfImage);
                return CommitUnsafeFailure(
                    activation,
                    Failure(
                        InjectorStage.Ready,
                        InjectorFailureCode.ControlEventOpenFailed,
                        InjectorSafetyDisposition.UnsafeRetained));
            }

            var readyWait = ready.Wait(Remaining(readyDeadline));
            if (readyWait.Kind != NativeWaitKind.Signaled)
            {
                await EmergencyDetachAsync(
                    process,
                    shutdown,
                    quiesced,
                    contract.CanonicalPath,
                    contract.SizeOfImage);
                var code = readyWait.Kind == NativeWaitKind.TimedOut
                    ? InjectorFailureCode.ReadyTimeout
                    : InjectorFailureCode.ReadyWaitFailed;
                return CommitUnsafeFailure(
                    activation,
                    Failure(
                        InjectorStage.Ready,
                        code,
                        InjectorSafetyDisposition.UnsafeRetained,
                        readyWait.ErrorCode));
            }

            if (!ValidateIdentity(process, permit).Succeeded ||
                !HasExactlyOneBridge(process, contract))
            {
                await EmergencyDetachAsync(
                    process,
                    shutdown,
                    quiesced,
                    contract.CanonicalPath,
                    contract.SizeOfImage);
                return CommitUnsafeFailure(
                    activation,
                    Failure(
                        InjectorStage.Ready,
                        InjectorFailureCode.FinalValidationFailed,
                        InjectorSafetyDisposition.UnsafeRetained));
            }

            var stable = journal.TryTransition(activation.ActivationId, ActivationState.Stable);
            if (!stable.Succeeded)
            {
                await EmergencyDetachAsync(
                    process,
                    shutdown,
                    quiesced,
                    contract.CanonicalPath,
                    contract.SizeOfImage);
                return CommitUnsafeFailure(
                    activation,
                    Failure(
                        InjectorStage.JournalCommit,
                        InjectorFailureCode.JournalWriteFailed,
                        InjectorSafetyDisposition.UnsafeRetained));
            }

            var session = new ExplorerProbeSession(
                process,
                artifact,
                sessionLease!,
                shutdown,
                quiesced,
                contract.CanonicalPath,
                contract.SizeOfImage,
                activation.ActivationId);
            process = null;
            artifact = null;
            sessionLease = null;
            shutdown = null;
            quiesced = null;
            transferred = true;
            return ExplorerInjectionResult.Ready(session);
        }
        catch (Exception)
        {
            if (activation is not null)
            {
                if (mappingMayExist && process is not null && activeContract is not null)
                {
                    try
                    {
                        _ = await EmergencyDetachAsync(
                            process,
                            shutdown,
                            quiesced,
                            activeContract.CanonicalPath,
                            activeContract.SizeOfImage);
                    }
                    catch (Exception)
                    {
                        // The journal remains Unsafe even when best-effort detach itself faults.
                    }
                }

                return CommitUnsafeFailure(
                    activation,
                    Failure(
                        InjectorStage.EmergencyDetach,
                        InjectorFailureCode.UnexpectedBoundaryFailure,
                        InjectorSafetyDisposition.UnsafeRetained));
            }

            return Fail(
                InjectorStage.Artifact,
                InjectorFailureCode.ArtifactMaterializationFailed,
                InjectorSafetyDisposition.RejectedNoMutation);
        }
        finally
        {
            if (!transferred)
            {
                DisposeBestEffort(quiesced);
                DisposeBestEffort(shutdown);
                DisposeBestEffort(process);
                DisposeBestEffort(artifact);
                DisposeBestEffort(sessionLease);
            }
        }
    }

    internal Task<ExplorerDetachResult> DetachAsync(
        ExplorerProbeSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.DetachAsync(this, cancellationToken);
    }

    internal bool MarkUnsafe(ExplorerProbeSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.LatchUnsafe();
        return journal.TryTransition(session.ActivationId, ActivationState.Unsafe).Succeeded;
    }

    private async Task<ExplorerDetachResult> DetachCoreAsync(
        ExplorerProbeSession session,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (!session.ShutdownSignaled)
        {
            var signal = session.Shutdown.Signal();
            if (!signal.Succeeded)
            {
                return UnsafeDetach(
                    session,
                    InjectorFailureCode.ShutdownSignalFailed,
                    signal.ErrorCode);
            }

            session.ShutdownSignaled = true;
        }

        var started = timeProvider.GetTimestamp();
        var quiesced = session.Quiesced.Wait(Remaining(started));
        if (quiesced.Kind != NativeWaitKind.Signaled)
        {
            return UnsafeDetach(
                session,
                quiesced.Kind == NativeWaitKind.TimedOut
                    ? InjectorFailureCode.QuiescedTimeout
                    : InjectorFailureCode.QuiescedWaitFailed,
                quiesced.ErrorCode);
        }

        var absent = await WaitForModuleAbsenceAsync(
            session.Process,
            session.BridgePath,
            session.BridgeImageSize,
            started);
        if (!absent)
        {
            return UnsafeDetach(
                session,
                InjectorFailureCode.ModuleUnloadTimeout);
        }

        if (session.UnsafeLatched)
        {
            session.MarkCleanAndRelease();
            return new ExplorerDetachResult(
                ExplorerDetachOutcome.CleanButJournalBlocked,
                Failure(
                    InjectorStage.JournalCommit,
                    InjectorFailureCode.SafetyBreakerLatched,
                    InjectorSafetyDisposition.UnsafeRetained));
        }

        var clean = journal.TryTransition(session.ActivationId, ActivationState.Clean);
        if (!clean.Succeeded)
        {
            session.MarkCleanAndRelease();
            return new ExplorerDetachResult(
                ExplorerDetachOutcome.CleanButJournalBlocked,
                Failure(
                    InjectorStage.JournalCommit,
                    InjectorFailureCode.JournalWriteFailed,
                    InjectorSafetyDisposition.CleanButJournalBlocked));
        }

        session.MarkCleanAndRelease();
        return new ExplorerDetachResult(ExplorerDetachOutcome.Clean);
    }

    private ExplorerDetachResult UnsafeDetach(
        ExplorerProbeSession session,
        InjectorFailureCode code,
        int? nativeError = null)
    {
        _ = journal.TryTransition(session.ActivationId, ActivationState.Unsafe);
        return new ExplorerDetachResult(
            ExplorerDetachOutcome.Unsafe,
            Failure(
                InjectorStage.Detach,
                code,
                InjectorSafetyDisposition.UnsafeRetained,
                nativeError));
    }

    private StartAndWaitResult StartAndWait(
        IExplorerMutationLease process,
        ulong startAddress,
        ReadOnlyMemory<byte> parameterBytes,
        InjectorStage stage,
        bool mappingMayExist)
    {
        var allocation = process.Allocate(checked((nuint)parameterBytes.Length));
        if (!allocation.Succeeded)
        {
            return StartAndWaitResult.Failed(
                Failure(
                    stage,
                    InjectorFailureCode.RemoteAllocationFailed,
                    mappingMayExist
                        ? InjectorSafetyDisposition.UnsafeRetained
                        : InjectorSafetyDisposition.FailedClean,
                    allocation.ErrorCode),
                unknownRemoteState: mappingMayExist);
        }

        using var memory = allocation.Value!;
        var write = process.WriteAll(memory, parameterBytes);
        if (!write.Succeeded)
        {
            return StartAndWaitResult.Failed(
                Failure(
                    stage,
                    InjectorFailureCode.RemoteWriteFailed,
                    mappingMayExist
                        ? InjectorSafetyDisposition.UnsafeRetained
                        : InjectorSafetyDisposition.FailedClean,
                    write.ErrorCode),
                unknownRemoteState: mappingMayExist);
        }

        var started = process.StartRemoteThread(startAddress, memory);
        if (!started.Succeeded)
        {
            return StartAndWaitResult.Failed(
                Failure(
                    stage,
                    InjectorFailureCode.RemoteThreadCreateFailed,
                    mappingMayExist
                        ? InjectorSafetyDisposition.UnsafeRetained
                        : InjectorSafetyDisposition.FailedClean,
                    started.ErrorCode),
                unknownRemoteState: mappingMayExist);
        }

        using var thread = started.Value!;
        var wait = thread.Wait(OperationTimeout);
        if (wait.Kind == NativeWaitKind.TimedOut)
        {
            memory.Abandon();
            return StartAndWaitResult.Failed(
                Failure(
                    stage,
                    stage == InjectorStage.LoadLibrary
                        ? InjectorFailureCode.LoadLibraryTimeout
                        : InjectorFailureCode.StartProbeTimeout,
                    InjectorSafetyDisposition.UnsafeRetained,
                    wait.ErrorCode),
                unknownRemoteState: true);
        }

        if (wait.Kind != NativeWaitKind.Signaled || wait.ExitCode is null)
        {
            memory.Abandon();
            return StartAndWaitResult.Failed(
                Failure(
                    stage,
                    stage == InjectorStage.LoadLibrary
                        ? InjectorFailureCode.LoadLibraryWaitFailed
                        : InjectorFailureCode.StartProbeWaitFailed,
                    InjectorSafetyDisposition.UnsafeRetained,
                    wait.ErrorCode),
                unknownRemoteState: true);
        }

        return StartAndWaitResult.Success(wait.ExitCode.Value);
    }

    private NativeResult<ExplorerProcessSnapshot> ValidateIdentity(
        IExplorerReadLease process,
        LiveActivationPermit permit)
    {
        try
        {
            var snapshot = process.Snapshot();
            if (!snapshot.Succeeded)
            {
                return snapshot;
            }

            var value = snapshot.Value!;
            if (value.HasExited ||
                value.ProcessId != permit.ExplorerProcessId ||
                value.CreationTimeFileTime100Nanoseconds !=
                    permit.ExplorerCreationTimeFileTime100Nanoseconds ||
                value.SessionId != processApi.CurrentSessionId ||
                !string.Equals(value.UserSid, processApi.CurrentUserSid, StringComparison.Ordinal) ||
                value.Architecture != ExplorerProcessArchitecture.X64 ||
                !PathsEqual(value.CanonicalImagePath, permit.ExpectedExplorerPath))
            {
                return NativeResult<ExplorerProcessSnapshot>.Failure(1);
            }

            return snapshot;
        }
        catch (Exception)
        {
            return NativeResult<ExplorerProcessSnapshot>.Failure(1);
        }
    }

    private bool CanRecoverPreviousActivation(
        ActivationJournalRecord previous,
        IExplorerReadLease currentProcess,
        LiveActivationPermit permit,
        BridgeImageContract contract,
        IReadOnlyList<RemoteModule> currentModules)
    {
        if (previous.ExplorerProcessId == currentProcess.ProcessId)
        {
            var snapshot = currentProcess.Snapshot();
            if (!snapshot.Succeeded || snapshot.Value is null)
            {
                return false;
            }

            if (snapshot.Value.CreationTimeFileTime100Nanoseconds !=
                previous.ExplorerCreationTimeFileTime100ns)
            {
                return true;
            }

            return FindModulesByPath(currentModules, contract.CanonicalPath).Count == 0;
        }

        NativeResult<IExplorerReadLease> opened;
        try
        {
            opened = processApi.OpenForCollection(previous.ExplorerProcessId);
        }
        catch (Exception)
        {
            return false;
        }

        if (!opened.Succeeded)
        {
            return opened.ErrorCode == NativeErrors.InvalidParameter;
        }

        if (opened.Value is null)
        {
            return false;
        }

        using var previousProcess = opened.Value;
        var previousSnapshot = previousProcess.Snapshot();
        if (!previousSnapshot.Succeeded || previousSnapshot.Value is null)
        {
            return false;
        }

        var value = previousSnapshot.Value;
        if (value.HasExited ||
            value.CreationTimeFileTime100Nanoseconds !=
                previous.ExplorerCreationTimeFileTime100ns)
        {
            return true;
        }

        if (value.SessionId != processApi.CurrentSessionId ||
            !string.Equals(value.UserSid, processApi.CurrentUserSid, StringComparison.Ordinal) ||
            value.Architecture != ExplorerProcessArchitecture.X64 ||
            !PathsEqual(value.CanonicalImagePath, permit.ExpectedExplorerPath))
        {
            return false;
        }

        var modules = previousProcess.EnumerateModules();
        return modules.Succeeded &&
            modules.Value is not null &&
            FindModulesByPath(modules.Value, contract.CanonicalPath).Count == 0;
    }

    private static void DisposeBestEffort(IDisposable? resource)
    {
        try
        {
            resource?.Dispose();
        }
        catch (Exception)
        {
            // Cleanup is independent: one faulty boundary must not skip the remaining leases.
        }
    }

    private static RemoteModule? FindSystemModule(
        IReadOnlyList<RemoteModule> modules,
        LocalSystemExport export)
    {
        var matches = modules.Where(module =>
                PathsEqual(module.Path, export.ModulePath) &&
                module.Size == export.ImageSize)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static IReadOnlyList<RemoteModule> FindModulesByPath(
        IReadOnlyList<RemoteModule> modules,
        string path) =>
        modules.Where(module => PathsEqual(module.Path, path)).ToArray();

    private static bool HasUnexpectedImageSize(
        IReadOnlyList<RemoteModule> modules,
        uint expectedSize) =>
        modules.Any(module => module.Size != expectedSize);

    private static bool TryAddRva(
        ulong moduleBase,
        uint moduleSize,
        uint rva,
        out ulong address)
    {
        if (moduleBase == 0 || rva == 0 || rva >= moduleSize)
        {
            address = 0;
            return false;
        }

        try
        {
            address = checked(moduleBase + rva);
            return true;
        }
        catch (OverflowException)
        {
            address = 0;
            return false;
        }
    }

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(first),
                Path.GetFullPath(second),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private bool HasExactlyOneBridge(
        IExplorerReadLease process,
        BridgeImageContract contract)
    {
        var modules = process.EnumerateModules();
        if (!modules.Succeeded || modules.Value is null)
        {
            return false;
        }

        var matches = FindModulesByPath(modules.Value, contract.CanonicalPath);
        return matches.Count == 1 && matches[0].Size == contract.SizeOfImage;
    }

    private async Task<INamedEvent?> OpenEventUntilDeadlineAsync(
        string name,
        NativeEventAccess access,
        long started)
    {
        for (var attempt = 0; attempt < MaximumEventOpenAttempts; attempt++)
        {
            var opened = processApi.TryOpenEvent(name, access);
            if (opened.Succeeded)
            {
                return opened.Value;
            }

            var remaining = Remaining(started);
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await processApi.DelayAsync(
                remaining < EventRetryDelay ? remaining : EventRetryDelay,
                CancellationToken.None);
        }

        return null;
    }

    private async Task<bool> EmergencyDetachAsync(
        IExplorerReadLease process,
        INamedEvent? shutdown,
        INamedEvent? quiesced,
        string bridgePath,
        uint bridgeImageSize)
    {
        if (shutdown is null)
        {
            return false;
        }

        var started = timeProvider.GetTimestamp();
        var signal = shutdown.Signal();
        if (!signal.Succeeded)
        {
            return false;
        }

        if (quiesced is null)
        {
            return false;
        }

        var wait = quiesced.Wait(Remaining(started));
        if (wait.Kind != NativeWaitKind.Signaled)
        {
            return false;
        }

        return await WaitForModuleAbsenceAsync(
            process,
            bridgePath,
            bridgeImageSize,
            started);
    }

    private async Task<bool> WaitForModuleAbsenceAsync(
        IExplorerReadLease process,
        string bridgePath,
        uint _,
        long started)
    {
        for (var attempt = 0; attempt < MaximumModulePollAttempts; attempt++)
        {
            var modules = process.EnumerateModules();
            if (modules.Succeeded &&
                modules.Value is not null &&
                FindModulesByPath(modules.Value, bridgePath).Count == 0)
            {
                return true;
            }

            if (!modules.Succeeded &&
                process.WaitForProcessExit(TimeSpan.Zero).Kind == NativeWaitKind.Signaled)
            {
                return true;
            }

            var remaining = Remaining(started);
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await processApi.DelayAsync(
                remaining < ModulePollDelay ? remaining : ModulePollDelay,
                CancellationToken.None);
        }

        return false;
    }

    private TimeSpan Remaining(long started)
    {
        var elapsed = timeProvider.GetElapsedTime(started);
        return elapsed >= OperationTimeout ? TimeSpan.Zero : OperationTimeout - elapsed;
    }

    private ExplorerInjectionResult CommitCleanFailure(
        ActivationJournalRecord activation,
        InjectorStage stage,
        InjectorFailureCode code,
        int? nativeError = null)
    {
        var clean = journal.TryTransition(activation.ActivationId, ActivationState.Clean);
        return Fail(
            stage,
            code,
            clean.Succeeded
                ? InjectorSafetyDisposition.FailedClean
                : InjectorSafetyDisposition.CleanButJournalBlocked,
            nativeError);
    }

    private ExplorerInjectionResult CommitUnsafeFailure(
        ActivationJournalRecord activation,
        InjectorFailure failure)
    {
        _ = journal.TryTransition(activation.ActivationId, ActivationState.Unsafe);
        return ExplorerInjectionResult.Failed(failure with
        {
            Disposition = InjectorSafetyDisposition.UnsafeRetained,
        });
    }

    private static ExplorerInjectionResult Fail(
        InjectorStage stage,
        InjectorFailureCode code,
        InjectorSafetyDisposition disposition,
        int? nativeError = null,
        int? remoteHResult = null) =>
        ExplorerInjectionResult.Failed(
            Failure(stage, code, disposition, nativeError, remoteHResult));

    private static InjectorFailure Failure(
        InjectorStage stage,
        InjectorFailureCode code,
        InjectorSafetyDisposition disposition,
        int? nativeError = null,
        int? remoteHResult = null) =>
        new(stage, code, disposition, nativeError, remoteHResult);

    private sealed record StartAndWaitResult(
        uint? ExitCode,
        InjectorFailure? Failure,
        bool UnknownRemoteState)
    {
        internal static StartAndWaitResult Success(uint exitCode) =>
            new(exitCode, null, false);

        internal static StartAndWaitResult Failed(
            InjectorFailure failure,
            bool unknownRemoteState) =>
            new(null, failure, unknownRemoteState);
    }

    internal sealed class ExplorerProbeSession
    {
        private readonly object syncRoot = new();
        private Task<ExplorerDetachResult>? activeDetach;
        private bool clean;
        private bool resourcesReleased;

        internal ExplorerProbeSession(
            IExplorerMutationLease process,
            IBridgeArtifactLease artifact,
            IActivationSessionLease activationLease,
            INamedEvent shutdown,
            INamedEvent quiesced,
            string bridgePath,
            uint bridgeImageSize,
            Guid activationId)
        {
            Process = process;
            Artifact = artifact;
            ActivationLease = activationLease;
            Shutdown = shutdown;
            Quiesced = quiesced;
            BridgePath = bridgePath;
            BridgeImageSize = bridgeImageSize;
            ActivationId = activationId;
        }

        internal IExplorerMutationLease Process { get; }

        internal IBridgeArtifactLease Artifact { get; }

        internal IActivationSessionLease ActivationLease { get; }

        internal INamedEvent Shutdown { get; }

        internal INamedEvent Quiesced { get; }

        internal string BridgePath { get; }

        internal uint BridgeImageSize { get; }

        internal Guid ActivationId { get; }

        internal bool ShutdownSignaled { get; set; }

        internal bool UnsafeLatched
        {
            get
            {
                lock (syncRoot)
                {
                    return unsafeLatched;
                }
            }
        }

        private bool unsafeLatched;

        internal void LatchUnsafe()
        {
            lock (syncRoot)
            {
                unsafeLatched = true;
            }
        }

        internal Task<ExplorerDetachResult> DetachAsync(
            ExplorerInjector owner,
            CancellationToken cancellationToken)
        {
            TaskCompletionSource<ExplorerDetachResult>? completion = null;
            lock (syncRoot)
            {
                if (clean)
                {
                    return Task.FromResult(
                        new ExplorerDetachResult(ExplorerDetachOutcome.AlreadyClean));
                }

                if (activeDetach is not null)
                {
                    return activeDetach;
                }

                completion = new TaskCompletionSource<ExplorerDetachResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                activeDetach = completion.Task;
            }

            _ = CompleteDetachAsync(owner, cancellationToken, completion);
            return completion.Task;
        }

        internal void MarkCleanAndRelease()
        {
            lock (syncRoot)
            {
                clean = true;
            }

            ReleaseResources();
        }

        internal void ReleaseResources()
        {
            lock (syncRoot)
            {
                if (resourcesReleased)
                {
                    return;
                }

                resourcesReleased = true;
            }

            DisposeBestEffort(Quiesced);
            DisposeBestEffort(Shutdown);
            DisposeBestEffort(Process);
            DisposeBestEffort(Artifact);
            DisposeBestEffort(ActivationLease);
        }

        private async Task CompleteDetachAsync(
            ExplorerInjector owner,
            CancellationToken cancellationToken,
            TaskCompletionSource<ExplorerDetachResult> completion)
        {
            try
            {
                var result = await owner.DetachCoreAsync(this, cancellationToken)
                    .ConfigureAwait(false);
                completion.TrySetResult(result);
            }
            catch (OperationCanceledException exception)
            {
                completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                lock (syncRoot)
                {
                    if (ReferenceEquals(activeDetach, completion.Task))
                    {
                        activeDetach = null;
                    }
                }
            }
        }
    }
}
