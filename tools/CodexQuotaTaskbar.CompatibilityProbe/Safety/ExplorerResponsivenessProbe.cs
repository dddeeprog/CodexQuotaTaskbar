using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using CodexQuotaTaskbar.BridgeControl.Injection;

namespace CodexQuotaTaskbar.CompatibilityProbe.Safety;

internal enum ExplorerResponsivenessStatus
{
    Responsive,
    TransientFailure,
    Unsafe,
}

internal enum ExplorerResponsivenessFailureReason
{
    None,
    IdentityReadFailedBeforeSample,
    IdentityMismatchBeforeSample,
    NoTaskbarWindows,
    WindowEnumerationFailed,
    MessageTimedOut,
    MessageSendFailed,
    IdentityReadFailedAfterSample,
    IdentityMismatchAfterSample,
}

internal sealed record ExplorerResponsivenessIdentity
{
    internal ExplorerResponsivenessIdentity(
        int processId,
        ulong creationTimeFileTime100Nanoseconds,
        uint sessionId,
        string userSid)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        if (creationTimeFileTime100Nanoseconds == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(creationTimeFileTime100Nanoseconds));
        }

        ProcessId = processId;
        CreationTimeFileTime100Nanoseconds = creationTimeFileTime100Nanoseconds;
        SessionId = sessionId;
        UserSid = NormalizeSid(userSid);
    }

    internal int ProcessId { get; init; }

    internal ulong CreationTimeFileTime100Nanoseconds { get; init; }

    internal uint SessionId { get; init; }

    internal string UserSid { get; init; }

    internal bool IsValid
    {
        get
        {
            if (ProcessId <= 0 ||
                CreationTimeFileTime100Nanoseconds == 0 ||
                string.IsNullOrWhiteSpace(UserSid))
            {
                return false;
            }

            try
            {
                return string.Equals(
                    new SecurityIdentifier(UserSid).Value,
                    UserSid,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }

    private static string NormalizeSid(string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        try
        {
            return new SecurityIdentifier(userSid).Value;
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                "The Explorer user SID is invalid.",
                nameof(userSid),
                exception);
        }
    }
}

internal readonly record struct ExplorerResponsivenessResult(
    ExplorerResponsivenessStatus Status,
    int ConsecutiveFailures,
    ExplorerResponsivenessFailureReason FailureReason,
    int NativeErrorCode);

internal sealed class ExplorerResponsivenessProbe
{
    private const uint WmNull = 0;
    private const uint TimeoutMilliseconds = 2_000;
    private const int UnsafeFailureCount = 3;
    private readonly IExplorerResponsivenessApi api;
    private readonly object syncRoot = new();
    private int consecutiveFailures;
    private bool unsafeLatched;
    private ExplorerResponsivenessFailureReason lastFailureReason;
    private int lastNativeErrorCode;

    internal ExplorerResponsivenessProbe()
        : this(new NativeExplorerResponsivenessApi())
    {
    }

    internal ExplorerResponsivenessProbe(IExplorerResponsivenessApi api) =>
        this.api = api ?? throw new ArgumentNullException(nameof(api));

    internal ExplorerResponsivenessResult Sample(
        ExplorerResponsivenessIdentity expectedIdentity)
    {
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        if (!expectedIdentity.IsValid)
        {
            throw new ArgumentException(
                "The expected Explorer identity is invalid.",
                nameof(expectedIdentity));
        }

        lock (syncRoot)
        {
            if (unsafeLatched)
            {
                return Result(
                    ExplorerResponsivenessStatus.Unsafe,
                    lastFailureReason,
                    lastNativeErrorCode);
            }

            var observation = Observe(expectedIdentity);
            if (observation.FailureReason == ExplorerResponsivenessFailureReason.None)
            {
                consecutiveFailures = 0;
                lastFailureReason = ExplorerResponsivenessFailureReason.None;
                lastNativeErrorCode = 0;
                return Result(
                    ExplorerResponsivenessStatus.Responsive,
                    lastFailureReason,
                    lastNativeErrorCode);
            }

            consecutiveFailures++;
            lastFailureReason = observation.FailureReason;
            lastNativeErrorCode = observation.NativeErrorCode;
            if (consecutiveFailures >= UnsafeFailureCount)
            {
                consecutiveFailures = UnsafeFailureCount;
                unsafeLatched = true;
                return Result(
                    ExplorerResponsivenessStatus.Unsafe,
                    lastFailureReason,
                    lastNativeErrorCode);
            }

            return Result(
                ExplorerResponsivenessStatus.TransientFailure,
                lastFailureReason,
                lastNativeErrorCode);
        }
    }

    private ObservationResult Observe(ExplorerResponsivenessIdentity expectedIdentity)
    {
        ExplorerResponsivenessIdentity before;
        try
        {
            before = api.ReadExplorerIdentity(expectedIdentity.ProcessId);
        }
        catch (Win32Exception exception)
        {
            return ObservationResult.Failed(
                ExplorerResponsivenessFailureReason.IdentityReadFailedBeforeSample,
                exception.NativeErrorCode);
        }

        if (before != expectedIdentity)
        {
            return ObservationResult.Failed(
                ExplorerResponsivenessFailureReason.IdentityMismatchBeforeSample);
        }

        var operation = ObserveTaskbarWindows(expectedIdentity.ProcessId);

        ExplorerResponsivenessIdentity after;
        try
        {
            after = api.ReadExplorerIdentity(expectedIdentity.ProcessId);
        }
        catch (Win32Exception exception)
        {
            return ObservationResult.Failed(
                ExplorerResponsivenessFailureReason.IdentityReadFailedAfterSample,
                exception.NativeErrorCode);
        }

        return after == expectedIdentity
            ? operation
            : ObservationResult.Failed(
                ExplorerResponsivenessFailureReason.IdentityMismatchAfterSample);
    }

    private ObservationResult ObserveTaskbarWindows(int expectedExplorerProcessId)
    {
        IReadOnlyList<IntPtr> windows;
        try
        {
            windows = api.FindTaskbarWindows(expectedExplorerProcessId);
        }
        catch (Win32Exception exception)
        {
            return ObservationResult.Failed(
                ExplorerResponsivenessFailureReason.WindowEnumerationFailed,
                exception.NativeErrorCode);
        }

        if (windows.Count == 0)
        {
            return ObservationResult.Failed(
                ExplorerResponsivenessFailureReason.NoTaskbarWindows);
        }

        var failure = ObservationResult.Succeeded;
        foreach (var window in windows)
        {
            if (window == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "The responsiveness boundary returned a null taskbar window.");
            }

            try
            {
                if (!api.SendMessageTimeout(window, WmNull, TimeoutMilliseconds))
                {
                    failure = ObservationResult.Failed(
                        ExplorerResponsivenessFailureReason.MessageTimedOut);
                }
            }
            catch (Win32Exception exception)
            {
                return ObservationResult.Failed(
                    ExplorerResponsivenessFailureReason.MessageSendFailed,
                    exception.NativeErrorCode);
            }
        }

        return failure;
    }

    private ExplorerResponsivenessResult Result(
        ExplorerResponsivenessStatus status,
        ExplorerResponsivenessFailureReason failureReason,
        int nativeErrorCode) =>
        new(status, consecutiveFailures, failureReason, nativeErrorCode);

    private readonly record struct ObservationResult(
        ExplorerResponsivenessFailureReason FailureReason,
        int NativeErrorCode)
    {
        internal static ObservationResult Succeeded { get; } =
            new(ExplorerResponsivenessFailureReason.None, 0);

        internal static ObservationResult Failed(
            ExplorerResponsivenessFailureReason reason,
            int nativeErrorCode = 0) =>
            new(reason, nativeErrorCode);
    }
}

internal interface IExplorerResponsivenessApi
{
    ExplorerResponsivenessIdentity ReadExplorerIdentity(int processId);

    IReadOnlyList<IntPtr> FindTaskbarWindows(int expectedExplorerProcessId);

    bool SendMessageTimeout(IntPtr window, uint message, uint timeoutMilliseconds);
}

internal interface IExplorerResponsivenessIdentityReader
{
    ExplorerResponsivenessIdentity Read(int processId);
}

internal sealed class ExplorerProcessResponsivenessIdentityReader :
    IExplorerResponsivenessIdentityReader
{
    private const int ErrorInvalidHandle = 6;
    private const int ErrorInvalidData = 13;
    private readonly IExplorerProcessApi processApi;

    internal ExplorerProcessResponsivenessIdentityReader()
        : this(new Win32ExplorerProcessApi())
    {
    }

    internal ExplorerProcessResponsivenessIdentityReader(IExplorerProcessApi processApi) =>
        this.processApi = processApi ?? throw new ArgumentNullException(nameof(processApi));

    public ExplorerResponsivenessIdentity Read(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        var open = processApi.OpenForCollection(processId);
        if (!open.Succeeded)
        {
            throw new Win32Exception(open.ErrorCode);
        }

        using var lease = open.Value!;
        var snapshotResult = lease.Snapshot();
        if (!snapshotResult.Succeeded)
        {
            throw new Win32Exception(snapshotResult.ErrorCode);
        }

        var snapshot = snapshotResult.Value!;
        if (snapshot.HasExited)
        {
            throw new Win32Exception(ErrorInvalidHandle);
        }

        if (snapshot.ProcessId != processId ||
            snapshot.CreationTimeFileTime100Nanoseconds == 0 ||
            string.IsNullOrWhiteSpace(snapshot.UserSid))
        {
            throw new Win32Exception(ErrorInvalidData);
        }

        try
        {
            return new ExplorerResponsivenessIdentity(
                snapshot.ProcessId,
                snapshot.CreationTimeFileTime100Nanoseconds,
                snapshot.SessionId,
                snapshot.UserSid);
        }
        catch (ArgumentException)
        {
            throw new Win32Exception(ErrorInvalidData);
        }
    }
}

internal delegate bool NativeEnumWindowsCallback(IntPtr window, IntPtr data);

internal interface INativeExplorerWindowApi
{
    int LastError { get; }

    bool EnumWindows(NativeEnumWindowsCallback callback, IntPtr data);

    int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

    uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    bool SendMessageTimeout(IntPtr window, uint message, uint timeoutMilliseconds);
}

internal sealed class NativeExplorerResponsivenessApi : IExplorerResponsivenessApi
{
    private const int ErrorInvalidData = 13;
    private readonly IExplorerResponsivenessIdentityReader identityReader;
    private readonly INativeExplorerWindowApi windowApi;

    internal NativeExplorerResponsivenessApi()
        : this(
            new ExplorerProcessResponsivenessIdentityReader(),
            new Win32NativeExplorerWindowApi())
    {
    }

    internal NativeExplorerResponsivenessApi(
        IExplorerResponsivenessIdentityReader identityReader,
        INativeExplorerWindowApi windowApi)
    {
        this.identityReader = identityReader ??
            throw new ArgumentNullException(nameof(identityReader));
        this.windowApi = windowApi ?? throw new ArgumentNullException(nameof(windowApi));
    }

    public ExplorerResponsivenessIdentity ReadExplorerIdentity(int processId) =>
        identityReader.Read(processId);

    public IReadOnlyList<IntPtr> FindTaskbarWindows(int expectedExplorerProcessId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedExplorerProcessId);
        var windows = new List<IntPtr>();
        Exception? failure = null;
        NativeEnumWindowsCallback callback = (window, _) =>
        {
            try
            {
                var className = new StringBuilder(256);
                var length = windowApi.GetClassName(window, className, className.Capacity);
                if (length == 0)
                {
                    throw LastWindowError();
                }

                var windowClass = className.ToString(0, length);
                if (windowClass is not "Shell_TrayWnd" and not "Shell_SecondaryTrayWnd")
                {
                    return true;
                }

                var threadId = windowApi.GetWindowThreadProcessId(window, out var processId);
                if (threadId == 0)
                {
                    throw LastWindowError();
                }

                if (processId == checked((uint)expectedExplorerProcessId))
                {
                    windows.Add(window);
                }

                return true;
            }
            catch (Exception exception)
            {
                failure = exception;
                return false;
            }
        };

        var completed = windowApi.EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        if (failure is not null)
        {
            throw failure;
        }

        if (!completed)
        {
            throw LastWindowError();
        }

        return windows.AsReadOnly();
    }

    public bool SendMessageTimeout(
        IntPtr window,
        uint message,
        uint timeoutMilliseconds) =>
        windowApi.SendMessageTimeout(window, message, timeoutMilliseconds);

    private Win32Exception LastWindowError() =>
        new(windowApi.LastError == 0 ? ErrorInvalidData : windowApi.LastError);
}

internal sealed class Win32NativeExplorerWindowApi : INativeExplorerWindowApi
{
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint SmtoErrorOnExit = 0x0020;

    public int LastError => Marshal.GetLastWin32Error();

    public bool EnumWindows(NativeEnumWindowsCallback callback, IntPtr data) =>
        NativeEnumWindows(callback, data);

    public int GetClassName(IntPtr window, StringBuilder className, int maximumCount) =>
        NativeGetClassName(window, className, maximumCount);

    public uint GetWindowThreadProcessId(IntPtr window, out uint processId) =>
        NativeGetWindowThreadProcessId(window, out processId);

    public bool SendMessageTimeout(
        IntPtr window,
        uint message,
        uint timeoutMilliseconds) =>
        NativeSendMessageTimeout(
            window,
            message,
            IntPtr.Zero,
            IntPtr.Zero,
            SmtoAbortIfHung | SmtoErrorOnExit,
            timeoutMilliseconds,
            out _) != IntPtr.Zero;

    [DllImport("user32.dll", EntryPoint = "EnumWindows", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeEnumWindows(
        NativeEnumWindowsCallback callback,
        IntPtr data);

    [DllImport(
        "user32.dll",
        EntryPoint = "GetClassNameW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern int NativeGetClassName(
        IntPtr window,
        StringBuilder className,
        int maximumCount);

    [DllImport(
        "user32.dll",
        EntryPoint = "GetWindowThreadProcessId",
        SetLastError = true)]
    private static extern uint NativeGetWindowThreadProcessId(
        IntPtr window,
        out uint processId);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern IntPtr NativeSendMessageTimeout(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeoutMilliseconds,
        out UIntPtr result);
}
