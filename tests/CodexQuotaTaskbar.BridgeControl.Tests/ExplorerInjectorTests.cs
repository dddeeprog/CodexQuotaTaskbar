using System.Buffers.Binary;
using System.Text;
using CodexQuotaTaskbar.BridgeControl.Injection;
using CodexQuotaTaskbar.BridgeControl.Safety;

namespace CodexQuotaTaskbar.BridgeControl.Tests;

public sealed class ExplorerInjectorTests
{
    [Fact]
    public async Task Successful_injection_uses_one_lease_full_x64_bases_and_durable_ordering()
    {
        var harness = new InjectorHarness();

        var result = await harness.Injector.InjectAsync(harness.Permit);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Session);
        Assert.True(harness.Journal.SessionLeaseHeld);
        Assert.Equal(1, harness.Api.OpenInjectionCount);
        Assert.Equal(0, harness.Api.OpenCollectionCount);
        Assert.Equal(ActivationState.Stable, harness.Journal.State);
        Assert.True(
            harness.Trace.IndexOf("Journal:Pending") <
            harness.Trace.FindIndex(entry => entry.StartsWith("Thread:", StringComparison.Ordinal)));

        Assert.Collection(
            harness.Lease.ThreadStarts,
            load => Assert.Equal(
                InjectorHarness.KernelBase + InjectorHarness.LoadLibraryRva,
                load.StartAddress),
            start => Assert.Equal(
                InjectorHarness.BridgeBase + InjectorHarness.StartProbeRva,
                start.StartAddress));
        var request = Assert.Single(
            harness.Lease.Writes,
            write => write.Bytes.Length == BridgeStartRequest.Size);
        Assert.Equal(56U, BinaryPrimitives.ReadUInt32LittleEndian(request.Bytes));
        Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(request.Bytes.AsSpan(4)));
        Assert.Equal(
            InjectorHarness.BridgeBase + InjectorHarness.StartProbeRva,
            harness.Lease.ThreadStarts[1].StartAddress);
        Assert.True(
            harness.Trace.FindIndex(entry => entry == "ThreadWait:StartProbe") <
            harness.Trace.FindIndex(entry => entry == "EventSignal:StartReleased"));

        var detached = await harness.Injector.DetachAsync(result.Session!);
        var repeated = await harness.Injector.DetachAsync(result.Session!);

        Assert.Equal(ExplorerDetachOutcome.Clean, detached.Outcome);
        Assert.Equal(ExplorerDetachOutcome.AlreadyClean, repeated.Outcome);
        Assert.Equal(1, harness.Api.ShutdownSignalCount);
        Assert.Equal(ActivationState.Clean, harness.Journal.State);
        Assert.True(harness.Lease.Disposed);
        Assert.True(harness.Artifact.Disposed);
        Assert.False(harness.Journal.SessionLeaseHeld);
        Assert.Equal(1, harness.Journal.SessionLeaseDisposeCount);
    }

    [Fact]
    public async Task Active_session_lease_blocks_a_concurrent_explicit_retry()
    {
        var harness = new InjectorHarness();
        var first = await harness.Injector.InjectAsync(harness.Permit);

        var second = await harness.Injector.InjectAsync(harness.IssuePermit(explicitRetry: true));

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Equal(InjectorFailureCode.ActivationSessionBusy, second.Failure!.Code);
        Assert.Equal(InjectorSafetyDisposition.RejectedNoMutation, second.Failure.Disposition);
        Assert.Equal(1, harness.Store.MaterializeCount);
        Assert.Equal(1, harness.Journal.BeginCount);

        _ = await harness.Injector.DetachAsync(first.Session!);
    }

    [Fact]
    public async Task Existing_unclean_journal_blocks_before_artifact_or_process_mutation()
    {
        var harness = new InjectorHarness();
        harness.Journal.State = ActivationState.Unsafe;

        var result = await harness.Injector.InjectAsync(harness.Permit);

        Assert.False(result.Succeeded);
        Assert.Equal(InjectorFailureCode.PreviousActivationUnclean, result.Failure!.Code);
        Assert.Equal(InjectorSafetyDisposition.RejectedNoMutation, result.Failure.Disposition);
        Assert.Equal(0, harness.Store.MaterializeCount);
        Assert.Equal(0, harness.Api.OpenInjectionCount);
    }

    [Fact]
    public async Task Journal_boundary_exceptions_are_typed_and_release_any_acquired_session_lease()
    {
        var acquireFailure = new InjectorHarness();
        acquireFailure.Journal.ThrowOnAcquireSessionLease = true;
        var readFailure = new InjectorHarness();
        readFailure.Journal.ThrowOnReadStatus = true;

        var acquireResult = await acquireFailure.Injector.InjectAsync(acquireFailure.Permit);
        var readResult = await readFailure.Injector.InjectAsync(readFailure.Permit);

        Assert.Equal(InjectorFailureCode.JournalUnavailable, acquireResult.Failure!.Code);
        Assert.Equal(InjectorFailureCode.JournalUnavailable, readResult.Failure!.Code);
        Assert.Equal(0, acquireFailure.Store.MaterializeCount);
        Assert.Equal(0, readFailure.Store.MaterializeCount);
        Assert.False(readFailure.Journal.SessionLeaseHeld);
        Assert.Equal(1, readFailure.Journal.SessionLeaseDisposeCount);
    }

    [Fact]
    public async Task Explicit_retry_can_replace_an_unclean_record_only_after_module_absence_is_verified()
    {
        var harness = new InjectorHarness();
        harness.Journal.State = ActivationState.Unsafe;

        var result = await harness.Injector.InjectAsync(harness.IssuePermit(explicitRetry: true));

        Assert.True(result.Succeeded);
        Assert.Equal(1, harness.Journal.BeginCount);
        Assert.Equal(ActivationState.Stable, harness.Journal.State);
        _ = await harness.Injector.DetachAsync(result.Session!);
    }

    [Fact]
    public async Task Explicit_retry_preserves_active_unclean_record_when_bridge_is_still_mapped()
    {
        var harness = new InjectorHarness();
        harness.Journal.State = ActivationState.Unsafe;
        harness.Lease.PreexistingBridgeImageSize = 0x2000;

        var result = await harness.Injector.InjectAsync(harness.IssuePermit(explicitRetry: true));

        Assert.False(result.Succeeded);
        Assert.Equal(InjectorFailureCode.PreviousActivationUnclean, result.Failure!.Code);
        Assert.Equal(0, harness.Journal.BeginCount);
        Assert.Equal(ActivationState.Unsafe, harness.Journal.State);
        Assert.Empty(harness.Lease.ThreadStarts);
    }

    [Theory]
    [InlineData(87, true)]
    [InlineData(5, false)]
    public async Task Explicit_retry_treats_only_missing_old_pid_as_exited(
        int openError,
        bool shouldRecover)
    {
        const int oldProcessId = 1234;
        var harness = new InjectorHarness();
        harness.Journal.State = ActivationState.Unsafe;
        harness.Journal.RecordedExplorerProcessId = oldProcessId;
        harness.Api.OpenCollectionError = openError;

        var result = await harness.Injector.InjectAsync(
            harness.IssuePermit(explicitRetry: true));

        Assert.Equal(oldProcessId, harness.Api.LastCollectionProcessId);
        Assert.Equal(1, harness.Api.OpenCollectionCount);
        if (shouldRecover)
        {
            Assert.True(result.Succeeded);
            Assert.Equal(1, harness.Journal.BeginCount);
            Assert.Equal(ActivationState.Stable, harness.Journal.State);
            _ = await harness.Injector.DetachAsync(result.Session!);
        }
        else
        {
            Assert.False(result.Succeeded);
            Assert.Equal(InjectorFailureCode.PreviousActivationUnclean, result.Failure!.Code);
            Assert.Equal(0, harness.Journal.BeginCount);
            Assert.Equal(ActivationState.Unsafe, harness.Journal.State);
            Assert.Empty(harness.Lease.ThreadStarts);
        }
    }

    [Fact]
    public async Task Open_process_and_identity_failures_are_typed_before_pending()
    {
        var openFailure = new InjectorHarness();
        openFailure.Api.OpenInjectionError = 5;
        var wrongIdentity = new InjectorHarness();
        wrongIdentity.Lease.SnapshotValue = new ExplorerProcessSnapshot(
            4321,
            0x0123456789abcdef,
            99,
            "S-1-5-21-test",
            ExplorerProcessArchitecture.X64,
            @"C:\Windows\explorer.exe",
            hasExited: false);

        var openResult = await openFailure.Injector.InjectAsync(openFailure.Permit);
        var identityResult = await wrongIdentity.Injector.InjectAsync(wrongIdentity.Permit);

        Assert.Equal(InjectorFailureCode.OpenProcessFailed, openResult.Failure!.Code);
        Assert.Equal(5, openResult.Failure.NativeError);
        Assert.Equal(InjectorFailureCode.TargetIdentityMismatch, identityResult.Failure!.Code);
        Assert.Equal(0, openFailure.Journal.BeginCount);
        Assert.Equal(0, wrongIdentity.Journal.BeginCount);
    }

    [Fact]
    public async Task Abi_mismatch_is_rejected_without_opening_the_target()
    {
        var harness = new InjectorHarness();
        harness.Inspector.Failure = new BridgeImageException(
            BridgeImageFailureCode.AbiMismatch,
            "fake ABI mismatch");

        var result = await harness.Injector.InjectAsync(harness.Permit);

        Assert.Equal(InjectorFailureCode.BridgeContractInvalid, result.Failure!.Code);
        Assert.Equal(InjectorStage.Artifact, result.Failure.Stage);
        Assert.Equal(0, harness.Api.OpenInjectionCount);
        Assert.Equal(0, harness.Journal.BeginCount);
    }

    [Fact]
    public async Task Pending_precedes_remote_setup_and_setup_failure_commits_clean()
    {
        var allocationFailure = new InjectorHarness();
        allocationFailure.Lease.AllocationError = 8;
        var writeFailure = new InjectorHarness();
        writeFailure.Lease.WriteError = 299;
        var threadFailure = new InjectorHarness();
        threadFailure.Lease.ThreadStartError = 5;

        var allocationResult = await allocationFailure.Injector.InjectAsync(allocationFailure.Permit);
        var writeResult = await writeFailure.Injector.InjectAsync(writeFailure.Permit);
        var threadResult = await threadFailure.Injector.InjectAsync(threadFailure.Permit);

        Assert.Equal(InjectorFailureCode.RemoteAllocationFailed, allocationResult.Failure!.Code);
        Assert.Equal(InjectorFailureCode.RemoteWriteFailed, writeResult.Failure!.Code);
        Assert.Equal(InjectorFailureCode.RemoteThreadCreateFailed, threadResult.Failure!.Code);
        Assert.Equal(ActivationState.Clean, allocationFailure.Journal.State);
        Assert.Equal(ActivationState.Clean, writeFailure.Journal.State);
        Assert.Equal(ActivationState.Clean, threadFailure.Journal.State);
        Assert.All(
            new[] { allocationFailure, writeFailure, threadFailure },
            item => Assert.True(
                item.Trace.IndexOf("Journal:Pending") <
                item.Trace.FindIndex(entry => entry.StartsWith("Allocate", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task Load_library_timeout_abandons_parameter_and_records_unsafe()
    {
        var harness = new InjectorHarness();
        harness.Lease.ThreadWaits.Clear();
        harness.Lease.ThreadWaits.Enqueue(
            new NativeWaitResult(NativeWaitKind.TimedOut, 0));

        var result = await harness.Injector.InjectAsync(harness.Permit);

        Assert.Equal(InjectorFailureCode.LoadLibraryTimeout, result.Failure!.Code);
        Assert.Equal(InjectorSafetyDisposition.UnsafeRetained, result.Failure.Disposition);
        Assert.Equal(ActivationState.Unsafe, harness.Journal.State);
        var memory = Assert.Single(harness.Lease.Memories);
        Assert.True(memory.Abandoned);
        Assert.False(memory.Released);
        Assert.Equal(0, harness.Api.StartReleasedSignalCount);
    }

    [Fact]
    public async Task Start_probe_non_s_ok_is_unsafe_and_never_attempts_remote_unload()
    {
        var harness = new InjectorHarness();
        harness.Lease.ThreadWaits.Clear();
        harness.Lease.ThreadWaits.Enqueue(
            new NativeWaitResult(NativeWaitKind.Signaled, 0, 0x1234));
        harness.Lease.ThreadWaits.Enqueue(
            new NativeWaitResult(NativeWaitKind.Signaled, 0, 1));

        var result = await harness.Injector.InjectAsync(harness.Permit);

        Assert.Equal(InjectorFailureCode.StartProbeRejected, result.Failure!.Code);
        Assert.Equal(1, result.Failure.RemoteHResult);
        Assert.Equal(ActivationState.Unsafe, harness.Journal.State);
        Assert.DoesNotContain(
            harness.Trace,
            entry => entry.Contains("FreeLibrary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Ready_timeout_requests_emergency_detach_but_remains_unsafe()
    {
        var harness = new InjectorHarness();
        harness.Api.ReadyWait = new NativeWaitResult(NativeWaitKind.TimedOut, 0);

        var result = await harness.Injector.InjectAsync(harness.Permit);

        Assert.Equal(InjectorFailureCode.ReadyTimeout, result.Failure!.Code);
        Assert.Equal(InjectorSafetyDisposition.UnsafeRetained, result.Failure.Disposition);
        Assert.Equal(1, harness.Api.ShutdownSignalCount);
        Assert.Equal(1, harness.Api.QuiescedWaitCount);
        Assert.Equal(ActivationState.Unsafe, harness.Journal.State);
        Assert.True(harness.Artifact.Disposed);
        Assert.True(harness.Lease.Disposed);
    }

    [Fact]
    public async Task Ready_timeout_records_the_highest_signaled_lifecycle_phase_before_emergency_detach()
    {
        var harness = new InjectorHarness();
        harness.Api.ReadyWait = new NativeWaitResult(NativeWaitKind.TimedOut, 0);
        harness.Api.SignaledLifecyclePhases.UnionWith(
            ["ControlEvents", "InitializeTaskbarThreads", "AdviseWatcher"]);

        var result = await harness.Injector.InjectAsync(harness.Permit);

        Assert.Equal(InjectorFailureCode.ReadyTimeout, result.Failure!.Code);
        Assert.Equal(BridgeLifecyclePhase.AdviseWatcher, result.Failure.ObservedPhase);
        Assert.Equal(2, harness.Api.LifecyclePhaseWaitTimeouts.Count);
        Assert.All(
            harness.Api.LifecyclePhaseWaitTimeouts,
            timeout => Assert.Equal(TimeSpan.Zero, timeout));
        Assert.True(
            harness.Trace.IndexOf("EventWait:Phase:AdviseWatcher") <
            harness.Trace.IndexOf("EventSignal:Shutdown"));
        Assert.Equal(InjectorSafetyDisposition.UnsafeRetained, result.Failure.Disposition);
        Assert.Equal(ActivationState.Unsafe, harness.Journal.State);
    }

    [Fact]
    public async Task Phase_captured_before_ready_timeout_survives_native_phase_handle_cleanup()
    {
        var harness = new InjectorHarness();
        harness.Api.ReadyWait = new NativeWaitResult(NativeWaitKind.TimedOut, 0);
        harness.Api.SignaledLifecyclePhases.UnionWith(
            ["ControlEvents", "InitializeDiagnostics", "AdviseWatcher"]);
        harness.Api.PhaseEventsDisappearWhenReadyWaitBegins = true;

        var result = await harness.Injector.InjectAsync(harness.Permit);

        Assert.Equal(InjectorFailureCode.ReadyTimeout, result.Failure!.Code);
        Assert.All(
            Enum.GetValues<BridgeLifecyclePhase>(),
            phase => Assert.True(
                harness.Trace.IndexOf($"EventOpen:Phase:{phase}") <
                harness.Trace.IndexOf("EventWait:Ready")));
        Assert.Equal(BridgeLifecyclePhase.AdviseWatcher, result.Failure.ObservedPhase);
        Assert.Equal(InjectorSafetyDisposition.UnsafeRetained, result.Failure.Disposition);
        Assert.Equal(ActivationState.Unsafe, harness.Journal.State);
    }

    [Fact]
    public async Task Unexpected_ready_wait_exception_requests_emergency_detach()
    {
        var harness = new InjectorHarness();
        harness.Api.ThrowOnReadyWait = true;

        var result = await harness.Injector.InjectAsync(harness.Permit);

        Assert.False(result.Succeeded);
        Assert.Equal(InjectorFailureCode.UnexpectedBoundaryFailure, result.Failure!.Code);
        Assert.Equal(1, harness.Api.ShutdownSignalCount);
        Assert.Equal(1, harness.Api.QuiescedWaitCount);
        Assert.Equal(ActivationState.Unsafe, harness.Journal.State);
        Assert.True(harness.Lease.Disposed);
        Assert.True(harness.Artifact.Disposed);
        Assert.False(harness.Journal.SessionLeaseHeld);
    }

    [Fact]
    public async Task Cancellation_before_first_remote_thread_is_typed_and_clean()
    {
        var harness = new InjectorHarness();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await harness.Injector.InjectAsync(harness.Permit, cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(InjectorFailureCode.OperationCancelled, result.Failure!.Code);
        Assert.Equal(InjectorSafetyDisposition.FailedClean, result.Failure.Disposition);
        Assert.Equal(ActivationState.Clean, harness.Journal.State);
        Assert.Empty(harness.Lease.ThreadStarts);
    }

    [Fact]
    public async Task Cleanup_disposes_remaining_leases_when_one_dispose_throws()
    {
        var harness = new InjectorHarness();
        harness.Lease.AllocationError = 8;
        harness.Lease.ThrowOnDispose = true;

        var result = await harness.Injector.InjectAsync(harness.Permit);

        Assert.Equal(InjectorFailureCode.RemoteAllocationFailed, result.Failure!.Code);
        Assert.True(harness.Lease.Disposed);
        Assert.True(harness.Artifact.Disposed);
        Assert.False(harness.Journal.SessionLeaseHeld);
        Assert.Equal(1, harness.Journal.SessionLeaseDisposeCount);
    }

    [Fact]
    public async Task Missing_quiesced_event_still_signals_shutdown_during_emergency_detach()
    {
        var harness = new InjectorHarness();
        harness.Api.MissingEvents.Add("Quiesced");

        var result = await harness.Injector.InjectAsync(harness.Permit);

        Assert.False(result.Succeeded);
        Assert.Equal(InjectorFailureCode.ControlEventOpenFailed, result.Failure!.Code);
        Assert.Equal(1, harness.Api.ShutdownSignalCount);
        Assert.Equal(0, harness.Api.QuiescedWaitCount);
        Assert.Equal(ActivationState.Unsafe, harness.Journal.State);
    }

    [Fact]
    public async Task Same_bridge_path_with_wrong_image_size_is_rejected_before_pending()
    {
        var harness = new InjectorHarness();
        harness.Lease.PreexistingBridgeImageSize = 0x3000;

        var result = await harness.Injector.InjectAsync(harness.Permit);

        Assert.False(result.Succeeded);
        Assert.Equal(InjectorFailureCode.BridgeModuleImageMismatch, result.Failure!.Code);
        Assert.Equal(InjectorSafetyDisposition.RejectedNoMutation, result.Failure.Disposition);
        Assert.Equal(0, harness.Journal.BeginCount);
        Assert.Empty(harness.Lease.ThreadStarts);
    }

    [Fact]
    public async Task Nonzero_load_result_without_enumerated_bridge_is_unsafe()
    {
        var harness = new InjectorHarness();
        harness.Lease.LoadedBridgeCount = 0;

        var result = await harness.Injector.InjectAsync(harness.Permit);

        Assert.False(result.Succeeded);
        Assert.Equal(InjectorFailureCode.BridgeModuleMissing, result.Failure!.Code);
        Assert.Equal(InjectorSafetyDisposition.UnsafeRetained, result.Failure.Disposition);
        Assert.Equal(ActivationState.Unsafe, harness.Journal.State);
    }

    [Fact]
    public async Task Duplicate_bridge_paths_after_load_are_unsafe()
    {
        var harness = new InjectorHarness();
        harness.Lease.LoadedBridgeCount = 2;

        var result = await harness.Injector.InjectAsync(harness.Permit);

        Assert.False(result.Succeeded);
        Assert.Equal(InjectorFailureCode.BridgeModuleAmbiguous, result.Failure!.Code);
        Assert.Equal(InjectorSafetyDisposition.UnsafeRetained, result.Failure.Disposition);
        Assert.Equal(ActivationState.Unsafe, harness.Journal.State);
    }

    [Fact]
    public async Task Bridge_path_with_wrong_size_after_load_is_unsafe()
    {
        var harness = new InjectorHarness();
        harness.Lease.LoadedBridgeImageSize = 0x3000;

        var result = await harness.Injector.InjectAsync(harness.Permit);

        Assert.False(result.Succeeded);
        Assert.Equal(InjectorFailureCode.BridgeModuleImageMismatch, result.Failure!.Code);
        Assert.Equal(InjectorSafetyDisposition.UnsafeRetained, result.Failure.Disposition);
        Assert.Equal(ActivationState.Unsafe, harness.Journal.State);
    }

    [Fact]
    public async Task Final_validation_rejects_expected_bridge_plus_same_path_wrong_size_module()
    {
        var harness = new InjectorHarness();
        harness.Lease.AddWrongSizeBridgeDuringFinalValidation = true;

        var result = await harness.Injector.InjectAsync(harness.Permit);

        Assert.False(result.Succeeded);
        Assert.Equal(InjectorFailureCode.FinalValidationFailed, result.Failure!.Code);
        Assert.Equal(InjectorSafetyDisposition.UnsafeRetained, result.Failure.Disposition);
        Assert.Equal(ActivationState.Unsafe, harness.Journal.State);
    }

    [Fact]
    public async Task Quiesced_without_module_absence_is_unsafe_and_never_clean()
    {
        var harness = new InjectorHarness();
        harness.Lease.KeepBridgeMappedDuringDetach = true;
        var injected = await harness.Injector.InjectAsync(harness.Permit);

        var detached = await harness.Injector.DetachAsync(injected.Session!);

        Assert.Equal(ExplorerDetachOutcome.Unsafe, detached.Outcome);
        Assert.Equal(ActivationState.Unsafe, harness.Journal.State);
        Assert.NotEqual(ActivationState.Clean, harness.Journal.State);
    }

    [Fact]
    public async Task Detach_rejects_same_path_wrong_size_residual_after_expected_bridge_unloads()
    {
        var harness = new InjectorHarness();
        var injected = await harness.Injector.InjectAsync(harness.Permit);
        harness.Lease.KeepWrongSizeBridgeMappedDuringDetach = true;

        var detached = await harness.Injector.DetachAsync(injected.Session!);

        Assert.Equal(ExplorerDetachOutcome.Unsafe, detached.Outcome);
        Assert.Equal(InjectorFailureCode.ModuleUnloadTimeout, detached.Failure!.Code);
        Assert.Equal(ActivationState.Unsafe, harness.Journal.State);
    }

    [Fact]
    public async Task Latched_runtime_unsafe_state_survives_physically_clean_detach()
    {
        var harness = new InjectorHarness();
        var injected = await harness.Injector.InjectAsync(harness.Permit);

        var latched = harness.Injector.MarkUnsafe(injected.Session!);
        var detached = await harness.Injector.DetachAsync(injected.Session!);

        Assert.True(latched);
        Assert.Equal(ExplorerDetachOutcome.CleanButJournalBlocked, detached.Outcome);
        Assert.Equal(ActivationState.Unsafe, harness.Journal.State);
        Assert.True(harness.Lease.Disposed);
        Assert.True(harness.Artifact.Disposed);
        Assert.False(harness.Journal.SessionLeaseHeld);
    }

    [Fact]
    public async Task Failed_quiesced_wait_is_distinguished_from_a_timeout()
    {
        var harness = new InjectorHarness();
        var injected = await harness.Injector.InjectAsync(harness.Permit);
        harness.Api.QuiescedWait = new NativeWaitResult(NativeWaitKind.Failed, 6);

        var detached = await harness.Injector.DetachAsync(injected.Session!);

        Assert.Equal(ExplorerDetachOutcome.Unsafe, detached.Outcome);
        Assert.Equal(InjectorFailureCode.QuiescedWaitFailed, detached.Failure!.Code);
        Assert.Equal(6, detached.Failure.NativeError);
        Assert.Equal(ActivationState.Unsafe, harness.Journal.State);
    }

    [Fact]
    public async Task Synchronously_failed_detach_can_be_retried()
    {
        var harness = new InjectorHarness();
        var injected = await harness.Injector.InjectAsync(harness.Permit);
        harness.Api.ShutdownSignalError = 5;

        var first = await harness.Injector.DetachAsync(injected.Session!);
        harness.Api.ShutdownSignalError = 0;
        var second = await harness.Injector.DetachAsync(injected.Session!);

        Assert.Equal(ExplorerDetachOutcome.Unsafe, first.Outcome);
        Assert.Equal(InjectorFailureCode.ShutdownSignalFailed, first.Failure!.Code);
        Assert.Equal(ExplorerDetachOutcome.Clean, second.Outcome);
        Assert.Equal(2, harness.Api.ShutdownSignalAttemptCount);
        Assert.Equal(1, harness.Api.ShutdownSignalCount);
        Assert.Equal(ActivationState.Clean, harness.Journal.State);
    }

    [Fact]
    public async Task Concurrent_detach_calls_share_one_shutdown_and_cleanup_operation()
    {
        var harness = new InjectorHarness();
        var injected = await harness.Injector.InjectAsync(harness.Permit);
        using var waitEntered = new ManualResetEventSlim(initialState: false);
        using var releaseWait = new ManualResetEventSlim(initialState: false);
        harness.Api.QuiescedWaitEntered = waitEntered;
        harness.Api.ReleaseQuiescedWait = releaseWait;

        var first = Task.Run(() => harness.Injector.DetachAsync(injected.Session!));
        Assert.True(waitEntered.Wait(TimeSpan.FromSeconds(5)));
        var second = harness.Injector.DetachAsync(injected.Session!);
        releaseWait.Set();

        var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(5));
        var secondResult = await second.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ExplorerDetachOutcome.Clean, firstResult.Outcome);
        Assert.Equal(ExplorerDetachOutcome.Clean, secondResult.Outcome);
        Assert.Equal(1, harness.Api.ShutdownSignalAttemptCount);
        Assert.Equal(1, harness.Api.QuiescedWaitCount);
        Assert.Equal(1, harness.Journal.SessionLeaseDisposeCount);
    }

    private sealed class InjectorHarness
    {
        internal const ulong KernelBase = 0x00000002_10000000;
        internal const ulong BridgeBase = 0x00000003_20000000;
        internal const uint LoadLibraryRva = 0x1234;
        internal const uint StartProbeRva = 0x1110;
        internal const string BridgePath = @"C:\safe\bridge\CodexQuotaTaskbar.Bridge.dll";
        private const string ExplorerPath = @"C:\Windows\explorer.exe";
        private static readonly Guid ActivationId =
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

        internal InjectorHarness()
        {
            Lease = new FakeMutationLease(Trace);
            Api = new FakeProcessApi(Lease, Trace);
            Artifact = new FakeArtifact();
            Store = new FakeArtifactStore(Artifact);
            Inspector = new FakeImageInspector();
            Journal = new FakeJournal(Trace, ActivationId);
            Injector = new ExplorerInjector(Api, Store, Inspector, Journal);
            Permit = LiveActivationPermit.Issue(
                "1.2.3",
                26200,
                new string('a', 64),
                new string('b', 64),
                4321,
                0x0123456789abcdef,
                ExplorerPath,
                explicitRetry: false);
        }

        internal LiveActivationPermit IssuePermit(bool explicitRetry) =>
            LiveActivationPermit.Issue(
                "1.2.3",
                26200,
                new string('a', 64),
                new string('b', 64),
                4321,
                0x0123456789abcdef,
                ExplorerPath,
                explicitRetry);

        internal List<string> Trace { get; } = [];

        internal FakeMutationLease Lease { get; }

        internal FakeProcessApi Api { get; }

        internal FakeArtifact Artifact { get; }

        internal FakeArtifactStore Store { get; }

        internal FakeImageInspector Inspector { get; }

        internal FakeJournal Journal { get; }

        internal ExplorerInjector Injector { get; }

        internal LiveActivationPermit Permit { get; }

        internal sealed class FakeMutationLease : IExplorerMutationLease
        {
            private readonly List<string> trace;
            private int moduleReadCount;

            internal FakeMutationLease(List<string> trace)
            {
                this.trace = trace;
                SnapshotValue = new ExplorerProcessSnapshot(
                    4321,
                    0x0123456789abcdef,
                    7,
                    "S-1-5-21-test",
                    ExplorerProcessArchitecture.X64,
                    ExplorerPath,
                    hasExited: false);
                ThreadWaits.Enqueue(new NativeWaitResult(NativeWaitKind.Signaled, 0, 0x89abcdef));
                ThreadWaits.Enqueue(new NativeWaitResult(NativeWaitKind.Signaled, 0, 0));
            }

            public int ProcessId => 4321;

            internal ExplorerProcessSnapshot SnapshotValue { get; set; }

            internal int AllocationError { get; set; }

            internal int WriteError { get; set; }

            internal int ThreadStartError { get; set; }

            internal bool KeepBridgeMappedDuringDetach { get; set; }

            internal bool KeepWrongSizeBridgeMappedDuringDetach { get; set; }

            internal bool AddWrongSizeBridgeDuringFinalValidation { get; set; }

            internal uint? PreexistingBridgeImageSize { get; set; }

            internal int LoadedBridgeCount { get; set; } = 1;

            internal uint LoadedBridgeImageSize { get; set; } = 0x2000;

            internal bool Disposed { get; private set; }

            internal bool ThrowOnDispose { get; set; }

            internal List<FakeMemory> Memories { get; } = [];

            internal List<WriteCall> Writes { get; } = [];

            internal List<ThreadCall> ThreadStarts { get; } = [];

            internal Queue<NativeWaitResult> ThreadWaits { get; } = new();

            public NativeResult<ExplorerProcessSnapshot> Snapshot()
            {
                trace.Add("Snapshot");
                return NativeResult<ExplorerProcessSnapshot>.Success(SnapshotValue);
            }

            public NativeResult<IReadOnlyList<RemoteModule>> EnumerateModules()
            {
                trace.Add("Modules");
                moduleReadCount++;
                IReadOnlyList<RemoteModule> modules;
                if (moduleReadCount is 1 or 2)
                {
                    modules = PreexistingBridgeImageSize is uint size
                        ? [KernelModule(), BridgeModule(size)]
                        : [KernelModule()];
                }
                else if (moduleReadCount is 3 or 4)
                {
                    var loaded = Enumerable.Range(0, LoadedBridgeCount)
                        .Select(index => BridgeModule(
                            LoadedBridgeImageSize,
                            BridgeBase + checked((ulong)index * 0x10000)))
                        .ToList();
                    if (moduleReadCount == 4 && AddWrongSizeBridgeDuringFinalValidation)
                    {
                        loaded.Add(BridgeModule(0x3000, BridgeBase + 0x10000));
                    }

                    modules = [KernelModule(), .. loaded];
                }
                else if (KeepBridgeMappedDuringDetach)
                {
                    modules = [KernelModule(), BridgeModule()];
                }
                else if (KeepWrongSizeBridgeMappedDuringDetach)
                {
                    modules = [KernelModule(), BridgeModule(0x3000)];
                }
                else
                {
                    modules = [KernelModule()];
                }

                return NativeResult<IReadOnlyList<RemoteModule>>.Success(modules);
            }

            public NativeWaitResult WaitForProcessExit(TimeSpan timeout) =>
                new(NativeWaitKind.TimedOut, 0);

            public NativeResult<IRemoteMemory> Allocate(nuint size)
            {
                trace.Add($"Allocate:{size}");
                if (AllocationError != 0)
                {
                    return NativeResult<IRemoteMemory>.Failure(AllocationError);
                }

                var memory = new FakeMemory(
                    0x00000004_00000000 + checked((ulong)Memories.Count * 0x1000),
                    size);
                Memories.Add(memory);
                return NativeResult<IRemoteMemory>.Success(memory);
            }

            public NativeResult WriteAll(IRemoteMemory memory, ReadOnlyMemory<byte> bytes)
            {
                trace.Add($"Write:{bytes.Length}");
                if (WriteError != 0)
                {
                    return NativeResult.Failure(WriteError);
                }

                Writes.Add(new WriteCall(memory.Address, bytes.ToArray()));
                return NativeResult.Success();
            }

            public NativeResult<IRemoteThread> StartRemoteThread(
                ulong startAddress,
                IRemoteMemory? parameter)
            {
                trace.Add($"Thread:{startAddress:x16}");
                if (ThreadStartError != 0)
                {
                    return NativeResult<IRemoteThread>.Failure(ThreadStartError);
                }

                var name = ThreadStarts.Count == 0 ? "LoadLibrary" : "StartProbe";
                var call = new ThreadCall(startAddress, parameter?.Address ?? 0);
                ThreadStarts.Add(call);
                return NativeResult<IRemoteThread>.Success(
                    new FakeThread(name, ThreadWaits.Dequeue(), trace));
            }

            public void Dispose()
            {
                Disposed = true;
                trace.Add("LeaseDispose");
                if (ThrowOnDispose)
                {
                    throw new IOException("fake process lease dispose failure");
                }
            }

            private static RemoteModule KernelModule() =>
                new(@"C:\Windows\System32\kernelbase.dll", KernelBase, 0x200000);

            private static RemoteModule BridgeModule(
                uint size = 0x2000,
                ulong baseAddress = BridgeBase) =>
                new(BridgePath, baseAddress, size);
        }

        internal sealed class FakeProcessApi : IExplorerProcessApi
        {
            private readonly FakeMutationLease lease;
            private readonly List<string> trace;

            internal FakeProcessApi(FakeMutationLease lease, List<string> trace)
            {
                this.lease = lease;
                this.trace = trace;
            }

            public uint CurrentSessionId => 7;

            public string CurrentUserSid => "S-1-5-21-test";

            internal int OpenInjectionError { get; set; }

            internal int OpenCollectionError { get; set; }

            internal int OpenInjectionCount { get; private set; }

            internal int OpenCollectionCount { get; private set; }

            internal int? LastCollectionProcessId { get; private set; }

            internal int StartReleasedSignalCount { get; private set; }

            internal int ShutdownSignalCount { get; private set; }

            internal int ShutdownSignalAttemptCount { get; private set; }

            internal int ShutdownSignalError { get; set; }

            internal int QuiescedWaitCount { get; private set; }

            internal ManualResetEventSlim? QuiescedWaitEntered { get; set; }

            internal ManualResetEventSlim? ReleaseQuiescedWait { get; set; }

            internal NativeWaitResult ReadyWait { get; set; } =
                new(NativeWaitKind.Signaled, 0);

            internal NativeWaitResult QuiescedWait { get; set; } =
                new(NativeWaitKind.Signaled, 0);

            internal bool ThrowOnReadyWait { get; set; }

            internal bool PhaseEventsDisappearWhenReadyWaitBegins { get; set; }

            internal bool ReadyWaitStarted { get; private set; }

            internal HashSet<string> MissingEvents { get; } =
                new(StringComparer.Ordinal);

            internal HashSet<string> SignaledLifecyclePhases { get; } =
                new(StringComparer.Ordinal);

            internal List<TimeSpan> LifecyclePhaseWaitTimeouts { get; } = [];

            public NativeResult<IExplorerReadLease> OpenForCollection(int processId)
            {
                OpenCollectionCount++;
                LastCollectionProcessId = processId;
                return OpenCollectionError == 0
                    ? NativeResult<IExplorerReadLease>.Success(lease)
                    : NativeResult<IExplorerReadLease>.Failure(OpenCollectionError);
            }

            public NativeResult<IExplorerMutationLease> OpenForInjection(int processId)
            {
                OpenInjectionCount++;
                trace.Add("OpenInjection");
                return OpenInjectionError == 0
                    ? NativeResult<IExplorerMutationLease>.Success(lease)
                    : NativeResult<IExplorerMutationLease>.Failure(OpenInjectionError);
            }

            public NativeResult<LocalSystemExport> ResolveLocalSystemExport(
                string moduleName,
                string exportName) =>
                NativeResult<LocalSystemExport>.Success(
                    new LocalSystemExport(
                        @"C:\Windows\System32\kernelbase.dll",
                        LoadLibraryRva,
                        0x200000));

            public NativeResult<INamedEvent> TryOpenEvent(
                string eventName,
                NativeEventAccess access)
            {
                var kind = eventName[(eventName.LastIndexOf('.') + 1)..];
                var isLifecyclePhase = eventName.Contains(".Phase.", StringComparison.Ordinal);
                trace.Add(isLifecyclePhase ? $"EventOpen:Phase:{kind}" : $"EventOpen:{kind}");
                if (MissingEvents.Contains(kind) ||
                    (isLifecyclePhase &&
                        PhaseEventsDisappearWhenReadyWaitBegins &&
                        ReadyWaitStarted))
                {
                    return NativeResult<INamedEvent>.Failure(2);
                }

                return NativeResult<INamedEvent>.Success(
                    new FakeEvent(this, kind, access, isLifecyclePhase, trace));
            }

            public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
                Task.CompletedTask;

            private sealed class FakeEvent : INamedEvent
            {
                private readonly FakeProcessApi owner;
                private readonly string kind;
                private readonly NativeEventAccess access;
                private readonly bool isLifecyclePhase;
                private readonly List<string> trace;

                internal FakeEvent(
                    FakeProcessApi owner,
                    string kind,
                    NativeEventAccess access,
                    bool isLifecyclePhase,
                    List<string> trace)
                {
                    this.owner = owner;
                    this.kind = kind;
                    this.access = access;
                    this.isLifecyclePhase = isLifecyclePhase;
                    this.trace = trace;
                }

                public NativeWaitResult Wait(TimeSpan timeout)
                {
                    trace.Add(isLifecyclePhase ? $"EventWait:Phase:{kind}" : $"EventWait:{kind}");
                    if (isLifecyclePhase)
                    {
                        owner.LifecyclePhaseWaitTimeouts.Add(timeout);
                        return owner.SignaledLifecyclePhases.Contains(kind)
                            ? new NativeWaitResult(NativeWaitKind.Signaled, 0)
                            : new NativeWaitResult(NativeWaitKind.TimedOut, 0);
                    }

                    if (kind == "Ready")
                    {
                        owner.ReadyWaitStarted = true;
                    }

                    if (kind == "Ready" && owner.ThrowOnReadyWait)
                    {
                        throw new IOException("fake ready wait failure");
                    }

                    if (kind == "Quiesced")
                    {
                        owner.QuiescedWaitCount++;
                        owner.QuiescedWaitEntered?.Set();
                        if (owner.ReleaseQuiescedWait is not null &&
                            !owner.ReleaseQuiescedWait.Wait(TimeSpan.FromSeconds(5)))
                        {
                            throw new TimeoutException("fake quiesced wait was not released");
                        }

                        return owner.QuiescedWait;
                    }

                    return kind == "Ready"
                        ? owner.ReadyWait
                        : new NativeWaitResult(NativeWaitKind.Failed, 5);
                }

                public NativeResult Signal()
                {
                    trace.Add($"EventSignal:{kind}");
                    if (access != NativeEventAccess.Signal)
                    {
                        return NativeResult.Failure(5);
                    }

                    if (kind == "StartReleased")
                    {
                        owner.StartReleasedSignalCount++;
                    }
                    else if (kind == "Shutdown")
                    {
                        owner.ShutdownSignalAttemptCount++;
                        if (owner.ShutdownSignalError != 0)
                        {
                            return NativeResult.Failure(owner.ShutdownSignalError);
                        }

                        owner.ShutdownSignalCount++;
                    }

                    return NativeResult.Success();
                }

                public void Dispose() => trace.Add(
                    isLifecyclePhase ? $"EventDispose:Phase:{kind}" : $"EventDispose:{kind}");
            }
        }

        internal sealed class FakeMemory : IRemoteMemory
        {
            internal FakeMemory(ulong address, nuint size)
            {
                Address = address;
                Size = size;
            }

            public ulong Address { get; }

            public nuint Size { get; }

            internal bool Abandoned { get; private set; }

            internal bool Released { get; private set; }

            public void Abandon() => Abandoned = true;

            public void Dispose()
            {
                if (!Abandoned)
                {
                    Released = true;
                }
            }
        }

        internal sealed class FakeThread : IRemoteThread
        {
            private readonly string name;
            private readonly NativeWaitResult result;
            private readonly List<string> trace;

            internal FakeThread(
                string name,
                NativeWaitResult result,
                List<string> trace)
            {
                this.name = name;
                this.result = result;
                this.trace = trace;
            }

            public NativeWaitResult Wait(TimeSpan timeout)
            {
                trace.Add($"ThreadWait:{name}");
                return result;
            }

            public void Dispose() => trace.Add($"ThreadDispose:{name}");
        }

        internal sealed class FakeArtifact : IBridgeArtifactLease
        {
            public string CanonicalPath => BridgePath;

            public string Sha256Hex => new string('c', 64);

            internal bool Disposed { get; private set; }

            public Stream OpenRead() => new MemoryStream([1, 2, 3], writable: false);

            public void Dispose() => Disposed = true;
        }

        internal sealed class FakeArtifactStore : IBridgeArtifactStore
        {
            private readonly FakeArtifact artifact;

            internal FakeArtifactStore(FakeArtifact artifact) => this.artifact = artifact;

            internal int MaterializeCount { get; private set; }

            public IBridgeArtifactLease Materialize()
            {
                MaterializeCount++;
                return artifact;
            }
        }

        internal sealed class FakeImageInspector : IBridgeImageInspector
        {
            internal BridgeImageException? Failure { get; set; }

            public BridgeImageContract InspectAndValidate(IBridgeArtifactLease artifact)
            {
                if (Failure is not null)
                {
                    throw Failure;
                }

                return new BridgeImageContract(
                    BridgePath,
                    artifact.Sha256Hex,
                    1,
                    0x2000,
                    0x1100,
                    StartProbeRva);
            }
        }

        internal sealed class FakeJournal : IActivationJournalStore
        {
            private readonly List<string> trace;
            private readonly Guid activationId;
            private ActivationJournalRecord? record;

            internal FakeJournal(List<string> trace, Guid activationId)
            {
                this.trace = trace;
                this.activationId = activationId;
            }

            internal ActivationState? State { get; set; }

            internal int RecordedExplorerProcessId { get; set; } = 4321;

            internal int BeginCount { get; private set; }

            internal bool SessionLeaseHeld { get; private set; }

            internal int SessionLeaseDisposeCount { get; private set; }

            internal bool ThrowOnAcquireSessionLease { get; set; }

            internal bool ThrowOnReadStatus { get; set; }

            public ActivationSessionLeaseResult TryAcquireSessionLease()
            {
                if (ThrowOnAcquireSessionLease)
                {
                    throw new IOException("fake session lease acquisition failure");
                }

                if (SessionLeaseHeld)
                {
                    return new ActivationSessionLeaseResult(
                        ActivationSessionLeaseOutcome.Busy,
                        null);
                }

                SessionLeaseHeld = true;
                return new ActivationSessionLeaseResult(
                    ActivationSessionLeaseOutcome.Acquired,
                    new FakeSessionLease(this));
            }

            public ActivationJournalResult ReadStatus()
            {
                if (ThrowOnReadStatus)
                {
                    throw new IOException("fake journal read failure");
                }

                if (State is null or ActivationState.Clean)
                {
                    return new ActivationJournalResult(ActivationJournalOutcome.Allowed, record);
                }

                record ??= CreateRecord(State.Value);
                return new ActivationJournalResult(ActivationJournalOutcome.Blocked, record);
            }

            public ActivationJournalResult TryBegin(
                ActivationStartRequest request,
                bool explicitRetry)
            {
                BeginCount++;
                trace.Add("Journal:Pending");
                record = new ActivationJournalRecord(
                    request.AppVersion,
                    request.WindowsBuild,
                    request.ExplorerSignatureSha256,
                    request.TaskbarSignatureSha256,
                    request.ExplorerProcessId,
                    request.ExplorerCreationTimeFileTime100ns,
                    activationId,
                    DateTimeOffset.UtcNow,
                    ActivationState.Pending);
                State = ActivationState.Pending;
                return new ActivationJournalResult(
                    ActivationJournalOutcome.PendingPersisted,
                    record);
            }

            public ActivationJournalResult TryTransition(Guid id, ActivationState state)
            {
                trace.Add($"Journal:{state}");
                State = state;
                record = record! with { State = state };
                return new ActivationJournalResult(
                    ActivationJournalOutcome.TransitionPersisted,
                    record);
            }

            private ActivationJournalRecord CreateRecord(ActivationState state) =>
                new(
                    "1.2.3",
                    26200,
                    new string('a', 64),
                    new string('b', 64),
                    RecordedExplorerProcessId,
                    0x0123456789abcdef,
                    activationId,
                    DateTimeOffset.UtcNow,
                    state);

            private sealed class FakeSessionLease(FakeJournal owner) :
                IActivationSessionLease
            {
                private FakeJournal? owner = owner;

                public void Dispose()
                {
                    var current = Interlocked.Exchange(ref owner, null);
                    if (current is null)
                    {
                        return;
                    }

                    current.SessionLeaseHeld = false;
                    current.SessionLeaseDisposeCount++;
                }
            }
        }

        internal sealed record WriteCall(ulong Address, byte[] Bytes);

        internal sealed record ThreadCall(ulong StartAddress, ulong ParameterAddress);
    }
}
