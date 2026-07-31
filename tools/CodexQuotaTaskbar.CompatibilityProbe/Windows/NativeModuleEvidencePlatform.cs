using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexQuotaTaskbar.CompatibilityProbe.Windows;

internal sealed class NativeModuleEvidencePlatform : IModuleEvidencePlatform
{
    private const uint MonitorDefaultToNull = 0;
    private const uint MonitorInfoPrimary = 0x00000001;
    private const uint TokenQuery = 0x0008;
    private const ushort ImageFileMachineUnknown = 0x0000;
    private const ushort ImageFileMachineI386 = 0x014c;
    private const ushort ImageFileMachineAmd64 = 0x8664;
    private const ushort ImageFileMachineArm64 = 0xaa64;
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    public int CurrentSessionId
    {
        get
        {
            using var current = Process.GetCurrentProcess();
            return current.SessionId;
        }
    }

    public string CurrentUserSid
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? throw new InvalidOperationException("Current user SID is unavailable.");
        }
    }

    public string ExpectedExplorerPath => Path.GetFullPath(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "explorer.exe"));

    public int GetShellWindowOwnerProcessId()
    {
        var shellWindow = GetShellWindow();
        if (shellWindow == IntPtr.Zero)
        {
            throw new Win32Exception("The shell window is unavailable.");
        }

        _ = GetWindowThreadProcessId(shellWindow, out var processId);
        if (processId == 0 || processId > int.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return (int)processId;
    }

    public IReadOnlyList<RawTaskbarHost> EnumerateTaskbarHosts()
    {
        var hosts = new List<RawTaskbarHost>();
        Exception? failure = null;
        EnumWindowsCallback callback = (window, _) =>
        {
            try
            {
                var className = ReadWindowClass(window);
                if (className is not "Shell_TrayWnd" and not "Shell_SecondaryTrayWnd")
                {
                    return true;
                }

                GetWindowThreadProcessId(window, out var processId);
                var monitor = MonitorFromWindow(window, MonitorDefaultToNull);
                if (processId == 0 || processId > int.MaxValue || monitor == IntPtr.Zero ||
                    !GetWindowRect(window, out var rectangle))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                hosts.Add(new RawTaskbarHost(
                    window.ToInt64(),
                    className,
                    (int)processId,
                    monitor.ToInt64(),
                    new RawWindowBounds(
                        rectangle.Left,
                        rectangle.Top,
                        rectangle.Right,
                        rectangle.Bottom)));
                return true;
            }
            catch (Exception exception)
            {
                failure = exception;
                return false;
            }
        };

        var completed = EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        if (failure is not null)
        {
            throw failure;
        }

        if (!completed)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return hosts;
    }

    public IReadOnlyList<RawDisplayMonitor> EnumerateDisplayMonitors()
    {
        var monitors = new List<RawDisplayMonitor>();
        Exception? failure = null;
        MonitorEnumCallback callback = (
            IntPtr monitor,
            IntPtr _,
            ref NativeRectangle _,
            IntPtr _) =>
        {
            try
            {
                var information = new NativeMonitorInfo
                {
                    Size = (uint)Marshal.SizeOf<NativeMonitorInfo>(),
                };
                if (!GetMonitorInfo(monitor, ref information))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                monitors.Add(new RawDisplayMonitor(
                    monitor.ToInt64(),
                    new RawWindowBounds(
                        information.Monitor.Left,
                        information.Monitor.Top,
                        information.Monitor.Right,
                        information.Monitor.Bottom),
                    (information.Flags & MonitorInfoPrimary) != 0));
                return true;
            }
            catch (Exception exception)
            {
                failure = exception;
                return false;
            }
        };

        var completed = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        if (failure is not null)
        {
            throw failure;
        }

        if (!completed)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return monitors;
    }

    public IExplorerProcessLease OpenExplorer(int processId) =>
        new NativeExplorerProcessLease(Process.GetProcessById(processId));

    public IStableFileCaptureLease OpenStableFile(string path)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
        }
        catch (Exception exception)
        {
            throw new FileEvidenceBoundaryException(
                EvidenceFailureReason.FileOpenFailed,
                exception);
        }

        try
        {
            string canonicalPath;
            try
            {
                canonicalPath = ReadFinalPath(stream.SafeFileHandle);
            }
            catch (Exception exception)
            {
                throw new FileEvidenceBoundaryException(
                    EvidenceFailureReason.FinalPathReadFailed,
                    exception);
            }

            StableFileIdentity before;
            try
            {
                before = ReadFileIdentity(stream.SafeFileHandle);
            }
            catch (Exception exception)
            {
                throw new FileEvidenceBoundaryException(
                    EvidenceFailureReason.FileIdentityReadFailed,
                    exception);
            }

            string? version;
            try
            {
                version = FileVersionInfo.GetVersionInfo(canonicalPath).FileVersion;
            }
            catch (Exception exception)
            {
                throw new FileEvidenceBoundaryException(
                    EvidenceFailureReason.FileVersionReadFailed,
                    exception);
            }

            FileStream versionPathStream;
            try
            {
                versionPathStream = new FileStream(
                    canonicalPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
            }
            catch (Exception exception)
            {
                throw new FileEvidenceBoundaryException(
                    EvidenceFailureReason.FileOpenFailed,
                    exception);
            }

            StableFileIdentity versionPathIdentity;
            using (versionPathStream)
            {
                try
                {
                    versionPathIdentity = ReadFileIdentity(versionPathStream.SafeFileHandle);
                }
                catch (Exception exception)
                {
                    throw new FileEvidenceBoundaryException(
                        EvidenceFailureReason.FileIdentityReadFailed,
                        exception);
                }
            }

            string hash;
            try
            {
                hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }
            catch (Exception exception)
            {
                throw new FileEvidenceBoundaryException(
                    EvidenceFailureReason.FileHashReadFailed,
                    exception);
            }

            return new NativeStableFileCaptureLease(
                stream,
                canonicalPath,
                Path.GetFileName(canonicalPath),
                version,
                hash,
                before,
                versionPathIdentity);
        }
        catch
        {
            try
            {
                stream.Dispose();
            }
            catch (Exception)
            {
                // Preserve the original bounded evidence failure.
            }

            throw;
        }
    }

    public IDpiAwarenessScope EnterPerMonitorV2DpiAwareness() => new DpiAwarenessScope();

    public int GetDpiForWindow(long windowToken)
    {
        var dpi = NativeGetDpiForWindow(new IntPtr(windowToken));
        if (dpi == 0 || dpi > int.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return (int)dpi;
    }

    private static string ReadWindowClass(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        var length = GetClassName(window, buffer, buffer.Capacity);
        if (length <= 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return buffer.ToString(0, length);
    }

    private static string ReadFinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var path = buffer.ToString();
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            path = path[4..];
        }

        return Path.GetFullPath(path);
    }

    private static StableFileIdentity ReadFileIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var fileId = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        var length = ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
        var lastWrite = ((long)information.LastWriteTime.High << 32) |
            information.LastWriteTime.Low;
        return new StableFileIdentity(
            information.VolumeSerialNumber,
            fileId,
            length,
            lastWrite);
    }

    private sealed class NativeExplorerProcessLease : IExplorerProcessLease
    {
        private readonly Process process;

        internal NativeExplorerProcessLease(Process process)
        {
            this.process = process;
        }

        public RawExplorerProcess Describe()
        {
            process.Refresh();
            if (process.HasExited)
            {
                return new RawExplorerProcess(
                    process.Id,
                    0,
                    0,
                    string.Empty,
                    Architecture.X86,
                    string.Empty,
                    HasExited: true);
            }

            return new RawExplorerProcess(
                process.Id,
                process.StartTime.ToUniversalTime().Ticks,
                process.SessionId,
                ReadProcessUserSid(process.SafeHandle),
                ReadArchitecture(process.SafeHandle),
                ReadProcessImagePath(process.SafeHandle),
                HasExited: false);
        }

        public IReadOnlyList<string> EnumerateModulePaths()
        {
            process.Refresh();
            if (process.HasExited)
            {
                throw new InvalidOperationException("Explorer exited during module enumeration.");
            }

            var paths = new List<string>();
            foreach (ProcessModule module in process.Modules)
            {
                using (module)
                {
                    if (!string.IsNullOrWhiteSpace(module.FileName))
                    {
                        paths.Add(module.FileName);
                    }
                }
            }

            return paths;
        }

        public void Dispose() => process.Dispose();

        private static string ReadProcessUserSid(SafeProcessHandle processHandle)
        {
            if (!OpenProcessToken(processHandle, TokenQuery, out var token))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            {
                return identity.User?.Value ??
                    throw new InvalidOperationException("Explorer user SID is unavailable.");
            }
        }

        private static Architecture ReadArchitecture(SafeProcessHandle processHandle)
        {
            if (!IsWow64Process2(processHandle, out var processMachine, out var nativeMachine))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var machine = processMachine == ImageFileMachineUnknown ? nativeMachine : processMachine;
            return machine switch
            {
                ImageFileMachineAmd64 => Architecture.X64,
                ImageFileMachineArm64 => Architecture.Arm64,
                ImageFileMachineI386 => Architecture.X86,
                _ => throw new InvalidOperationException("Explorer architecture is unsupported."),
            };
        }

        private static string ReadProcessImagePath(SafeProcessHandle processHandle)
        {
            var capacity = 32768;
            var buffer = new StringBuilder(capacity);
            if (!QueryFullProcessImageName(processHandle, 0, buffer, ref capacity))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return Path.GetFullPath(buffer.ToString());
        }
    }

    private sealed class NativeStableFileCaptureLease : IStableFileCaptureLease
    {
        private readonly FileStream stream;
        private readonly string canonicalPath;
        private readonly string fileName;
        private readonly string? version;
        private readonly string hash;
        private readonly StableFileIdentity before;
        private readonly StableFileIdentity versionPathIdentity;
        private bool completed;
        private bool disposed;

        internal NativeStableFileCaptureLease(
            FileStream stream,
            string canonicalPath,
            string fileName,
            string? version,
            string hash,
            StableFileIdentity before,
            StableFileIdentity versionPathIdentity)
        {
            this.stream = stream;
            this.canonicalPath = canonicalPath;
            this.fileName = fileName;
            this.version = version;
            this.hash = hash;
            this.before = before;
            this.versionPathIdentity = versionPathIdentity;
        }

        public StableFileCapture Complete()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (completed)
            {
                throw new InvalidOperationException("The file-evidence lease is already complete.");
            }

            StableFileIdentity after;
            try
            {
                after = ReadFileIdentity(stream.SafeFileHandle);
            }
            catch (Exception exception)
            {
                throw new FileEvidenceBoundaryException(
                    EvidenceFailureReason.FileIdentityReadFailed,
                    exception);
            }

            completed = true;
            return new StableFileCapture(
                canonicalPath,
                fileName,
                version,
                hash,
                before,
                versionPathIdentity,
                after);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            stream.Dispose();
            disposed = true;
        }
    }

    private sealed class DpiAwarenessScope : IDpiAwarenessScope
    {
        private readonly IntPtr previousContext;
        private bool disposed;

        internal DpiAwarenessScope()
        {
            previousContext = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
            if (previousContext == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        public bool RestoreSucceeded { get; private set; }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            RestoreSucceeded = SetThreadDpiAwarenessContext(previousContext) != IntPtr.Zero;
            disposed = true;
        }
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr data);

    private delegate bool MonitorEnumCallback(
        IntPtr monitor,
        IntPtr deviceContext,
        ref NativeRectangle monitorRectangle,
        IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRectangle
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public uint Size;
        public NativeRectangle Monitor;
        public NativeRectangle WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeFileTime
    {
        public readonly uint Low;
        public readonly uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ByHandleFileInformation
    {
        public readonly uint FileAttributes;
        public readonly NativeFileTime CreationTime;
        public readonly NativeFileTime LastAccessTime;
        public readonly NativeFileTime LastWriteTime;
        public readonly uint VolumeSerialNumber;
        public readonly uint FileSizeHigh;
        public readonly uint FileSizeLow;
        public readonly uint NumberOfLinks;
        public readonly uint FileIndexHigh;
        public readonly uint FileIndexLow;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        IntPtr deviceContext,
        IntPtr clipRectangle,
        MonitorEnumCallback callback,
        IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo information);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRectangle rectangle);

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow", SetLastError = true)]
    private static extern uint NativeGetDpiForWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        StringBuilder imagePath,
        ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(
        SafeProcessHandle process,
        out ushort processMachine,
        out ushort nativeMachine);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        SafeProcessHandle process,
        uint desiredAccess,
        out SafeAccessTokenHandle token);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);
}
