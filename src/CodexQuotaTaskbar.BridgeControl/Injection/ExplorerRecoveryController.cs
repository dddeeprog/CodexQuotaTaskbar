using CodexQuotaTaskbar.BridgeControl.Safety;

namespace CodexQuotaTaskbar.BridgeControl.Injection;

internal enum ExplorerRecoveryStage
{
    JournalGate,
    Contract,
    OpenTarget,
    ValidateTarget,
    EnumerateBridge,
    Shutdown,
    Quiesced,
    VerifyUnload,
    JournalCommit,
}

internal enum ExplorerRecoveryFailureCode
{
    ActivationSessionBusy,
    JournalUnavailable,
    InvalidBridgeContract,
    OpenProcessFailed,
    TargetSnapshotFailed,
    TargetIdentityMismatch,
    ModuleEnumerationFailed,
    BridgeModuleAmbiguous,
    BridgeModuleImageMismatch,
    ShutdownOpenFailed,
    ShutdownSignalFailed,
    QuiescedOpenFailed,
    QuiescedTimeout,
    QuiescedWaitFailed,
    ModuleUnloadTimeout,
    JournalWriteFailed,
    UnexpectedBoundaryFailure,
}

internal enum ExplorerRecoveryOutcome
{
    Clean,
    AlreadyClean,
    Unsafe,
    CleanButJournalBlocked,
}

internal sealed record ExplorerRecoveryFailure(
    ExplorerRecoveryStage Stage,
    ExplorerRecoveryFailureCode Code,
    int? NativeError = null);

internal sealed record ExplorerRecoveryResult(
    ExplorerRecoveryOutcome Outcome,
    ExplorerRecoveryFailure? Failure = null);

internal interface IActivationRecoveryJournalStore
{
    ActivationSessionLeaseResult TryAcquireSessionLease();

    ActivationJournalResult ReadStatus();

    ActivationJournalResult TryRecoverToClean(
        Guid activationId,
        int explorerProcessId,
        ulong explorerCreationTimeFileTime100ns);
}

internal sealed class ActivationRecoveryJournalStore :
    IActivationRecoveryJournalStore
{
    private readonly ActivationJournal journal;

    internal ActivationRecoveryJournalStore(ActivationJournal journal) =>
        this.journal = journal ?? throw new ArgumentNullException(nameof(journal));

    public ActivationSessionLeaseResult TryAcquireSessionLease() =>
        journal.TryAcquireSessionLease();

    public ActivationJournalResult ReadStatus() => journal.ReadStatus();

    public ActivationJournalResult TryRecoverToClean(
        Guid activationId,
        int explorerProcessId,
        ulong explorerCreationTimeFileTime100ns) =>
        journal.TryRecoverToClean(
            activationId,
            explorerProcessId,
            explorerCreationTimeFileTime100ns);
}

internal sealed class ExplorerRecoveryController
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ModulePollDelay = TimeSpan.FromMilliseconds(50);
    private const int MaximumModulePollAttempts = 200;
    private readonly IExplorerProcessApi processApi;
    private readonly IActivationRecoveryJournalStore journal;
    private readonly TimeProvider timeProvider;

    internal ExplorerRecoveryController(
        IExplorerProcessApi processApi,
        IActivationRecoveryJournalStore journal,
        TimeProvider? timeProvider = null)
    {
        this.processApi = processApi ?? throw new ArgumentNullException(nameof(processApi));
        this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal Task<ExplorerRecoveryResult> DetachRecordedAsync(
        BridgeImageContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return DetachRecordedCoreAsync(contract);
    }

    private async Task<ExplorerRecoveryResult> DetachRecordedCoreAsync(
        BridgeImageContract contract)
    {
        IActivationSessionLease? sessionLease = null;
        IExplorerReadLease? process = null;
        try
        {
            ActivationSessionLeaseResult acquired;
            try
            {
                acquired = journal.TryAcquireSessionLease();
            }
            catch
            {
                return Fail(
                    ExplorerRecoveryStage.JournalGate,
                    ExplorerRecoveryFailureCode.JournalUnavailable);
            }

            if (!acquired.Succeeded || acquired.Lease is null)
            {
                return Fail(
                    ExplorerRecoveryStage.JournalGate,
                    acquired.Outcome == ActivationSessionLeaseOutcome.Busy
                        ? ExplorerRecoveryFailureCode.ActivationSessionBusy
                        : ExplorerRecoveryFailureCode.JournalUnavailable);
            }

            sessionLease = acquired.Lease;

            ActivationJournalResult status;
            try
            {
                status = journal.ReadStatus();
            }
            catch
            {
                return Fail(
                    ExplorerRecoveryStage.JournalGate,
                    ExplorerRecoveryFailureCode.JournalUnavailable);
            }

            if (status.Outcome == ActivationJournalOutcome.Allowed &&
                status.Record is null or { State: ActivationState.Clean })
            {
                return new ExplorerRecoveryResult(ExplorerRecoveryOutcome.AlreadyClean);
            }

            if (status.Outcome != ActivationJournalOutcome.Blocked ||
                status.Record is not
                {
                    State: ActivationState.Pending or
                        ActivationState.Stable or
                        ActivationState.Unsafe,
                } record)
            {
                return Fail(
                    ExplorerRecoveryStage.JournalGate,
                    ExplorerRecoveryFailureCode.JournalUnavailable);
            }

            if (!TryNormalizeContract(contract, out var bridgePath))
            {
                return Fail(
                    ExplorerRecoveryStage.Contract,
                    ExplorerRecoveryFailureCode.InvalidBridgeContract);
            }

            NativeResult<IExplorerReadLease> opened;
            try
            {
                opened = processApi.OpenForCollection(record.ExplorerProcessId);
            }
            catch
            {
                return Fail(
                    ExplorerRecoveryStage.OpenTarget,
                    ExplorerRecoveryFailureCode.OpenProcessFailed);
            }

            if (!opened.Succeeded && opened.ErrorCode == NativeErrors.InvalidParameter)
            {
                return RecoverClean(record);
            }

            if (!opened.Succeeded || opened.Value is null)
            {
                return Fail(
                    ExplorerRecoveryStage.OpenTarget,
                    ExplorerRecoveryFailureCode.OpenProcessFailed,
                    opened.ErrorCode);
            }

            process = opened.Value;
            NativeResult<ExplorerProcessSnapshot> snapshotResult;
            try
            {
                snapshotResult = process.Snapshot();
            }
            catch
            {
                return Fail(
                    ExplorerRecoveryStage.ValidateTarget,
                    ExplorerRecoveryFailureCode.TargetSnapshotFailed);
            }

            if (!snapshotResult.Succeeded || snapshotResult.Value is null)
            {
                return Fail(
                    ExplorerRecoveryStage.ValidateTarget,
                    ExplorerRecoveryFailureCode.TargetSnapshotFailed,
                    snapshotResult.ErrorCode);
            }

            var snapshot = snapshotResult.Value;
            if (process.ProcessId != record.ExplorerProcessId ||
                snapshot.ProcessId != record.ExplorerProcessId)
            {
                return Fail(
                    ExplorerRecoveryStage.ValidateTarget,
                    ExplorerRecoveryFailureCode.TargetIdentityMismatch);
            }

            if (snapshot.HasExited ||
                snapshot.CreationTimeFileTime100Nanoseconds !=
                    record.ExplorerCreationTimeFileTime100ns)
            {
                return RecoverClean(record);
            }

            if (!HasExpectedLiveIdentity(snapshot))
            {
                return Fail(
                    ExplorerRecoveryStage.ValidateTarget,
                    ExplorerRecoveryFailureCode.TargetIdentityMismatch);
            }

            NativeResult<IReadOnlyList<RemoteModule>> modulesResult;
            try
            {
                modulesResult = process.EnumerateModules();
            }
            catch
            {
                return Fail(
                    ExplorerRecoveryStage.EnumerateBridge,
                    ExplorerRecoveryFailureCode.ModuleEnumerationFailed);
            }

            if (!modulesResult.Succeeded || modulesResult.Value is null)
            {
                return Fail(
                    ExplorerRecoveryStage.EnumerateBridge,
                    ExplorerRecoveryFailureCode.ModuleEnumerationFailed,
                    modulesResult.ErrorCode);
            }

            if (!TryFindBridgeModules(
                    modulesResult.Value,
                    bridgePath,
                    out var bridgeModules))
            {
                return Fail(
                    ExplorerRecoveryStage.EnumerateBridge,
                    ExplorerRecoveryFailureCode.ModuleEnumerationFailed);
            }

            if (bridgeModules.Count == 0)
            {
                return RecoverClean(record);
            }

            if (bridgeModules.Count != 1)
            {
                return Fail(
                    ExplorerRecoveryStage.EnumerateBridge,
                    ExplorerRecoveryFailureCode.BridgeModuleAmbiguous);
            }

            if (bridgeModules[0].Size != contract.SizeOfImage)
            {
                return Fail(
                    ExplorerRecoveryStage.EnumerateBridge,
                    ExplorerRecoveryFailureCode.BridgeModuleImageMismatch);
            }

            return await DetachMappedBridgeAsync(process, record, bridgePath);
        }
        catch
        {
            return Fail(
                ExplorerRecoveryStage.ValidateTarget,
                ExplorerRecoveryFailureCode.UnexpectedBoundaryFailure);
        }
        finally
        {
            DisposeBestEffort(process);
            DisposeBestEffort(sessionLease);
        }
    }

    private async Task<ExplorerRecoveryResult> DetachMappedBridgeAsync(
        IExplorerReadLease process,
        ActivationJournalRecord record,
        string bridgePath)
    {
        INamedEvent? shutdown = null;
        INamedEvent? quiesced = null;
        try
        {
            var eventNames = BridgeEventNames.For(
                new ExplorerInstanceBinding(
                    record.ExplorerProcessId,
                    record.ExplorerCreationTimeFileTime100ns,
                    record.ActivationId));

            NativeResult<INamedEvent> openedShutdown;
            try
            {
                openedShutdown = processApi.TryOpenEvent(
                    eventNames.Shutdown,
                    NativeEventAccess.Signal);
            }
            catch
            {
                openedShutdown = NativeResult<INamedEvent>.Failure(0);
            }

            if (!openedShutdown.Succeeded || openedShutdown.Value is null)
            {
                var racedClean = CheckPhysicalAbsence(process, bridgePath);
                if (racedClean.Kind == ModuleAbsenceKind.Absent)
                {
                    return RecoverClean(record);
                }

                return Fail(
                    ExplorerRecoveryStage.Shutdown,
                    ExplorerRecoveryFailureCode.ShutdownOpenFailed,
                    openedShutdown.ErrorCode);
            }

            shutdown = openedShutdown.Value;
            var started = timeProvider.GetTimestamp();
            NativeResult shutdownSignal;
            try
            {
                shutdownSignal = shutdown.Signal();
            }
            catch
            {
                return Fail(
                    ExplorerRecoveryStage.Shutdown,
                    ExplorerRecoveryFailureCode.ShutdownSignalFailed);
            }

            if (!shutdownSignal.Succeeded)
            {
                return Fail(
                    ExplorerRecoveryStage.Shutdown,
                    ExplorerRecoveryFailureCode.ShutdownSignalFailed,
                    shutdownSignal.ErrorCode);
            }

            NativeResult<INamedEvent> openedQuiesced;
            try
            {
                openedQuiesced = processApi.TryOpenEvent(
                    eventNames.Quiesced,
                    NativeEventAccess.Wait);
            }
            catch
            {
                return Fail(
                    ExplorerRecoveryStage.Quiesced,
                    ExplorerRecoveryFailureCode.QuiescedOpenFailed);
            }

            if (!openedQuiesced.Succeeded || openedQuiesced.Value is null)
            {
                return Fail(
                    ExplorerRecoveryStage.Quiesced,
                    ExplorerRecoveryFailureCode.QuiescedOpenFailed,
                    openedQuiesced.ErrorCode);
            }

            quiesced = openedQuiesced.Value;
            NativeWaitResult quiescedWait;
            try
            {
                quiescedWait = quiesced.Wait(Remaining(started));
            }
            catch
            {
                return Fail(
                    ExplorerRecoveryStage.Quiesced,
                    ExplorerRecoveryFailureCode.QuiescedWaitFailed);
            }

            if (quiescedWait.Kind == NativeWaitKind.TimedOut)
            {
                return Fail(
                    ExplorerRecoveryStage.Quiesced,
                    ExplorerRecoveryFailureCode.QuiescedTimeout,
                    quiescedWait.ErrorCode);
            }

            if (quiescedWait.Kind != NativeWaitKind.Signaled)
            {
                return Fail(
                    ExplorerRecoveryStage.Quiesced,
                    ExplorerRecoveryFailureCode.QuiescedWaitFailed,
                    quiescedWait.ErrorCode);
            }

            var absence = await WaitForModuleAbsenceAsync(
                process,
                bridgePath,
                started);
            return absence.Kind switch
            {
                ModuleAbsenceKind.Absent => RecoverClean(record),
                ModuleAbsenceKind.EnumerationFailed => Fail(
                    ExplorerRecoveryStage.VerifyUnload,
                    ExplorerRecoveryFailureCode.ModuleEnumerationFailed,
                    absence.NativeError),
                _ => Fail(
                    ExplorerRecoveryStage.VerifyUnload,
                    ExplorerRecoveryFailureCode.ModuleUnloadTimeout),
            };
        }
        catch
        {
            return Fail(
                ExplorerRecoveryStage.VerifyUnload,
                ExplorerRecoveryFailureCode.UnexpectedBoundaryFailure);
        }
        finally
        {
            DisposeBestEffort(quiesced);
            DisposeBestEffort(shutdown);
        }
    }

    private async Task<ModuleAbsenceResult> WaitForModuleAbsenceAsync(
        IExplorerReadLease process,
        string bridgePath,
        long started)
    {
        for (var attempt = 0; attempt < MaximumModulePollAttempts; attempt++)
        {
            var remaining = Remaining(started);
            if (remaining <= TimeSpan.Zero)
            {
                return new ModuleAbsenceResult(ModuleAbsenceKind.TimedOut);
            }

            var absence = CheckPhysicalAbsence(process, bridgePath);
            if (absence.Kind != ModuleAbsenceKind.Present)
            {
                return absence;
            }

            try
            {
                await processApi.DelayAsync(
                    remaining < ModulePollDelay ? remaining : ModulePollDelay,
                    CancellationToken.None);
            }
            catch
            {
                return new ModuleAbsenceResult(ModuleAbsenceKind.EnumerationFailed);
            }
        }

        return new ModuleAbsenceResult(ModuleAbsenceKind.TimedOut);
    }

    private static ModuleAbsenceResult CheckPhysicalAbsence(
        IExplorerReadLease process,
        string bridgePath)
    {
        NativeResult<IReadOnlyList<RemoteModule>> modules;
        try
        {
            modules = process.EnumerateModules();
        }
        catch
        {
            return ProcessExited(process)
                ? new ModuleAbsenceResult(ModuleAbsenceKind.Absent)
                : new ModuleAbsenceResult(ModuleAbsenceKind.EnumerationFailed);
        }

        if (!modules.Succeeded || modules.Value is null)
        {
            return ProcessExited(process)
                ? new ModuleAbsenceResult(ModuleAbsenceKind.Absent)
                : new ModuleAbsenceResult(
                    ModuleAbsenceKind.EnumerationFailed,
                    modules.ErrorCode);
        }

        if (!TryFindBridgeModules(modules.Value, bridgePath, out var matches))
        {
            return new ModuleAbsenceResult(ModuleAbsenceKind.EnumerationFailed);
        }

        return new ModuleAbsenceResult(
            matches.Count == 0
                ? ModuleAbsenceKind.Absent
                : ModuleAbsenceKind.Present);
    }

    private static bool ProcessExited(IExplorerReadLease process)
    {
        try
        {
            return process.WaitForProcessExit(TimeSpan.Zero).Kind ==
                NativeWaitKind.Signaled;
        }
        catch
        {
            return false;
        }
    }

    private TimeSpan Remaining(long started)
    {
        var elapsed = timeProvider.GetElapsedTime(started);
        return elapsed >= OperationTimeout
            ? TimeSpan.Zero
            : OperationTimeout - elapsed;
    }

    private bool HasExpectedLiveIdentity(ExplorerProcessSnapshot snapshot)
    {
        try
        {
            return snapshot.SessionId == processApi.CurrentSessionId &&
                string.Equals(
                    snapshot.UserSid,
                    processApi.CurrentUserSid,
                    StringComparison.Ordinal) &&
                snapshot.Architecture == ExplorerProcessArchitecture.X64;
        }
        catch
        {
            return false;
        }
    }

    private ExplorerRecoveryResult RecoverClean(ActivationJournalRecord record)
    {
        ActivationJournalResult recovered;
        try
        {
            recovered = journal.TryRecoverToClean(
                record.ActivationId,
                record.ExplorerProcessId,
                record.ExplorerCreationTimeFileTime100ns);
        }
        catch
        {
            return JournalBlocked();
        }

        return recovered.Outcome == ActivationJournalOutcome.TransitionPersisted &&
            recovered.Record is
            {
                State: ActivationState.Clean,
            } clean &&
            clean.ActivationId == record.ActivationId &&
            clean.ExplorerProcessId == record.ExplorerProcessId &&
            clean.ExplorerCreationTimeFileTime100ns ==
                record.ExplorerCreationTimeFileTime100ns
            ? new ExplorerRecoveryResult(ExplorerRecoveryOutcome.Clean)
            : JournalBlocked();
    }

    private static ExplorerRecoveryResult JournalBlocked() =>
        new(
            ExplorerRecoveryOutcome.CleanButJournalBlocked,
            new ExplorerRecoveryFailure(
                ExplorerRecoveryStage.JournalCommit,
                ExplorerRecoveryFailureCode.JournalWriteFailed));

    private static ExplorerRecoveryResult Fail(
        ExplorerRecoveryStage stage,
        ExplorerRecoveryFailureCode code,
        int? nativeError = null) =>
        new(
            ExplorerRecoveryOutcome.Unsafe,
            new ExplorerRecoveryFailure(stage, code, nativeError));

    private static bool TryNormalizeContract(
        BridgeImageContract contract,
        out string bridgePath)
    {
        bridgePath = string.Empty;
        try
        {
            if (contract.SizeOfImage == 0 ||
                string.IsNullOrWhiteSpace(contract.CanonicalPath) ||
                !Path.IsPathFullyQualified(contract.CanonicalPath))
            {
                return false;
            }

            bridgePath = Path.GetFullPath(contract.CanonicalPath);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryFindBridgeModules(
        IReadOnlyList<RemoteModule> modules,
        string bridgePath,
        out IReadOnlyList<RemoteModule> matches)
    {
        var found = new List<RemoteModule>();
        try
        {
            foreach (var module in modules)
            {
                if (module is null || string.IsNullOrWhiteSpace(module.Path))
                {
                    matches = [];
                    return false;
                }

                if (string.Equals(
                        Path.GetFullPath(module.Path),
                        bridgePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(module);
                }
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            matches = [];
            return false;
        }

        matches = found;
        return true;
    }

    private static void DisposeBestEffort(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch
        {
            // Recovery never widens authority to compensate for cleanup failures.
        }
    }

    private enum ModuleAbsenceKind
    {
        Absent,
        Present,
        EnumerationFailed,
        TimedOut,
    }

    private readonly record struct ModuleAbsenceResult(
        ModuleAbsenceKind Kind,
        int? NativeError = null);
}
