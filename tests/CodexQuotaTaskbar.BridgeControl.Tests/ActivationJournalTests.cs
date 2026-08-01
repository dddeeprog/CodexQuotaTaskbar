using System.Text.Json;
using CodexQuotaTaskbar.BridgeControl.Safety;

namespace CodexQuotaTaskbar.BridgeControl.Tests;

public sealed class ActivationJournalTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 8, 1, 10, 30, 0, TimeSpan.Zero);

    private static readonly string UpperDigest = new('A', 64);
    private static readonly string LowerDigest = new('b', 64);

    public static TheoryData<string, string> InvalidJournalDocuments => new()
    {
        { "malformed JSON", "{" },
        { "unknown schema", ValidJson(values => values["schemaVersion"] = 2) },
        { "unknown field", ValidJson(values => values["unexpected"] = "data") },
        { "missing field", ValidJson(values => values.Remove("appVersion")) },
        { "invalid enum", ValidJson(values => values["state"] = "pending") },
        { "non-normalized digest", ValidJson(values => values["explorerSignatureSha256"] = UpperDigest) },
        { "path-like digest", ValidJson(values => values["taskbarSignatureSha256"] = "C:/taskbar.dll") },
        { "path-like app version", ValidJson(values => values["appVersion"] = @"C:\Users\person\app.exe") },
        { "future start", ValidJson(values => values["startedUtc"] = FixedNow.AddTicks(1)) },
        { "non-UTC start", ValidJson(values => values["startedUtc"] = FixedNow.ToOffset(TimeSpan.FromHours(8))) },
        { "zero build", ValidJson(values => values["windowsBuild"] = 0) },
        { "zero Explorer PID", ValidJson(values => values["explorerProcessId"] = 0) },
        { "zero Explorer creation time", ValidJson(values => values["explorerCreationTimeFileTime100ns"] = 0UL) },
        { "zero activation ID", ValidJson(values => values["activationId"] = Guid.Empty) },
    };

    [Fact]
    public void Uses_the_fixed_local_app_data_path_and_allows_test_root_injection()
    {
        var expectedDefaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexQuotaTaskbar",
            "Probe",
            "activation.json");

        using var temporaryDirectory = new TemporaryDirectory();
        var journal = CreateJournal(temporaryDirectory.Path);

        Assert.Equal(expectedDefaultPath, ActivationJournal.DefaultPath);
        Assert.Equal(
            Path.Combine(
                temporaryDirectory.Path,
                "CodexQuotaTaskbar",
                "Probe",
                "activation.json"),
            journal.JournalPath);
    }

    [Fact]
    public void Begin_persists_a_minimal_normalized_pending_record_synchronously()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var journal = CreateJournal(temporaryDirectory.Path);

        var result = journal.TryBegin(CreateRequest());

        Assert.Equal(ActivationJournalOutcome.PendingPersisted, result.Outcome);
        Assert.True(result.Succeeded);
        Assert.NotNull(result.Record);
        Assert.NotEqual(Guid.Empty, result.Record.ActivationId);
        Assert.Equal(ActivationState.Pending, result.Record.State);
        Assert.Equal(FixedNow, result.Record.StartedUtc);
        Assert.True(File.Exists(journal.JournalPath));

        var json = File.ReadAllText(journal.JournalPath);
        using var document = JsonDocument.Parse(json);
        var properties = document.RootElement.EnumerateObject().ToArray();
        var expectedNames = new[]
        {
            "activationId",
            "appVersion",
            "explorerCreationTimeFileTime100ns",
            "explorerProcessId",
            "explorerSignatureSha256",
            "schemaVersion",
            "startedUtc",
            "state",
            "taskbarSignatureSha256",
            "windowsBuild",
        };

        Assert.Equal(
            expectedNames,
            properties.Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("1.2.3-beta.1", document.RootElement.GetProperty("appVersion").GetString());
        Assert.Equal(26200, document.RootElement.GetProperty("windowsBuild").GetInt32());
        Assert.Equal(new string('a', 64), document.RootElement.GetProperty("explorerSignatureSha256").GetString());
        Assert.Equal(LowerDigest, document.RootElement.GetProperty("taskbarSignatureSha256").GetString());
        Assert.Equal(4321, document.RootElement.GetProperty("explorerProcessId").GetInt32());
        Assert.Equal(134_300_538_000_000_000UL, document.RootElement.GetProperty("explorerCreationTimeFileTime100ns").GetUInt64());
        Assert.Equal(result.Record.ActivationId, document.RootElement.GetProperty("activationId").GetGuid());
        Assert.Equal(FixedNow, document.RootElement.GetProperty("startedUtc").GetDateTimeOffset());
        Assert.Equal("Pending", document.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public void None_and_clean_allow_a_new_activation()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var journal = CreateJournal(temporaryDirectory.Path);

        var missing = journal.ReadStatus();

        Assert.Equal(ActivationJournalOutcome.Allowed, missing.Outcome);
        Assert.True(missing.Succeeded);
        Assert.Null(missing.Record);

        var pending = journal.TryBegin(CreateRequest());
        var clean = journal.TryTransition(pending.Record!.ActivationId, ActivationState.Clean);
        var status = journal.ReadStatus();

        Assert.Equal(ActivationJournalOutcome.TransitionPersisted, clean.Outcome);
        Assert.Equal(ActivationJournalOutcome.Allowed, status.Outcome);
        Assert.Equal(ActivationState.Clean, status.Record!.State);
    }

    [Fact]
    public async Task Different_instances_serialize_begin_for_the_same_journal_path()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var firstEnteredClock = new ManualResetEventSlim();
        using var releaseFirstClock = new ManualResetEventSlim();
        using var secondEnteredClock = new ManualResetEventSlim();
        var first = new ActivationJournal(
            temporaryDirectory.Path,
            () =>
            {
                firstEnteredClock.Set();
                Assert.True(releaseFirstClock.Wait(TimeSpan.FromSeconds(5)));
                return FixedNow;
            });
        var second = new ActivationJournal(
            temporaryDirectory.Path,
            () =>
            {
                secondEnteredClock.Set();
                return FixedNow;
            });

        var firstTask = Task.Factory.StartNew(
            () => first.TryBegin(CreateRequest()),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Assert.True(firstEnteredClock.Wait(TimeSpan.FromSeconds(5)));

        var secondTask = Task.Factory.StartNew(
            () => second.TryBegin(CreateRequest()),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        var secondReachedClockBeforeRelease =
            secondEnteredClock.Wait(TimeSpan.FromMilliseconds(250));
        if (secondReachedClockBeforeRelease)
        {
            await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
        }

        releaseFirstClock.Set();
        var firstResult = await firstTask;
        var secondResult = await secondTask;

        Assert.False(secondReachedClockBeforeRelease);
        Assert.Equal(ActivationJournalOutcome.PendingPersisted, firstResult.Outcome);
        Assert.Equal(ActivationJournalOutcome.Blocked, secondResult.Outcome);
        Assert.Equal(firstResult.Record!.ActivationId, secondResult.Record!.ActivationId);
        Assert.Equal(firstResult.Record, first.ReadStatus().Record);
    }

    [Fact]
    public void Session_lease_is_exclusive_across_instances_and_reacquirable_after_release()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var firstJournal = CreateJournal(temporaryDirectory.Path);
        var secondJournal = CreateJournal(temporaryDirectory.Path);

        var first = firstJournal.TryAcquireSessionLease();
        var blocked = secondJournal.TryAcquireSessionLease();

        Assert.Equal(ActivationSessionLeaseOutcome.Acquired, first.Outcome);
        Assert.NotNull(first.Lease);
        Assert.Equal(ActivationSessionLeaseOutcome.Busy, blocked.Outcome);
        Assert.Null(blocked.Lease);

        first.Lease.Dispose();
        var reacquired = secondJournal.TryAcquireSessionLease();

        Assert.Equal(ActivationSessionLeaseOutcome.Acquired, reacquired.Outcome);
        Assert.NotNull(reacquired.Lease);
        reacquired.Lease.Dispose();
    }

    [Fact]
    public void Mutex_name_uses_local_scope_and_only_a_canonical_path_hash()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string? observedName = null;
        var gate = new DelegatingMutexGate(
            (name, _) =>
            {
                observedName = name;
                return NoOpLease.Instance;
            });
        var journal = new ActivationJournal(
            temporaryDirectory.Path,
            () => FixedNow,
            gate,
            TimeSpan.FromSeconds(1));

        var status = journal.ReadStatus();

        Assert.Equal(ActivationJournalOutcome.Allowed, status.Outcome);
        Assert.Equal(journal.MutexName, observedName);
        Assert.Matches(
            @"^Local\\CQTB\.ActivationJournal\.v1\.[0-9A-F]{64}$",
            journal.MutexName);
        Assert.DoesNotContain(
            temporaryDirectory.Path,
            journal.MutexName,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mutex_timeout_is_a_typed_storage_failure_without_touching_the_journal()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var journal = new ActivationJournal(
            temporaryDirectory.Path,
            () => FixedNow,
            new DelegatingMutexGate((_, _) => null),
            TimeSpan.FromMilliseconds(1));

        var result = journal.TryBegin(CreateRequest());

        Assert.Equal(ActivationJournalOutcome.StorageFailure, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.False(File.Exists(journal.JournalPath));
    }

    [Fact]
    public void Mutex_open_exception_is_a_typed_storage_failure_and_preserves_the_record()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var initialJournal = CreateJournal(temporaryDirectory.Path);
        var pending = initialJournal.TryBegin(CreateRequest());
        var before = File.ReadAllBytes(initialJournal.JournalPath);
        var failingJournal = new ActivationJournal(
            temporaryDirectory.Path,
            () => FixedNow,
            new DelegatingMutexGate(
                (_, _) => throw new WaitHandleCannotBeOpenedException("mutex unavailable")),
            TimeSpan.FromSeconds(1));

        var result = failingJournal.TryTransition(
            pending.Record!.ActivationId,
            ActivationState.Stable);

        Assert.Equal(ActivationJournalOutcome.StorageFailure, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.Equal(before, File.ReadAllBytes(initialJournal.JournalPath));
    }

    [Fact]
    public void Abandoned_mutex_is_safely_taken_over()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var journal = CreateJournal(temporaryDirectory.Path);
        using var keepNamedObjectAlive = new Mutex(initiallyOwned: false, journal.MutexName);
        using var acquired = new ManualResetEventSlim();
        Exception? ownerFailure = null;
        var owner = new Thread(
            () =>
            {
                try
                {
                    var abandoned = new Mutex(initiallyOwned: false, journal.MutexName);
                    if (!abandoned.WaitOne(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("Could not acquire the test mutex.");
                    }

                    acquired.Set();
                    GC.KeepAlive(abandoned);
                }
                catch (Exception exception)
                {
                    ownerFailure = exception;
                    acquired.Set();
                }
            });
        owner.Start();
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(owner.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(ownerFailure);

        var result = journal.TryBegin(CreateRequest());

        Assert.Equal(ActivationJournalOutcome.PendingPersisted, result.Outcome);
        Assert.Equal(ActivationState.Pending, result.Record!.State);
        GC.KeepAlive(keepNamedObjectAlive);
    }

    [Theory]
    [InlineData(ActivationState.Pending)]
    [InlineData(ActivationState.Stable)]
    [InlineData(ActivationState.Unsafe)]
    public void Unclean_record_blocks_live_until_explicit_retry_writes_a_new_pending(
        ActivationState existingState)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var journal = CreateJournal(temporaryDirectory.Path);
        var first = journal.TryBegin(CreateRequest());
        if (existingState != ActivationState.Pending)
        {
            Assert.Equal(
                ActivationJournalOutcome.TransitionPersisted,
                journal.TryTransition(first.Record!.ActivationId, existingState).Outcome);
        }

        var blockedStatus = journal.ReadStatus();
        var blockedBegin = journal.TryBegin(CreateRequest());

        Assert.Equal(ActivationJournalOutcome.Blocked, blockedStatus.Outcome);
        Assert.Equal(ActivationJournalOutcome.Blocked, blockedBegin.Outcome);
        Assert.Equal(first.Record!.ActivationId, blockedBegin.Record!.ActivationId);

        var retry = journal.TryBegin(CreateRequest(), explicitRetry: true);

        Assert.Equal(ActivationJournalOutcome.PendingPersisted, retry.Outcome);
        Assert.NotEqual(first.Record.ActivationId, retry.Record!.ActivationId);
        Assert.Equal(ActivationState.Pending, retry.Record.State);
        Assert.Equal(
            retry.Record.ActivationId,
            journal.ReadStatus().Record!.ActivationId);
    }

    [Theory]
    [MemberData(nameof(InvalidJournalDocuments))]
    public void Invalid_documents_fail_closed_even_for_explicit_retry(
        string description,
        string json)
    {
        _ = description;
        using var temporaryDirectory = new TemporaryDirectory();
        var journal = CreateJournal(temporaryDirectory.Path);
        WriteJournal(journal, json);
        var before = File.ReadAllBytes(journal.JournalPath);

        var status = journal.ReadStatus();
        var begin = journal.TryBegin(CreateRequest(), explicitRetry: true);

        Assert.Equal(ActivationJournalOutcome.InvalidRecord, status.Outcome);
        Assert.Equal(ActivationJournalOutcome.InvalidRecord, begin.Outcome);
        Assert.False(status.Succeeded);
        Assert.False(begin.Succeeded);
        Assert.Null(status.Record);
        Assert.Equal(before, File.ReadAllBytes(journal.JournalPath));
    }

    [Fact]
    public void Oversized_document_fails_closed_without_loading_or_overwriting_it()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var journal = CreateJournal(temporaryDirectory.Path);
        WriteJournal(journal, new string('x', ActivationJournal.MaximumJournalBytes + 1));
        var before = File.ReadAllBytes(journal.JournalPath);

        var status = journal.ReadStatus();
        var begin = journal.TryBegin(CreateRequest(), explicitRetry: true);

        Assert.Equal(ActivationJournalOutcome.InvalidRecord, status.Outcome);
        Assert.Equal(ActivationJournalOutcome.InvalidRecord, begin.Outcome);
        Assert.Equal(before, File.ReadAllBytes(journal.JournalPath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Existing_reparse_point_anywhere_in_the_journal_path_fails_before_read_or_write(
        int reparsePathIndex)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var initialJournal = CreateJournal(temporaryDirectory.Path);
        Assert.Equal(
            ActivationJournalOutcome.PendingPersisted,
            initialJournal.TryBegin(CreateRequest()).Outcome);
        File.WriteAllText(initialJournal.JournalPath, "{");
        var before = File.ReadAllBytes(initialJournal.JournalPath);
        var paths = new[]
        {
            temporaryDirectory.Path,
            Path.Combine(temporaryDirectory.Path, "CodexQuotaTaskbar"),
            Path.Combine(temporaryDirectory.Path, "CodexQuotaTaskbar", "Probe"),
            initialJournal.JournalPath,
        };
        var journal = new ActivationJournal(
            temporaryDirectory.Path,
            () => FixedNow,
            NamedActivationJournalMutexGate.Instance,
            TimeSpan.FromSeconds(1),
            new ReparsePointPathInspector(paths[reparsePathIndex]));

        var status = journal.ReadStatus();
        var begin = journal.TryBegin(CreateRequest(), explicitRetry: true);

        Assert.Equal(ActivationJournalOutcome.StorageFailure, status.Outcome);
        Assert.Equal(ActivationJournalOutcome.StorageFailure, begin.Outcome);
        Assert.False(status.Succeeded);
        Assert.False(begin.Succeeded);
        Assert.Equal(before, File.ReadAllBytes(initialJournal.JournalPath));
    }

    [Fact]
    public void Path_inspection_failure_is_typed_and_does_not_create_the_journal()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var journal = new ActivationJournal(
            temporaryDirectory.Path,
            () => FixedNow,
            NamedActivationJournalMutexGate.Instance,
            TimeSpan.FromSeconds(1),
            new FailingPathInspector());

        var status = journal.ReadStatus();
        var begin = journal.TryBegin(CreateRequest());

        Assert.Equal(ActivationJournalOutcome.StorageFailure, status.Outcome);
        Assert.Equal(ActivationJournalOutcome.StorageFailure, begin.Outcome);
        Assert.False(File.Exists(journal.JournalPath));
    }

    [Fact]
    public void Invalid_start_requests_are_typed_and_never_create_a_record()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var journal = CreateJournal(temporaryDirectory.Path);
        var valid = CreateRequest();
        var invalidRequests = new ActivationStartRequest?[]
        {
            null,
            valid with { AppVersion = string.Empty },
            valid with { AppVersion = @"C:\Users\person\app.exe" },
            valid with { WindowsBuild = 0 },
            valid with { ExplorerSignatureSha256 = "not-a-digest" },
            valid with { TaskbarSignatureSha256 = "C:/taskbar.dll" },
            valid with { ExplorerProcessId = 0 },
            valid with { ExplorerCreationTimeFileTime100ns = 0 },
        };

        foreach (var request in invalidRequests)
        {
            var result = journal.TryBegin(request!);

            Assert.Equal(ActivationJournalOutcome.InvalidRequest, result.Outcome);
            Assert.False(result.Succeeded);
            Assert.Null(result.Record);
            Assert.False(File.Exists(journal.JournalPath));
        }
    }

    [Theory]
    [InlineData(ActivationState.Pending, ActivationState.Stable)]
    [InlineData(ActivationState.Pending, ActivationState.Clean)]
    [InlineData(ActivationState.Pending, ActivationState.Unsafe)]
    [InlineData(ActivationState.Stable, ActivationState.Clean)]
    [InlineData(ActivationState.Stable, ActivationState.Unsafe)]
    public void Persists_each_legal_one_way_transition(
        ActivationState initialState,
        ActivationState nextState)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var journal = CreateJournal(temporaryDirectory.Path);
        var pending = journal.TryBegin(CreateRequest());
        if (initialState == ActivationState.Stable)
        {
            Assert.Equal(
                ActivationJournalOutcome.TransitionPersisted,
                journal.TryTransition(pending.Record!.ActivationId, ActivationState.Stable).Outcome);
        }

        var transition = journal.TryTransition(pending.Record!.ActivationId, nextState);
        var persisted = journal.ReadStatus().Record;

        Assert.Equal(ActivationJournalOutcome.TransitionPersisted, transition.Outcome);
        Assert.True(transition.Succeeded);
        Assert.Equal(nextState, transition.Record!.State);
        Assert.Equal(nextState, persisted!.State);
        Assert.Equal(pending.Record.ActivationId, persisted.ActivationId);
    }

    [Theory]
    [InlineData(ActivationState.Pending, ActivationState.Pending)]
    [InlineData(ActivationState.Stable, ActivationState.Pending)]
    [InlineData(ActivationState.Stable, ActivationState.Stable)]
    [InlineData(ActivationState.Clean, ActivationState.Pending)]
    [InlineData(ActivationState.Clean, ActivationState.Stable)]
    [InlineData(ActivationState.Clean, ActivationState.Clean)]
    [InlineData(ActivationState.Clean, ActivationState.Unsafe)]
    [InlineData(ActivationState.Unsafe, ActivationState.Pending)]
    [InlineData(ActivationState.Unsafe, ActivationState.Stable)]
    [InlineData(ActivationState.Unsafe, ActivationState.Clean)]
    [InlineData(ActivationState.Unsafe, ActivationState.Unsafe)]
    [InlineData(ActivationState.Pending, (ActivationState)99)]
    public void Rejects_replay_reverse_and_terminal_transitions(
        ActivationState initialState,
        ActivationState nextState)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var journal = CreateJournal(temporaryDirectory.Path);
        var pending = journal.TryBegin(CreateRequest());
        if (initialState != ActivationState.Pending)
        {
            Assert.Equal(
                ActivationJournalOutcome.TransitionPersisted,
                journal.TryTransition(pending.Record!.ActivationId, initialState).Outcome);
        }

        var transition = journal.TryTransition(pending.Record!.ActivationId, nextState);
        var persisted = journal.ReadStatus().Record;

        Assert.Equal(ActivationJournalOutcome.InvalidTransition, transition.Outcome);
        Assert.False(transition.Succeeded);
        Assert.Equal(initialState, persisted!.State);
    }

    [Fact]
    public void Rejects_cross_activation_transition_without_changing_the_record()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var journal = CreateJournal(temporaryDirectory.Path);
        var pending = journal.TryBegin(CreateRequest());

        var transition = journal.TryTransition(Guid.NewGuid(), ActivationState.Stable);

        Assert.Equal(ActivationJournalOutcome.ActivationMismatch, transition.Outcome);
        Assert.False(transition.Succeeded);
        Assert.Equal(pending.Record, journal.ReadStatus().Record);
    }

    [Theory]
    [InlineData(ActivationState.Pending)]
    [InlineData(ActivationState.Stable)]
    [InlineData(ActivationState.Unsafe)]
    public void Controlled_recovery_persists_clean_for_the_exact_recorded_instance(
        ActivationState initialState)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var journal = CreateJournal(temporaryDirectory.Path);
        var pending = journal.TryBegin(CreateRequest());
        if (initialState != ActivationState.Pending)
        {
            Assert.Equal(
                ActivationJournalOutcome.TransitionPersisted,
                journal.TryTransition(pending.Record!.ActivationId, initialState).Outcome);
        }

        var record = journal.ReadStatus().Record!;
        var recovery = journal.TryRecoverToClean(
            record.ActivationId,
            record.ExplorerProcessId,
            record.ExplorerCreationTimeFileTime100ns);
        var persisted = journal.ReadStatus().Record;

        Assert.Equal(ActivationJournalOutcome.TransitionPersisted, recovery.Outcome);
        Assert.True(recovery.Succeeded);
        Assert.Equal(ActivationState.Clean, recovery.Record!.State);
        Assert.Equal(record with { State = ActivationState.Clean }, recovery.Record);
        Assert.Equal(recovery.Record, persisted);
    }

    [Theory]
    [InlineData("activation")]
    [InlineData("process")]
    [InlineData("creation-time")]
    public void Controlled_recovery_rejects_any_recorded_instance_mismatch(
        string mismatch)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var journal = CreateJournal(temporaryDirectory.Path);
        var pending = journal.TryBegin(CreateRequest());
        var record = pending.Record!;

        var recovery = journal.TryRecoverToClean(
            mismatch == "activation" ? Guid.NewGuid() : record.ActivationId,
            mismatch == "process" ? record.ExplorerProcessId + 1 : record.ExplorerProcessId,
            mismatch == "creation-time"
                ? record.ExplorerCreationTimeFileTime100ns + 1
                : record.ExplorerCreationTimeFileTime100ns);

        Assert.Equal(ActivationJournalOutcome.ActivationMismatch, recovery.Outcome);
        Assert.False(recovery.Succeeded);
        Assert.Equal(record, journal.ReadStatus().Record);
    }

    [Fact]
    public void Controlled_recovery_does_not_replay_a_clean_record()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var journal = CreateJournal(temporaryDirectory.Path);
        var pending = journal.TryBegin(CreateRequest());
        var clean = journal.TryTransition(
            pending.Record!.ActivationId,
            ActivationState.Clean);

        var replay = journal.TryRecoverToClean(
            clean.Record!.ActivationId,
            clean.Record.ExplorerProcessId,
            clean.Record.ExplorerCreationTimeFileTime100ns);

        Assert.Equal(ActivationJournalOutcome.InvalidTransition, replay.Outcome);
        Assert.False(replay.Succeeded);
        Assert.Equal(clean.Record, journal.ReadStatus().Record);
    }

    [Fact]
    public void Failed_atomic_replace_preserves_the_old_record_and_cleans_the_temp_file()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var journal = CreateJournal(temporaryDirectory.Path);
        var pending = journal.TryBegin(CreateRequest());
        var before = File.ReadAllBytes(journal.JournalPath);

        ActivationJournalResult transition;
        using (new FileStream(
            journal.JournalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            transition = journal.TryTransition(
                pending.Record!.ActivationId,
                ActivationState.Stable);

            Assert.Equal(before, File.ReadAllBytes(journal.JournalPath));
            Assert.Empty(Directory.GetFiles(
                Path.GetDirectoryName(journal.JournalPath)!,
                "*.tmp"));
        }

        Assert.Equal(ActivationJournalOutcome.StorageFailure, transition.Outcome);
        Assert.False(transition.Succeeded);
        Assert.Equal(ActivationState.Pending, journal.ReadStatus().Record!.State);
    }

    [Fact]
    public void Clock_and_storage_exceptions_are_returned_as_typed_failures()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var clockFailure = new ActivationJournal(
            temporaryDirectory.Path,
            () => throw new InvalidOperationException("clock unavailable"));

        Assert.Equal(
            ActivationJournalOutcome.StorageFailure,
            clockFailure.ReadStatus().Outcome);
        Assert.Equal(
            ActivationJournalOutcome.StorageFailure,
            clockFailure.TryBegin(CreateRequest()).Outcome);

        var journal = CreateJournal(temporaryDirectory.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(journal.JournalPath)!);
        using (new FileStream(
            journal.JournalPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            var readFailure = journal.ReadStatus();

            Assert.Equal(ActivationJournalOutcome.StorageFailure, readFailure.Outcome);
            Assert.False(readFailure.Succeeded);
        }
    }

    private static ActivationJournal CreateJournal(string root) =>
        new(root, () => FixedNow);

    private static ActivationStartRequest CreateRequest() =>
        new(
            "1.2.3-beta.1",
            26200,
            UpperDigest,
            LowerDigest,
            4321,
            134_300_538_000_000_000UL);

    private static string ValidJson(Action<Dictionary<string, object?>>? mutate = null)
    {
        var values = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["appVersion"] = "1.2.3",
            ["windowsBuild"] = 26200,
            ["explorerSignatureSha256"] = new string('a', 64),
            ["taskbarSignatureSha256"] = LowerDigest,
            ["explorerProcessId"] = 4321,
            ["explorerCreationTimeFileTime100ns"] = 134_300_538_000_000_000UL,
            ["activationId"] = Guid.Parse("b812d422-5c39-4f9b-b4ac-1f07fdcc81fb"),
            ["startedUtc"] = FixedNow,
            ["state"] = "Pending",
        };
        mutate?.Invoke(values);
        return JsonSerializer.Serialize(values);
    }

    private static void WriteJournal(ActivationJournal journal, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(journal.JournalPath)!);
        File.WriteAllText(journal.JournalPath, content);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "CodexQuotaTaskbar.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class DelegatingMutexGate(
        Func<string, TimeSpan, IDisposable?> enter) : IActivationJournalMutexGate
    {
        public IDisposable? TryEnter(string mutexName, TimeSpan timeout) =>
            enter(mutexName, timeout);
    }

    private sealed class NoOpLease : IDisposable
    {
        internal static NoOpLease Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private sealed class ReparsePointPathInspector(string reparsePath) :
        IActivationJournalPathInspector
    {
        public bool TryGetAttributes(
            string path,
            out FileAttributes attributes,
            out bool exists)
        {
            if (string.Equals(path, reparsePath, StringComparison.OrdinalIgnoreCase))
            {
                attributes = FileAttributes.ReparsePoint;
                exists = true;
                return true;
            }

            try
            {
                attributes = File.GetAttributes(path);
                exists = true;
                return true;
            }
            catch (FileNotFoundException)
            {
                attributes = default;
                exists = false;
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                attributes = default;
                exists = false;
                return true;
            }
        }
    }

    private sealed class FailingPathInspector : IActivationJournalPathInspector
    {
        public bool TryGetAttributes(
            string path,
            out FileAttributes attributes,
            out bool exists)
        {
            _ = path;
            attributes = default;
            exists = false;
            return false;
        }
    }
}
