using CodexQuotaTaskbar.BridgeControl.Injection;
using CodexQuotaTaskbar.BridgeControl.Safety;

namespace CodexQuotaTaskbar.BridgeControl.Tests;

public sealed class ExplorerRecoveryControllerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(ActivationState.Clean)]
    public async Task Missing_and_clean_records_are_idempotent_without_opening_explorer(
        ActivationState? state)
    {
        var harness = new RecoveryHarness(state);

        var result = await harness.Controller.DetachRecordedAsync(harness.Contract);

        Assert.Equal(ExplorerRecoveryOutcome.AlreadyClean, result.Outcome);
        Assert.Null(result.Failure);
        Assert.Equal(0, harness.Api.OpenCollectionCount);
        Assert.Equal(0, harness.Api.OpenInjectionCount);
        Assert.Equal(0, harness.Journal.RecoveryCount);
        AssertReleased(harness);
    }

    [Theory]
    [InlineData("Busy", "ActivationSessionBusy")]
    [InlineData("StorageFailure", "JournalUnavailable")]
    public async Task Session_lease_failures_are_typed_before_process_access(
        string leaseOutcome,
        string expectedFailure)
    {
        var harness = new RecoveryHarness(ActivationState.Unsafe);
        harness.Journal.LeaseOutcome = Enum.Parse<ActivationSessionLeaseOutcome>(leaseOutcome);

        var result = await harness.Controller.DetachRecordedAsync(harness.Contract);

        Assert.Equal(ExplorerRecoveryOutcome.Unsafe, result.Outcome);
        Assert.Equal(Enum.Parse<ExplorerRecoveryFailureCode>(expectedFailure), result.Failure!.Code);
        Assert.Equal(ExplorerRecoveryStage.JournalGate, result.Failure.Stage);
        Assert.Equal(0, harness.Api.OpenCollectionCount);
        Assert.Equal(0, harness.Api.OpenInjectionCount);
        Assert.Equal(0, harness.Journal.RecoveryCount);
    }

    [Theory]
    [InlineData(ActivationState.Pending)]
    [InlineData(ActivationState.Stable)]
    [InlineData(ActivationState.Unsafe)]
    public async Task Recorded_dirty_states_recover_when_the_recorded_process_has_exited(
        ActivationState state)
    {
        var harness = new RecoveryHarness(state);
        harness.Process.SnapshotValue = RecoveryHarness.CreateSnapshot(hasExited: true);

        var result = await harness.Controller.DetachRecordedAsync(harness.Contract);

        Assert.Equal(ExplorerRecoveryOutcome.Clean, result.Outcome);
        Assert.Null(result.Failure);
        Assert.Equal(1, harness.Api.OpenCollectionCount);
        Assert.Equal(0, harness.Api.OpenInjectionCount);
        AssertRecoveryMatchesRecord(harness);
        Assert.Equal(0, harness.Process.ModuleReadCount);
        Assert.Empty(harness.Api.OpenedEventKinds);
        AssertReleased(harness, processExpected: true);
    }

    [Theory]
    [InlineData(ActivationState.Pending)]
    [InlineData(ActivationState.Stable)]
    [InlineData(ActivationState.Unsafe)]
    public async Task Recorded_dirty_states_recover_when_the_recorded_pid_no_longer_exists(
        ActivationState state)
    {
        var harness = new RecoveryHarness(state);
        harness.Api.OpenCollectionError = 87;

        var result = await harness.Controller.DetachRecordedAsync(harness.Contract);

        Assert.Equal(ExplorerRecoveryOutcome.Clean, result.Outcome);
        Assert.Null(result.Failure);
        Assert.Equal(1, harness.Api.OpenCollectionCount);
        Assert.Equal(0, harness.Api.OpenInjectionCount);
        AssertRecoveryMatchesRecord(harness);
        Assert.Equal(0, harness.Process.ModuleReadCount);
        Assert.Empty(harness.Api.OpenedEventKinds);
        AssertReleased(harness);
    }

    [Fact]
    public async Task Reused_pid_recovers_without_touching_the_new_process()
    {
        var harness = new RecoveryHarness(ActivationState.Unsafe);
        harness.Process.SnapshotValue = RecoveryHarness.CreateSnapshot(
            creationTime: RecoveryHarness.CreationTime + 1);

        var result = await harness.Controller.DetachRecordedAsync(harness.Contract);

        Assert.Equal(ExplorerRecoveryOutcome.Clean, result.Outcome);
        AssertRecoveryMatchesRecord(harness);
        Assert.Equal(0, harness.Process.ModuleReadCount);
        Assert.Empty(harness.Api.OpenedEventKinds);
        AssertReleased(harness, processExpected: true);
    }

    [Theory]
    [InlineData("lease-pid")]
    [InlineData("snapshot-pid")]
    [InlineData("session")]
    [InlineData("sid")]
    [InlineData("architecture")]
    public async Task Live_process_identity_mismatch_fails_closed_before_events(
        string mismatch)
    {
        var harness = new RecoveryHarness(ActivationState.Unsafe);
        switch (mismatch)
        {
            case "lease-pid":
                harness.Process.ProcessIdValue++;
                break;
            case "snapshot-pid":
                harness.Process.SnapshotValue = RecoveryHarness.CreateSnapshot(
                    processId: RecoveryHarness.ProcessId + 1);
                break;
            case "session":
                harness.Process.SnapshotValue = RecoveryHarness.CreateSnapshot(
                    sessionId: RecoveryHarness.SessionId + 1);
                break;
            case "sid":
                harness.Process.SnapshotValue = RecoveryHarness.CreateSnapshot(
                    userSid: "S-1-5-21-someone-else");
                break;
            case "architecture":
                harness.Process.SnapshotValue = RecoveryHarness.CreateSnapshot(
                    architecture: ExplorerProcessArchitecture.Arm64);
                break;
        }

        var result = await harness.Controller.DetachRecordedAsync(harness.Contract);

        Assert.Equal(ExplorerRecoveryOutcome.Unsafe, result.Outcome);
        Assert.Equal(ExplorerRecoveryFailureCode.TargetIdentityMismatch, result.Failure!.Code);
        Assert.Equal(ExplorerRecoveryStage.ValidateTarget, result.Failure.Stage);
        Assert.Equal(0, harness.Process.ModuleReadCount);
        Assert.Equal(0, harness.Journal.RecoveryCount);
        Assert.Empty(harness.Api.OpenedEventKinds);
        AssertReleased(harness, processExpected: true);
    }

    [Fact]
    public async Task Initially_absent_bridge_recovers_without_opening_control_events()
    {
        var harness = new RecoveryHarness(ActivationState.Pending);
        harness.Process.Modules = [];

        var result = await harness.Controller.DetachRecordedAsync(harness.Contract);

        Assert.Equal(ExplorerRecoveryOutcome.Clean, result.Outcome);
        AssertRecoveryMatchesRecord(harness);
        Assert.Equal(1, harness.Process.ModuleReadCount);
        Assert.Empty(harness.Api.OpenedEventKinds);
        AssertReleased(harness, processExpected: true);
    }

    [Theory]
    [InlineData("InvalidRecord")]
    [InlineData("StorageFailure")]
    public async Task Invalid_or_unavailable_journal_fails_closed_and_releases_the_session_lease(
        string outcome)
    {
        var harness = new RecoveryHarness(ActivationState.Unsafe);
        harness.Journal.ReadOutcome = Enum.Parse<ActivationJournalOutcome>(outcome);

        var result = await harness.Controller.DetachRecordedAsync(harness.Contract);

        Assert.Equal(ExplorerRecoveryOutcome.Unsafe, result.Outcome);
        Assert.Equal(ExplorerRecoveryFailureCode.JournalUnavailable, result.Failure!.Code);
        Assert.Equal(0, harness.Api.OpenCollectionCount);
        AssertReleased(harness);
    }

    [Fact]
    public async Task Process_open_snapshot_and_module_failures_are_typed_and_fail_closed()
    {
        var openFailure = new RecoveryHarness(ActivationState.Unsafe);
        openFailure.Api.OpenCollectionError = 5;
        var snapshotFailure = new RecoveryHarness(ActivationState.Unsafe);
        snapshotFailure.Process.SnapshotError = 6;
        var moduleFailure = new RecoveryHarness(ActivationState.Unsafe);
        moduleFailure.Process.ModuleError = 299;

        var open = await openFailure.Controller.DetachRecordedAsync(openFailure.Contract);
        var snapshot = await snapshotFailure.Controller.DetachRecordedAsync(
            snapshotFailure.Contract);
        var modules = await moduleFailure.Controller.DetachRecordedAsync(
            moduleFailure.Contract);

        Assert.Equal(ExplorerRecoveryFailureCode.OpenProcessFailed, open.Failure!.Code);
        Assert.Equal(5, open.Failure.NativeError);
        Assert.Equal(ExplorerRecoveryFailureCode.TargetSnapshotFailed, snapshot.Failure!.Code);
        Assert.Equal(6, snapshot.Failure.NativeError);
        Assert.Equal(ExplorerRecoveryFailureCode.ModuleEnumerationFailed, modules.Failure!.Code);
        Assert.Equal(299, modules.Failure.NativeError);
        Assert.Equal(0, openFailure.Api.OpenInjectionCount);
        Assert.Equal(0, snapshotFailure.Api.OpenInjectionCount);
        Assert.Equal(0, moduleFailure.Api.OpenInjectionCount);
        AssertReleased(openFailure);
        AssertReleased(snapshotFailure, processExpected: true);
        AssertReleased(moduleFailure, processExpected: true);
    }

    [Theory]
    [InlineData("wrong-size", "BridgeModuleImageMismatch")]
    [InlineData("duplicate", "BridgeModuleAmbiguous")]
    public async Task Initial_same_path_module_identity_mismatch_is_never_treated_as_absent(
        string shape,
        string expectedFailure)
    {
        var harness = new RecoveryHarness(ActivationState.Unsafe);
        harness.Process.Modules = shape == "wrong-size"
            ? [RecoveryHarness.BridgeModule(RecoveryHarness.BridgeSize + 1)]
            :
            [
                RecoveryHarness.BridgeModule(),
                RecoveryHarness.BridgeModule(
                    RecoveryHarness.BridgeSize,
                    baseAddress: 0x00000002_00000000),
            ];

        var result = await harness.Controller.DetachRecordedAsync(harness.Contract);

        Assert.Equal(ExplorerRecoveryOutcome.Unsafe, result.Outcome);
        Assert.Equal(
            Enum.Parse<ExplorerRecoveryFailureCode>(expectedFailure),
            result.Failure!.Code);
        Assert.Equal(0, harness.Journal.RecoveryCount);
        Assert.Empty(harness.Api.OpenedEventKinds);
        AssertReleased(harness, processExpected: true);
    }

    [Fact]
    public async Task Shutdown_is_signaled_before_a_missing_quiesced_event_is_reported()
    {
        var harness = new RecoveryHarness(ActivationState.Stable);
        harness.Process.Modules = [RecoveryHarness.BridgeModule()];
        harness.Api.MissingEvents.Add("Quiesced");

        var result = await harness.Controller.DetachRecordedAsync(harness.Contract);

        Assert.Equal(ExplorerRecoveryOutcome.Unsafe, result.Outcome);
        Assert.Equal(ExplorerRecoveryFailureCode.QuiescedOpenFailed, result.Failure!.Code);
        Assert.Equal(1, harness.Api.ShutdownSignalCount);
        Assert.True(
            harness.Trace.IndexOf("Event:Signal:Shutdown") <
            harness.Trace.IndexOf("Event:Open:Quiesced"));
        Assert.Equal(0, harness.Journal.RecoveryCount);
        AssertReleased(harness, processExpected: true);
    }

    [Fact]
    public async Task Successful_soft_detach_uses_bound_events_and_waits_for_path_absence()
    {
        var harness = new RecoveryHarness(ActivationState.Unsafe);
        harness.Process.EnqueueModules([RecoveryHarness.BridgeModule()]);
        harness.Process.Modules = [];

        var result = await harness.Controller.DetachRecordedAsync(harness.Contract);

        var record = harness.Journal.Record!;
        var expectedNames = BridgeEventNames.For(
            new ExplorerInstanceBinding(
                record.ExplorerProcessId,
                record.ExplorerCreationTimeFileTime100ns,
                record.ActivationId));
        Assert.Equal(ExplorerRecoveryOutcome.Clean, result.Outcome);
        Assert.Equal([expectedNames.Shutdown, expectedNames.Quiesced], harness.Api.OpenedEventNames);
        Assert.Equal(1, harness.Api.ShutdownSignalCount);
        Assert.Equal(1, harness.Api.QuiescedWaitCount);
        Assert.True(harness.Api.LastQuiescedTimeout > TimeSpan.Zero);
        Assert.True(harness.Api.LastQuiescedTimeout <= TimeSpan.FromSeconds(10));
        Assert.True(
            harness.Trace.IndexOf("Event:Signal:Shutdown") <
            harness.Trace.IndexOf("Event:Wait:Quiesced"));
        Assert.True(
            harness.Trace.IndexOf("Event:Wait:Quiesced") <
            harness.Trace.LastIndexOf("Process:Modules"));
        AssertRecoveryMatchesRecord(harness);
        Assert.Equal(0, harness.Api.OpenInjectionCount);
        AssertReleased(harness, processExpected: true);
    }

    [Fact]
    public async Task Missing_shutdown_rechecks_physical_absence_before_failing()
    {
        var harness = new RecoveryHarness(ActivationState.Unsafe);
        harness.Process.EnqueueModules([RecoveryHarness.BridgeModule()]);
        harness.Process.Modules = [];
        harness.Api.MissingEvents.Add("Shutdown");

        var result = await harness.Controller.DetachRecordedAsync(harness.Contract);

        Assert.Equal(ExplorerRecoveryOutcome.Clean, result.Outcome);
        Assert.Equal(0, harness.Api.ShutdownSignalCount);
        AssertRecoveryMatchesRecord(harness);
        AssertReleased(harness, processExpected: true);
    }

    [Fact]
    public async Task Shutdown_open_and_signal_failures_are_typed_without_opening_quiesced()
    {
        var openFailure = new RecoveryHarness(ActivationState.Unsafe);
        openFailure.Process.Modules = [RecoveryHarness.BridgeModule()];
        openFailure.Api.MissingEvents.Add("Shutdown");
        var signalFailure = new RecoveryHarness(ActivationState.Unsafe);
        signalFailure.Process.Modules = [RecoveryHarness.BridgeModule()];
        signalFailure.Api.ShutdownSignalError = 5;

        var open = await openFailure.Controller.DetachRecordedAsync(openFailure.Contract);
        var signal = await signalFailure.Controller.DetachRecordedAsync(signalFailure.Contract);

        Assert.Equal(ExplorerRecoveryFailureCode.ShutdownOpenFailed, open.Failure!.Code);
        Assert.Equal(ExplorerRecoveryFailureCode.ShutdownSignalFailed, signal.Failure!.Code);
        Assert.Equal(5, signal.Failure.NativeError);
        Assert.DoesNotContain("Quiesced", openFailure.Api.OpenedEventKinds);
        Assert.DoesNotContain("Quiesced", signalFailure.Api.OpenedEventKinds);
        Assert.Equal(0, openFailure.Journal.RecoveryCount);
        Assert.Equal(0, signalFailure.Journal.RecoveryCount);
        AssertReleased(openFailure, processExpected: true);
        AssertReleased(signalFailure, processExpected: true);
    }

    [Theory]
    [InlineData("TimedOut", "QuiescedTimeout", 0)]
    [InlineData("Failed", "QuiescedWaitFailed", 123)]
    [InlineData("Abandoned", "QuiescedWaitFailed", 735)]
    public async Task Quiesced_wait_failures_are_typed_after_shutdown_signal(
        string waitKind,
        string expectedFailure,
        int nativeError)
    {
        var harness = new RecoveryHarness(ActivationState.Unsafe);
        harness.Process.Modules = [RecoveryHarness.BridgeModule()];
        harness.Api.QuiescedWait = new NativeWaitResult(
            Enum.Parse<NativeWaitKind>(waitKind),
            nativeError);

        var result = await harness.Controller.DetachRecordedAsync(harness.Contract);

        Assert.Equal(
            Enum.Parse<ExplorerRecoveryFailureCode>(expectedFailure),
            result.Failure!.Code);
        Assert.Equal(1, harness.Api.ShutdownSignalCount);
        Assert.Equal(1, harness.Api.QuiescedWaitCount);
        Assert.Equal(0, harness.Journal.RecoveryCount);
        AssertReleased(harness, processExpected: true);
    }

    [Fact]
    public async Task Any_same_path_residual_after_quiescence_blocks_clean_even_with_wrong_size()
    {
        var harness = new RecoveryHarness(ActivationState.Unsafe);
        harness.Process.EnqueueModules([RecoveryHarness.BridgeModule()]);
        harness.Process.Modules =
            [RecoveryHarness.BridgeModule(RecoveryHarness.BridgeSize + 1)];

        var result = await harness.Controller.DetachRecordedAsync(harness.Contract);

        Assert.Equal(ExplorerRecoveryOutcome.Unsafe, result.Outcome);
        Assert.Equal(ExplorerRecoveryFailureCode.ModuleUnloadTimeout, result.Failure!.Code);
        Assert.True(harness.Api.DelayCount > 0);
        Assert.Equal(0, harness.Journal.RecoveryCount);
        AssertReleased(harness, processExpected: true);
    }

    [Fact]
    public async Task Process_exit_during_poll_is_clean_evidence_when_module_enumeration_fails()
    {
        var harness = new RecoveryHarness(ActivationState.Unsafe);
        harness.Process.EnqueueModules([RecoveryHarness.BridgeModule()]);
        harness.Process.EnqueueModuleFailure(299);
        harness.Process.WaitForExit = new NativeWaitResult(NativeWaitKind.Signaled, 0);

        var result = await harness.Controller.DetachRecordedAsync(harness.Contract);

        Assert.Equal(ExplorerRecoveryOutcome.Clean, result.Outcome);
        AssertRecoveryMatchesRecord(harness);
        AssertReleased(harness, processExpected: true);
    }

    [Fact]
    public async Task Physical_cleanup_with_a_failed_recovery_write_is_reported_separately()
    {
        var harness = new RecoveryHarness(ActivationState.Unsafe);
        harness.Process.Modules = [];
        harness.Journal.RecoveryOutcome = ActivationJournalOutcome.StorageFailure;

        var result = await harness.Controller.DetachRecordedAsync(harness.Contract);

        Assert.Equal(ExplorerRecoveryOutcome.CleanButJournalBlocked, result.Outcome);
        Assert.Equal(ExplorerRecoveryStage.JournalCommit, result.Failure!.Stage);
        Assert.Equal(ExplorerRecoveryFailureCode.JournalWriteFailed, result.Failure.Code);
        AssertRecoveryMatchesRecord(harness);
        AssertReleased(harness, processExpected: true);
    }

    [Fact]
    public async Task Quiescence_and_unload_verification_share_one_ten_second_deadline()
    {
        var clock = new ManualTimeProvider();
        var harness = new RecoveryHarness(ActivationState.Unsafe, clock);
        harness.Process.EnqueueModules([RecoveryHarness.BridgeModule()]);
        harness.Process.Modules = [];
        harness.Api.OnQuiescedWait = _ => clock.Advance(TimeSpan.FromSeconds(10));

        var result = await harness.Controller.DetachRecordedAsync(harness.Contract);

        Assert.Equal(ExplorerRecoveryOutcome.Unsafe, result.Outcome);
        Assert.Equal(ExplorerRecoveryFailureCode.ModuleUnloadTimeout, result.Failure!.Code);
        Assert.Equal(1, harness.Process.ModuleReadCount);
        Assert.Equal(0, harness.Journal.RecoveryCount);
        AssertReleased(harness, processExpected: true);
    }

    [Fact]
    public async Task Boundary_exceptions_are_typed_and_do_not_skip_later_resource_releases()
    {
        var readFailure = new RecoveryHarness(ActivationState.Unsafe);
        readFailure.Journal.ThrowOnRead = true;
        var recoveryFailure = new RecoveryHarness(ActivationState.Unsafe);
        recoveryFailure.Process.Modules = [];
        recoveryFailure.Journal.ThrowOnRecovery = true;
        recoveryFailure.Process.ThrowOnDispose = true;

        var read = await readFailure.Controller.DetachRecordedAsync(readFailure.Contract);
        var recovery = await recoveryFailure.Controller.DetachRecordedAsync(
            recoveryFailure.Contract);

        Assert.Equal(ExplorerRecoveryFailureCode.JournalUnavailable, read.Failure!.Code);
        Assert.Equal(ExplorerRecoveryOutcome.CleanButJournalBlocked, recovery.Outcome);
        Assert.Equal(ExplorerRecoveryFailureCode.JournalWriteFailed, recovery.Failure!.Code);
        AssertReleased(readFailure);
        Assert.False(recoveryFailure.Journal.SessionLeaseHeld);
        Assert.Equal(1, recoveryFailure.Journal.SessionLeaseDisposeCount);
        Assert.Equal(1, recoveryFailure.Process.DisposeCount);
    }

    [Fact]
    public async Task Invalid_bridge_contract_is_typed_before_process_access()
    {
        var harness = new RecoveryHarness(ActivationState.Unsafe);
        var invalid = harness.Contract with { SizeOfImage = 0 };

        var result = await harness.Controller.DetachRecordedAsync(invalid);

        Assert.Equal(ExplorerRecoveryOutcome.Unsafe, result.Outcome);
        Assert.Equal(ExplorerRecoveryFailureCode.InvalidBridgeContract, result.Failure!.Code);
        Assert.Equal(ExplorerRecoveryStage.Contract, result.Failure.Stage);
        Assert.Equal(0, harness.Api.OpenCollectionCount);
        AssertReleased(harness);
    }

    private static void AssertRecoveryMatchesRecord(RecoveryHarness harness)
    {
        var call = Assert.Single(harness.Journal.RecoveryCalls);
        Assert.Equal(harness.Journal.Record!.ActivationId, call.ActivationId);
        Assert.Equal(harness.Journal.Record.ExplorerProcessId, call.ProcessId);
        Assert.Equal(
            harness.Journal.Record.ExplorerCreationTimeFileTime100ns,
            call.CreationTime);
    }

    private static void AssertReleased(
        RecoveryHarness harness,
        bool processExpected = false)
    {
        Assert.False(harness.Journal.SessionLeaseHeld);
        Assert.Equal(1, harness.Journal.SessionLeaseDisposeCount);
        Assert.Equal(processExpected ? 1 : 0, harness.Process.DisposeCount);
    }

    private sealed class RecoveryHarness
    {
        internal const int ProcessId = 4321;
        internal const ulong CreationTime = 0x0123456789abcdef;
        internal const uint SessionId = 7;
        internal const string UserSid = "S-1-5-21-test";
        internal const string BridgePath = @"C:\ProgramData\CodexQuotaTaskbar\bridge.dll";
        internal const uint BridgeSize = 0x2000;
        private static readonly Guid ActivationId =
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

        internal RecoveryHarness(
            ActivationState? state,
            TimeProvider? timeProvider = null)
        {
            Trace = [];
            Journal = new FakeRecoveryJournal(state, Trace);
            Process = new FakeReadLease(Trace);
            Api = new FakeProcessApi(Process, Trace);
            Controller = new ExplorerRecoveryController(Api, Journal, timeProvider);
            Contract = new BridgeImageContract(
                BridgePath,
                new string('c', 64),
                1,
                BridgeSize,
                0x1100,
                0x1200);
        }

        internal List<string> Trace { get; }

        internal FakeRecoveryJournal Journal { get; }

        internal FakeReadLease Process { get; }

        internal FakeProcessApi Api { get; }

        internal ExplorerRecoveryController Controller { get; }

        internal BridgeImageContract Contract { get; }

        internal static ActivationJournalRecord CreateRecord(ActivationState state) =>
            new(
                "1.2.3",
                26200,
                new string('a', 64),
                new string('b', 64),
                ProcessId,
                CreationTime,
                ActivationId,
                DateTimeOffset.Parse("2026-08-01T10:30:00Z"),
                state);

        internal static ExplorerProcessSnapshot CreateSnapshot(
            int processId = ProcessId,
            ulong creationTime = CreationTime,
            uint sessionId = SessionId,
            string userSid = UserSid,
            ExplorerProcessArchitecture architecture = ExplorerProcessArchitecture.X64,
            bool hasExited = false) =>
            new(
                processId,
                creationTime,
                sessionId,
                userSid,
                architecture,
                @"C:\Windows\explorer.exe",
                hasExited);

        internal static RemoteModule BridgeModule(
            uint size = BridgeSize,
            ulong baseAddress = 0x00000001_00000000) =>
            new(BridgePath, baseAddress, size);

        internal sealed class FakeRecoveryJournal : IActivationRecoveryJournalStore
        {
            private readonly List<string> trace;

            internal FakeRecoveryJournal(ActivationState? state, List<string> trace)
            {
                this.trace = trace;
                Record = state is null ? null : CreateRecord(state.Value);
            }

            internal ActivationSessionLeaseOutcome LeaseOutcome { get; set; } =
                ActivationSessionLeaseOutcome.Acquired;

            internal ActivationJournalRecord? Record { get; private set; }

            internal ActivationJournalOutcome? ReadOutcome { get; set; }

            internal ActivationJournalOutcome RecoveryOutcome { get; set; } =
                ActivationJournalOutcome.TransitionPersisted;

            internal bool ThrowOnRead { get; set; }

            internal bool ThrowOnRecovery { get; set; }

            internal bool SessionLeaseHeld { get; private set; }

            internal int SessionLeaseDisposeCount { get; private set; }

            internal int RecoveryCount => RecoveryCalls.Count;

            internal List<RecoveryCall> RecoveryCalls { get; } = [];

            public ActivationSessionLeaseResult TryAcquireSessionLease()
            {
                trace.Add("Journal:Acquire");
                if (LeaseOutcome != ActivationSessionLeaseOutcome.Acquired)
                {
                    return new ActivationSessionLeaseResult(LeaseOutcome, null);
                }

                SessionLeaseHeld = true;
                return new ActivationSessionLeaseResult(
                    LeaseOutcome,
                    new FakeSessionLease(this));
            }

            public ActivationJournalResult ReadStatus()
            {
                trace.Add("Journal:Read");
                if (ThrowOnRead)
                {
                    throw new IOException("fake journal read failure");
                }

                if (ReadOutcome is { } outcome)
                {
                    return new ActivationJournalResult(outcome, Record);
                }

                return Record is null or { State: ActivationState.Clean }
                    ? new ActivationJournalResult(ActivationJournalOutcome.Allowed, Record)
                    : new ActivationJournalResult(ActivationJournalOutcome.Blocked, Record);
            }

            public ActivationJournalResult TryRecoverToClean(
                Guid activationId,
                int explorerProcessId,
                ulong explorerCreationTimeFileTime100ns)
            {
                trace.Add("Journal:Recover");
                RecoveryCalls.Add(
                    new RecoveryCall(
                        activationId,
                        explorerProcessId,
                        explorerCreationTimeFileTime100ns));
                if (ThrowOnRecovery)
                {
                    throw new IOException("fake journal recovery failure");
                }

                if (RecoveryOutcome == ActivationJournalOutcome.TransitionPersisted)
                {
                    Record = Record! with { State = ActivationState.Clean };
                }

                return new ActivationJournalResult(
                    RecoveryOutcome,
                    Record);
            }

            private sealed class FakeSessionLease(FakeRecoveryJournal owner) :
                IActivationSessionLease
            {
                private FakeRecoveryJournal? owner = owner;

                public void Dispose()
                {
                    var current = Interlocked.Exchange(ref owner, null);
                    if (current is null)
                    {
                        return;
                    }

                    current.SessionLeaseHeld = false;
                    current.SessionLeaseDisposeCount++;
                    current.trace.Add("Journal:Release");
                }
            }
        }

        internal sealed class FakeReadLease(List<string> trace) : IExplorerReadLease
        {
            private readonly Queue<NativeResult<IReadOnlyList<RemoteModule>>> moduleResults =
                new();

            internal int ProcessIdValue { get; set; } = RecoveryHarness.ProcessId;

            public int ProcessId => ProcessIdValue;

            internal ExplorerProcessSnapshot SnapshotValue { get; set; } =
                CreateSnapshot();

            internal IReadOnlyList<RemoteModule> Modules { get; set; } = [];

            internal int SnapshotError { get; set; }

            internal int ModuleError { get; set; }

            internal NativeWaitResult WaitForExit { get; set; } =
                new(NativeWaitKind.TimedOut, 0);

            internal int ModuleReadCount { get; private set; }

            internal int DisposeCount { get; private set; }

            internal bool ThrowOnDispose { get; set; }

            public NativeResult<ExplorerProcessSnapshot> Snapshot()
            {
                trace.Add("Process:Snapshot");
                return SnapshotError == 0
                    ? NativeResult<ExplorerProcessSnapshot>.Success(SnapshotValue)
                    : NativeResult<ExplorerProcessSnapshot>.Failure(SnapshotError);
            }

            public NativeResult<IReadOnlyList<RemoteModule>> EnumerateModules()
            {
                ModuleReadCount++;
                trace.Add("Process:Modules");
                if (moduleResults.TryDequeue(out var result))
                {
                    return result;
                }

                return ModuleError == 0
                    ? NativeResult<IReadOnlyList<RemoteModule>>.Success(Modules)
                    : NativeResult<IReadOnlyList<RemoteModule>>.Failure(ModuleError);
            }

            public NativeWaitResult WaitForProcessExit(TimeSpan timeout) =>
                WaitForExit;

            internal void EnqueueModules(IReadOnlyList<RemoteModule> modules) =>
                moduleResults.Enqueue(
                    NativeResult<IReadOnlyList<RemoteModule>>.Success(modules));

            internal void EnqueueModuleFailure(int errorCode) =>
                moduleResults.Enqueue(
                    NativeResult<IReadOnlyList<RemoteModule>>.Failure(errorCode));

            public void Dispose()
            {
                DisposeCount++;
                trace.Add("Process:Dispose");
                if (ThrowOnDispose)
                {
                    throw new IOException("fake process dispose failure");
                }
            }
        }

        internal sealed class FakeProcessApi(
            FakeReadLease process,
            List<string> trace) : IExplorerProcessApi
        {
            public uint CurrentSessionId => SessionId;

            public string CurrentUserSid => UserSid;

            internal int OpenCollectionCount { get; private set; }

            internal int OpenCollectionError { get; set; }

            internal int OpenInjectionCount { get; private set; }

            internal List<string> OpenedEventKinds { get; } = [];

            internal List<string> OpenedEventNames { get; } = [];

            internal HashSet<string> MissingEvents { get; } =
                new(StringComparer.Ordinal);

            internal int ShutdownSignalError { get; set; }

            internal int ShutdownSignalCount { get; private set; }

            internal NativeWaitResult QuiescedWait { get; set; } =
                new(NativeWaitKind.Signaled, 0);

            internal int QuiescedWaitCount { get; private set; }

            internal TimeSpan LastQuiescedTimeout { get; private set; }

            internal int DelayCount { get; private set; }

            internal Action<TimeSpan>? OnQuiescedWait { get; set; }

            public NativeResult<IExplorerReadLease> OpenForCollection(int processId)
            {
                OpenCollectionCount++;
                trace.Add("Process:OpenCollection");
                return OpenCollectionError == 0
                    ? NativeResult<IExplorerReadLease>.Success(process)
                    : NativeResult<IExplorerReadLease>.Failure(OpenCollectionError);
            }

            public NativeResult<IExplorerMutationLease> OpenForInjection(int processId)
            {
                OpenInjectionCount++;
                throw new InvalidOperationException("Recovery must never request mutation access.");
            }

            public NativeResult<LocalSystemExport> ResolveLocalSystemExport(
                string moduleName,
                string exportName) =>
                throw new InvalidOperationException("Recovery must not resolve injection exports.");

            public NativeResult<INamedEvent> TryOpenEvent(
                string eventName,
                NativeEventAccess access)
            {
                var kind = eventName[(eventName.LastIndexOf('.') + 1)..];
                OpenedEventKinds.Add(kind);
                OpenedEventNames.Add(eventName);
                trace.Add($"Event:Open:{kind}");
                return MissingEvents.Contains(kind)
                    ? NativeResult<INamedEvent>.Failure(2)
                    : NativeResult<INamedEvent>.Success(
                        new FakeEvent(this, kind, access, trace));
            }

            public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
            {
                DelayCount++;
                trace.Add("Process:Delay");
                return Task.CompletedTask;
            }

            private sealed class FakeEvent(
                FakeProcessApi owner,
                string kind,
                NativeEventAccess access,
                List<string> trace) : INamedEvent
            {
                public NativeWaitResult Wait(TimeSpan timeout)
                {
                    trace.Add($"Event:Wait:{kind}");
                    if (kind != "Quiesced" || access != NativeEventAccess.Wait)
                    {
                        return new NativeWaitResult(NativeWaitKind.Failed, 5);
                    }

                    owner.QuiescedWaitCount++;
                    owner.LastQuiescedTimeout = timeout;
                    owner.OnQuiescedWait?.Invoke(timeout);
                    return owner.QuiescedWait;
                }

                public NativeResult Signal()
                {
                    trace.Add($"Event:Signal:{kind}");
                    if (kind != "Shutdown" || access != NativeEventAccess.Signal)
                    {
                        return NativeResult.Failure(5);
                    }

                    if (owner.ShutdownSignalError != 0)
                    {
                        return NativeResult.Failure(owner.ShutdownSignalError);
                    }

                    owner.ShutdownSignalCount++;
                    return NativeResult.Success();
                }

                public void Dispose() => trace.Add($"Event:Dispose:{kind}");
            }
        }

        internal sealed record RecoveryCall(
            Guid ActivationId,
            int ProcessId,
            ulong CreationTime);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => timestamp;

        internal void Advance(TimeSpan elapsed) =>
            timestamp = checked(timestamp + elapsed.Ticks);
    }
}
