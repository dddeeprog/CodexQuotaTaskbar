using System.ComponentModel;
using System.Text;
using CodexQuotaTaskbar.CompatibilityProbe.Safety;

namespace CodexQuotaTaskbar.Core.Tests.Compatibility;

public sealed class ExplorerResponsivenessProbeTests
{
    private static readonly ExplorerResponsivenessIdentity ExpectedIdentity = new(
        42,
        134_300_538_000_000_000UL,
        3,
        "S-1-5-21-111-222-333-1001");

    [Fact]
    public void Uses_wm_null_and_two_seconds_between_matching_identity_snapshots()
    {
        var api = new FakeResponsivenessApi(
            ExpectedIdentity,
            [new IntPtr(11), new IntPtr(12)]);
        var probe = new ExplorerResponsivenessProbe(api);

        var result = probe.Sample(ExpectedIdentity);

        Assert.Equal(ExplorerResponsivenessStatus.Responsive, result.Status);
        Assert.Equal(0, result.ConsecutiveFailures);
        Assert.Equal(ExplorerResponsivenessFailureReason.None, result.FailureReason);
        Assert.Equal(0, result.NativeErrorCode);
        Assert.Equal([42, 42], api.IdentityReads);
        Assert.Collection(
            api.Sends,
            send => AssertSend(send, new IntPtr(11)),
            send => AssertSend(send, new IntPtr(12)));
    }

    [Theory]
    [MemberData(nameof(MismatchedIdentities))]
    public void Any_identity_field_mismatch_before_sampling_fails_without_touching_windows(
        string mismatchedField)
    {
        var observed = mismatchedField switch
        {
            "process" => ExpectedIdentity with { ProcessId = 43 },
            "creation" => ExpectedIdentity with
            {
                CreationTimeFileTime100Nanoseconds =
                    ExpectedIdentity.CreationTimeFileTime100Nanoseconds + 1,
            },
            "session" => ExpectedIdentity with { SessionId = 4 },
            "sid" => ExpectedIdentity with
            {
                UserSid = "S-1-5-21-111-222-333-1002",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatchedField)),
        };
        var api = new FakeResponsivenessApi(ExpectedIdentity, [new IntPtr(11)]);
        api.IdentityOutcomes.Enqueue(observed);
        var probe = new ExplorerResponsivenessProbe(api);

        var result = probe.Sample(ExpectedIdentity);

        Assert.Equal(ExplorerResponsivenessStatus.TransientFailure, result.Status);
        Assert.Equal(
            ExplorerResponsivenessFailureReason.IdentityMismatchBeforeSample,
            result.FailureReason);
        Assert.Equal(0, api.FindCallCount);
        Assert.Empty(api.Sends);
    }

    [Fact]
    public void Identity_is_revalidated_after_window_sampling()
    {
        var replacement = ExpectedIdentity with
        {
            CreationTimeFileTime100Nanoseconds =
                ExpectedIdentity.CreationTimeFileTime100Nanoseconds + 1,
        };
        var api = new FakeResponsivenessApi(ExpectedIdentity, [new IntPtr(11)]);
        api.IdentityOutcomes.Enqueue(ExpectedIdentity);
        api.IdentityOutcomes.Enqueue(replacement);
        var probe = new ExplorerResponsivenessProbe(api);

        var result = probe.Sample(ExpectedIdentity);

        Assert.Equal(ExplorerResponsivenessStatus.TransientFailure, result.Status);
        Assert.Equal(
            ExplorerResponsivenessFailureReason.IdentityMismatchAfterSample,
            result.FailureReason);
        Assert.Single(api.Sends);
    }

    [Fact]
    public void Expected_win32_failures_are_typed_and_identity_is_still_revalidated()
    {
        var api = new FakeResponsivenessApi(ExpectedIdentity, [new IntPtr(11)]);
        api.FindExceptions.Enqueue(new Win32Exception(5));
        var probe = new ExplorerResponsivenessProbe(api);

        var result = probe.Sample(ExpectedIdentity);

        Assert.Equal(ExplorerResponsivenessStatus.TransientFailure, result.Status);
        Assert.Equal(
            ExplorerResponsivenessFailureReason.WindowEnumerationFailed,
            result.FailureReason);
        Assert.Equal(5, result.NativeErrorCode);
        Assert.Equal([42, 42], api.IdentityReads);
    }

    [Fact]
    public void Programming_exceptions_are_not_folded_into_responsiveness_failures()
    {
        var api = new FakeResponsivenessApi(ExpectedIdentity, [new IntPtr(11)]);
        api.FindExceptions.Enqueue(new InvalidOperationException("fake programming error"));
        var probe = new ExplorerResponsivenessProbe(api);

        Assert.Throws<InvalidOperationException>(() => probe.Sample(ExpectedIdentity));

        var next = probe.Sample(ExpectedIdentity);
        Assert.Equal(ExplorerResponsivenessStatus.Responsive, next.Status);
        Assert.Equal(0, next.ConsecutiveFailures);
    }

    [Fact]
    public void Three_consecutive_failures_latch_unsafe_with_the_triggering_diagnostic()
    {
        var api = new FakeResponsivenessApi(ExpectedIdentity, [new IntPtr(11)]);
        api.SendOutcomes.Enqueue(false);
        api.SendOutcomes.Enqueue(false);
        api.SendOutcomes.Enqueue(false);
        var probe = new ExplorerResponsivenessProbe(api);

        Assert.Equal(
            ExplorerResponsivenessStatus.TransientFailure,
            probe.Sample(ExpectedIdentity).Status);
        Assert.Equal(
            ExplorerResponsivenessStatus.TransientFailure,
            probe.Sample(ExpectedIdentity).Status);
        var unsafeResult = probe.Sample(ExpectedIdentity);

        Assert.Equal(ExplorerResponsivenessStatus.Unsafe, unsafeResult.Status);
        Assert.Equal(3, unsafeResult.ConsecutiveFailures);
        Assert.Equal(
            ExplorerResponsivenessFailureReason.MessageTimedOut,
            unsafeResult.FailureReason);

        var identityReadCount = api.IdentityReads.Count;
        var sendCount = api.Sends.Count;
        var latched = probe.Sample(ExpectedIdentity);
        Assert.Equal(ExplorerResponsivenessStatus.Unsafe, latched.Status);
        Assert.Equal(ExplorerResponsivenessFailureReason.MessageTimedOut, latched.FailureReason);
        Assert.Equal(identityReadCount, api.IdentityReads.Count);
        Assert.Equal(sendCount, api.Sends.Count);
    }

    [Fact]
    public void A_success_resets_the_consecutive_failure_and_diagnostic()
    {
        var api = new FakeResponsivenessApi(ExpectedIdentity, [new IntPtr(11)]);
        api.SendOutcomes.Enqueue(false);
        api.SendOutcomes.Enqueue(true);
        api.SendOutcomes.Enqueue(false);
        var probe = new ExplorerResponsivenessProbe(api);

        Assert.Equal(1, probe.Sample(ExpectedIdentity).ConsecutiveFailures);
        var success = probe.Sample(ExpectedIdentity);
        var third = probe.Sample(ExpectedIdentity);

        Assert.Equal(0, success.ConsecutiveFailures);
        Assert.Equal(ExplorerResponsivenessFailureReason.None, success.FailureReason);
        Assert.Equal(ExplorerResponsivenessStatus.TransientFailure, third.Status);
        Assert.Equal(1, third.ConsecutiveFailures);
    }

    [Fact]
    public void A_missing_window_has_a_specific_failure_reason()
    {
        var probe = new ExplorerResponsivenessProbe(
            new FakeResponsivenessApi(ExpectedIdentity, []));

        var result = probe.Sample(ExpectedIdentity);

        Assert.Equal(1, result.ConsecutiveFailures);
        Assert.Equal(
            ExplorerResponsivenessFailureReason.NoTaskbarWindows,
            result.FailureReason);
    }

    [Fact]
    public void One_hung_secondary_taskbar_makes_the_sample_fail_after_revalidation()
    {
        var api = new FakeResponsivenessApi(
            ExpectedIdentity,
            [new IntPtr(11), new IntPtr(12)]);
        api.SendOutcomes.Enqueue(true);
        api.SendOutcomes.Enqueue(false);
        var probe = new ExplorerResponsivenessProbe(api);

        var result = probe.Sample(ExpectedIdentity);

        Assert.Equal(ExplorerResponsivenessStatus.TransientFailure, result.Status);
        Assert.Equal(1, result.ConsecutiveFailures);
        Assert.Equal(
            ExplorerResponsivenessFailureReason.MessageTimedOut,
            result.FailureReason);
        Assert.Equal([42, 42], api.IdentityReads);
    }

    [Fact]
    public async Task Concurrent_samples_are_serialized_across_the_entire_boundary_sequence()
    {
        var api = new FakeResponsivenessApi(ExpectedIdentity, [new IntPtr(11)])
        {
            BoundaryDelayMilliseconds = 10,
        };
        var probe = new ExplorerResponsivenessProbe(api);
        using var start = new ManualResetEventSlim(initialState: false);
        var tasks = Enumerable.Range(0, 4)
            .Select(_ => Task.Factory.StartNew(
                () =>
                {
                    start.Wait();
                    return probe.Sample(ExpectedIdentity);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        start.Set();
        var results = await Task.WhenAll(tasks);

        Assert.All(
            results,
            result => Assert.Equal(ExplorerResponsivenessStatus.Responsive, result.Status));
        Assert.Equal(1, api.MaximumConcurrentBoundaryCalls);
    }

    [Fact]
    public void Native_enumeration_rejects_a_failed_window_process_id_query()
    {
        var windowApi = new FakeNativeExplorerWindowApi([new IntPtr(11)])
        {
            WindowClass = "Shell_TrayWnd",
            WindowThreadId = 0,
            OwnerProcessId = 42,
            LastError = 1_400,
        };
        var api = new NativeExplorerResponsivenessApi(
            new FakeIdentityReader(ExpectedIdentity),
            windowApi);

        var exception = Assert.Throws<Win32Exception>(() => api.FindTaskbarWindows(42));

        Assert.Equal(1_400, exception.NativeErrorCode);
    }

    [Fact]
    public void Identity_rejects_an_empty_sid_or_zero_creation_time()
    {
        Assert.Throws<ArgumentException>(() => new ExplorerResponsivenessIdentity(
            42,
            ExpectedIdentity.CreationTimeFileTime100Nanoseconds,
            3,
            string.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExplorerResponsivenessIdentity(
            42,
            0,
            3,
            ExpectedIdentity.UserSid));
    }

    public static IEnumerable<object[]> MismatchedIdentities()
    {
        yield return ["process"];
        yield return ["creation"];
        yield return ["session"];
        yield return ["sid"];
    }

    private static void AssertSend(SendCall send, IntPtr expectedWindow)
    {
        Assert.Equal(expectedWindow, send.Window);
        Assert.Equal(0U, send.Message);
        Assert.Equal(2_000U, send.TimeoutMilliseconds);
    }

    private sealed class FakeResponsivenessApi : IExplorerResponsivenessApi
    {
        private readonly object syncRoot = new();
        private readonly ExplorerResponsivenessIdentity defaultIdentity;
        private readonly IReadOnlyList<IntPtr> windows;
        private readonly List<int> identityReads = [];
        private readonly List<SendCall> sends = [];
        private int activeBoundaryCalls;
        private int maximumConcurrentBoundaryCalls;

        internal FakeResponsivenessApi(
            ExplorerResponsivenessIdentity defaultIdentity,
            IReadOnlyList<IntPtr> windows)
        {
            this.defaultIdentity = defaultIdentity;
            this.windows = windows;
        }

        internal Queue<object> IdentityOutcomes { get; } = new();

        internal Queue<Exception> FindExceptions { get; } = new();

        internal Queue<object> SendOutcomes { get; } = new();

        internal int BoundaryDelayMilliseconds { get; init; }

        internal int FindCallCount { get; private set; }

        internal int MaximumConcurrentBoundaryCalls =>
            Volatile.Read(ref maximumConcurrentBoundaryCalls);

        internal IReadOnlyList<int> IdentityReads
        {
            get
            {
                lock (syncRoot)
                {
                    return identityReads.ToArray();
                }
            }
        }

        internal IReadOnlyList<SendCall> Sends
        {
            get
            {
                lock (syncRoot)
                {
                    return sends.ToArray();
                }
            }
        }

        public ExplorerResponsivenessIdentity ReadExplorerIdentity(int processId) =>
            InBoundary(
                () =>
                {
                    lock (syncRoot)
                    {
                        identityReads.Add(processId);
                        if (IdentityOutcomes.TryDequeue(out var outcome))
                        {
                            if (outcome is Exception exception)
                            {
                                throw exception;
                            }

                            return (ExplorerResponsivenessIdentity)outcome;
                        }

                        return defaultIdentity;
                    }
                });

        public IReadOnlyList<IntPtr> FindTaskbarWindows(int expectedExplorerProcessId) =>
            InBoundary(
                () =>
                {
                    lock (syncRoot)
                    {
                        FindCallCount++;
                        if (FindExceptions.TryDequeue(out var exception))
                        {
                            throw exception;
                        }

                        return windows;
                    }
                });

        public bool SendMessageTimeout(
            IntPtr window,
            uint message,
            uint timeoutMilliseconds) =>
            InBoundary(
                () =>
                {
                    lock (syncRoot)
                    {
                        sends.Add(new SendCall(window, message, timeoutMilliseconds));
                        if (!SendOutcomes.TryDequeue(out var outcome))
                        {
                            return true;
                        }

                        if (outcome is Exception exception)
                        {
                            throw exception;
                        }

                        return (bool)outcome;
                    }
                });

        private T InBoundary<T>(Func<T> operation)
        {
            var active = Interlocked.Increment(ref activeBoundaryCalls);
            var maximum = Volatile.Read(ref maximumConcurrentBoundaryCalls);
            while (active > maximum)
            {
                var observed = Interlocked.CompareExchange(
                    ref maximumConcurrentBoundaryCalls,
                    active,
                    maximum);
                if (observed == maximum)
                {
                    break;
                }

                maximum = observed;
            }

            try
            {
                if (BoundaryDelayMilliseconds > 0)
                {
                    Thread.Sleep(BoundaryDelayMilliseconds);
                }

                return operation();
            }
            finally
            {
                _ = Interlocked.Decrement(ref activeBoundaryCalls);
            }
        }
    }

    private sealed class FakeIdentityReader(ExplorerResponsivenessIdentity identity) :
        IExplorerResponsivenessIdentityReader
    {
        public ExplorerResponsivenessIdentity Read(int processId)
        {
            Assert.Equal(identity.ProcessId, processId);
            return identity;
        }
    }

    private sealed class FakeNativeExplorerWindowApi(IReadOnlyList<IntPtr> windows) :
        INativeExplorerWindowApi
    {
        internal string WindowClass { get; init; } = "Shell_TrayWnd";

        internal uint WindowThreadId { get; init; } = 1;

        internal uint OwnerProcessId { get; init; } = 42;

        public int LastError { get; init; }

        public bool EnumWindows(NativeEnumWindowsCallback callback, IntPtr data)
        {
            foreach (var window in windows)
            {
                if (!callback(window, data))
                {
                    return false;
                }
            }

            return true;
        }

        public int GetClassName(IntPtr window, StringBuilder className, int maximumCount)
        {
            _ = window;
            Assert.True(WindowClass.Length < maximumCount);
            className.Append(WindowClass);
            return WindowClass.Length;
        }

        public uint GetWindowThreadProcessId(IntPtr window, out uint processId)
        {
            _ = window;
            processId = OwnerProcessId;
            return WindowThreadId;
        }

        public bool SendMessageTimeout(
            IntPtr window,
            uint message,
            uint timeoutMilliseconds)
        {
            _ = window;
            _ = message;
            _ = timeoutMilliseconds;
            return true;
        }
    }

    private sealed record SendCall(
        IntPtr Window,
        uint Message,
        uint TimeoutMilliseconds);
}
