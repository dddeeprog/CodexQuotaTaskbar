using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexQuotaTaskbar.BridgeControl.Injection;

internal interface IExplorerProcessApi
{
    uint CurrentSessionId { get; }

    string CurrentUserSid { get; }

    NativeResult<IExplorerReadLease> OpenForCollection(int processId);

    NativeResult<IExplorerMutationLease> OpenForInjection(int processId);

    NativeResult<LocalSystemExport> ResolveLocalSystemExport(
        string moduleName,
        string exportName);

    NativeResult<INamedEvent> TryOpenEvent(
        string eventName,
        NativeEventAccess access);

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal interface IExplorerReadLease : IDisposable
{
    int ProcessId { get; }

    NativeResult<ExplorerProcessSnapshot> Snapshot();

    NativeResult<IReadOnlyList<RemoteModule>> EnumerateModules();

    NativeWaitResult WaitForProcessExit(TimeSpan timeout);
}

internal interface IExplorerMutationLease : IExplorerReadLease
{
    NativeResult<IRemoteMemory> Allocate(nuint size);

    NativeResult WriteAll(IRemoteMemory memory, ReadOnlyMemory<byte> bytes);

    NativeResult<IRemoteThread> StartRemoteThread(
        ulong startAddress,
        IRemoteMemory? parameter);
}

internal interface IRemoteMemory : IDisposable
{
    ulong Address { get; }

    nuint Size { get; }

    void Abandon();
}

internal interface IRemoteThread : IDisposable
{
    NativeWaitResult Wait(TimeSpan timeout);
}

internal interface INamedEvent : IDisposable
{
    NativeWaitResult Wait(TimeSpan timeout);

    NativeResult Signal();
}

internal enum ExplorerProcessArchitecture
{
    Unknown,
    X86,
    X64,
    Arm64,
}

internal enum NativeEventAccess
{
    Wait,
    Signal,
}

internal enum NativeWaitKind
{
    Signaled,
    TimedOut,
    Abandoned,
    Failed,
}

internal readonly record struct NativeResult(bool Succeeded, int ErrorCode)
{
    internal static NativeResult Success() => new(true, 0);

    internal static NativeResult Failure(int errorCode) =>
        new(false, NativeErrors.Normalize(errorCode));
}

internal readonly record struct NativeResult<T>(
    bool Succeeded,
    T? Value,
    int ErrorCode)
    where T : class
{
    internal static NativeResult<T> Success(T value) =>
        new(true, value ?? throw new ArgumentNullException(nameof(value)), 0);

    internal static NativeResult<T> Failure(int errorCode) =>
        new(false, null, NativeErrors.Normalize(errorCode));
}

internal readonly record struct NativeWaitResult
{
    internal NativeWaitResult(
        NativeWaitKind kind,
        int errorCode,
        uint? exitCode = null)
    {
        Kind = kind;
        ErrorCode = errorCode;
        ExitCode = exitCode;
    }

    public NativeWaitKind Kind { get; }

    public int ErrorCode { get; }

    public uint? ExitCode { get; }

    public bool Succeeded => Kind is not NativeWaitKind.Failed;
}

internal sealed record ExplorerProcessSnapshot
{
    internal ExplorerProcessSnapshot(
        int processId,
        ulong creationTimeFileTime100Nanoseconds,
        uint sessionId,
        string userSid,
        ExplorerProcessArchitecture architecture,
        string canonicalImagePath,
        bool hasExited)
    {
        ProcessId = processId;
        CreationTimeFileTime100Nanoseconds = creationTimeFileTime100Nanoseconds;
        SessionId = sessionId;
        UserSid = userSid;
        Architecture = architecture;
        CanonicalImagePath = canonicalImagePath;
        HasExited = hasExited;
    }

    internal int ProcessId { get; }

    internal ulong CreationTimeFileTime100Nanoseconds { get; }

    internal uint SessionId { get; }

    internal string UserSid { get; }

    internal ExplorerProcessArchitecture Architecture { get; }

    internal string CanonicalImagePath { get; }

    internal bool HasExited { get; }
}

internal sealed record RemoteModule(
    string Path,
    ulong BaseAddress,
    uint Size);

internal sealed record LocalSystemExport(
    string ModulePath,
    uint RelativeVirtualAddress,
    uint ImageSize);

internal sealed class Win32ExplorerProcessApi : IExplorerProcessApi
{
    private const uint ProcessCreateThread = 0x0002;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;

    internal const uint CollectionAccessMask =
        ProcessVmRead |
        ProcessQueryInformation |
        ProcessQueryLimitedInformation |
        Synchronize;

    internal const uint InjectionAccessMask =
        ProcessCreateThread |
        ProcessVmOperation |
        ProcessVmRead |
        ProcessVmWrite |
        ProcessQueryInformation |
        ProcessQueryLimitedInformation |
        Synchronize;

    public uint CurrentSessionId
    {
        get
        {
            if (!NativeMethods.ProcessIdToSessionId(
                checked((uint)Environment.ProcessId),
                out var sessionId))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return sessionId;
        }
    }

    public string CurrentUserSid
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ??
                throw new InvalidOperationException("The current Windows identity has no SID.");
        }
    }

    public NativeResult<IExplorerReadLease> OpenForCollection(int processId)
    {
        var opened = OpenProcess(processId, CollectionAccessMask);
        if (!opened.Succeeded)
        {
            return NativeResult<IExplorerReadLease>.Failure(opened.ErrorCode);
        }

        return NativeResult<IExplorerReadLease>.Success(
            new Win32ExplorerReadLease(processId, opened.Value!));
    }

    public NativeResult<IExplorerMutationLease> OpenForInjection(int processId)
    {
        var opened = OpenProcess(processId, InjectionAccessMask);
        if (!opened.Succeeded)
        {
            return NativeResult<IExplorerMutationLease>.Failure(opened.ErrorCode);
        }

        return NativeResult<IExplorerMutationLease>.Success(
            new Win32ExplorerMutationLease(processId, opened.Value!));
    }

    public NativeResult<LocalSystemExport> ResolveLocalSystemExport(
        string moduleName,
        string exportName)
    {
        if (!IsSafeSystemModuleName(moduleName) ||
            string.IsNullOrWhiteSpace(exportName) ||
            exportName.IndexOf('\0') >= 0)
        {
            return NativeResult<LocalSystemExport>.Failure(NativeErrors.InvalidParameter);
        }

        using var requestedModule = NativeMethods.LoadLibraryEx(
            moduleName,
            0,
            NativeMethods.LoadLibrarySearchSystem32);
        if (requestedModule.IsInvalid)
        {
            return NativeResult<LocalSystemExport>.Failure(Marshal.GetLastWin32Error());
        }

        var exportAddress = NativeMethods.GetProcAddress(requestedModule, exportName);
        if (exportAddress == 0)
        {
            return NativeResult<LocalSystemExport>.Failure(Marshal.GetLastWin32Error());
        }

        if (!NativeMethods.GetModuleHandleFromAddress(
            NativeMethods.GetModuleHandleFromAddressFlags,
            exportAddress,
            out var ownerModule) ||
            ownerModule == 0)
        {
            return NativeResult<LocalSystemExport>.Failure(Marshal.GetLastWin32Error());
        }

        if (!NativeMethods.GetCurrentModuleInformation(
            NativeMethods.GetCurrentProcess(),
            ownerModule,
            out var moduleInformation,
            checked((uint)Marshal.SizeOf<ModuleInformation>())))
        {
            return NativeResult<LocalSystemExport>.Failure(Marshal.GetLastWin32Error());
        }

        var moduleBase = Pointer.ToUInt64(moduleInformation.BaseOfDll);
        var export = Pointer.ToUInt64(exportAddress);
        if (moduleBase == 0 ||
            moduleInformation.SizeOfImage == 0 ||
            export < moduleBase)
        {
            return NativeResult<LocalSystemExport>.Failure(NativeErrors.InvalidAddress);
        }

        var relativeAddress = export - moduleBase;
        if (relativeAddress >= moduleInformation.SizeOfImage ||
            relativeAddress > uint.MaxValue)
        {
            return NativeResult<LocalSystemExport>.Failure(NativeErrors.InvalidAddress);
        }

        var pathResult = ReadLocalModulePath(ownerModule);
        if (!pathResult.Succeeded)
        {
            return NativeResult<LocalSystemExport>.Failure(pathResult.ErrorCode);
        }

        return NativeResult<LocalSystemExport>.Success(
            new LocalSystemExport(
                pathResult.Value!,
                checked((uint)relativeAddress),
                moduleInformation.SizeOfImage));
    }

    public NativeResult<INamedEvent> TryOpenEvent(
        string eventName,
        NativeEventAccess access)
    {
        if (string.IsNullOrWhiteSpace(eventName) || eventName.IndexOf('\0') >= 0)
        {
            return NativeResult<INamedEvent>.Failure(NativeErrors.InvalidParameter);
        }

        var desiredAccess = access switch
        {
            NativeEventAccess.Wait => NativeMethods.Synchronize,
            NativeEventAccess.Signal => NativeMethods.EventModifyState,
            _ => 0U,
        };
        if (desiredAccess == 0)
        {
            return NativeResult<INamedEvent>.Failure(NativeErrors.InvalidParameter);
        }

        var eventHandle = NativeMethods.OpenEvent(desiredAccess, false, eventName);
        if (eventHandle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            eventHandle.Dispose();
            return NativeResult<INamedEvent>.Failure(error);
        }

        return NativeResult<INamedEvent>.Success(
            new Win32NamedEvent(eventHandle, access));
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);

    private static NativeResult<SafeProcessHandle> OpenProcess(
        int processId,
        uint desiredAccess)
    {
        if (processId <= 0)
        {
            return NativeResult<SafeProcessHandle>.Failure(NativeErrors.InvalidParameter);
        }

        var handle = NativeMethods.OpenProcess(
            desiredAccess,
            inheritHandle: false,
            checked((uint)processId));
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            return NativeResult<SafeProcessHandle>.Failure(error);
        }

        return NativeResult<SafeProcessHandle>.Success(handle);
    }

    private static bool IsSafeSystemModuleName(string? moduleName) =>
        !string.IsNullOrWhiteSpace(moduleName) &&
        moduleName.IndexOf('\0') < 0 &&
        string.Equals(Path.GetFileName(moduleName), moduleName, StringComparison.Ordinal) &&
        string.Equals(Path.GetExtension(moduleName), ".dll", StringComparison.OrdinalIgnoreCase);

    private static NativeResult<string> ReadLocalModulePath(nint module)
    {
        for (var capacity = 512; capacity <= 32768; capacity *= 2)
        {
            var builder = new StringBuilder(capacity);
            var length = NativeMethods.GetModuleFileName(module, builder, checked((uint)capacity));
            if (length == 0)
            {
                return NativeResult<string>.Failure(Marshal.GetLastWin32Error());
            }

            if (length < capacity - 1)
            {
                return CanonicalPath.From(builder.ToString());
            }
        }

        return NativeResult<string>.Failure(NativeErrors.InsufficientBuffer);
    }
}

internal class Win32ExplorerReadLease : IExplorerReadLease
{
    private readonly SafeProcessHandle processHandle;
    private int disposed;

    internal Win32ExplorerReadLease(int processId, SafeProcessHandle processHandle)
    {
        ProcessId = processId;
        this.processHandle = processHandle ?? throw new ArgumentNullException(nameof(processHandle));
    }

    public int ProcessId { get; }

    protected SafeProcessHandle ProcessHandle => processHandle;

    protected bool IsDisposed => Volatile.Read(ref disposed) != 0;

    public NativeResult<ExplorerProcessSnapshot> Snapshot()
    {
        if (IsDisposed || processHandle.IsClosed || processHandle.IsInvalid)
        {
            return NativeResult<ExplorerProcessSnapshot>.Failure(NativeErrors.InvalidHandle);
        }

        if (!NativeMethods.GetProcessTimes(
            processHandle,
            out var creationTime,
            out _,
            out _,
            out _))
        {
            return NativeResult<ExplorerProcessSnapshot>.Failure(Marshal.GetLastWin32Error());
        }

        if (!NativeMethods.ProcessIdToSessionId(
            checked((uint)ProcessId),
            out var sessionId))
        {
            return NativeResult<ExplorerProcessSnapshot>.Failure(Marshal.GetLastWin32Error());
        }

        var sid = ReadProcessUserSid();
        if (!sid.Succeeded)
        {
            return NativeResult<ExplorerProcessSnapshot>.Failure(sid.ErrorCode);
        }

        if (!NativeMethods.IsWow64Process2(
            processHandle,
            out var processMachine,
            out var nativeMachine))
        {
            return NativeResult<ExplorerProcessSnapshot>.Failure(Marshal.GetLastWin32Error());
        }

        var path = ReadProcessImagePath();
        if (!path.Succeeded)
        {
            return NativeResult<ExplorerProcessSnapshot>.Failure(path.ErrorCode);
        }

        var exitWait = NativeWait.Wait(processHandle, TimeSpan.Zero);
        if (exitWait.Kind is NativeWaitKind.Failed or NativeWaitKind.Abandoned)
        {
            return NativeResult<ExplorerProcessSnapshot>.Failure(exitWait.ErrorCode);
        }

        return NativeResult<ExplorerProcessSnapshot>.Success(
            new ExplorerProcessSnapshot(
                ProcessId,
                creationTime.ToUInt64(),
                sessionId,
                sid.Value!,
                MapArchitecture(processMachine, nativeMachine),
                path.Value!,
                exitWait.Kind == NativeWaitKind.Signaled));
    }

    public NativeResult<IReadOnlyList<RemoteModule>> EnumerateModules()
    {
        if (IsDisposed || processHandle.IsClosed || processHandle.IsInvalid)
        {
            return NativeResult<IReadOnlyList<RemoteModule>>.Failure(
                NativeErrors.InvalidHandle);
        }

        var moduleHandles = ReadModuleHandles();
        if (!moduleHandles.Succeeded)
        {
            return NativeResult<IReadOnlyList<RemoteModule>>.Failure(
                moduleHandles.ErrorCode);
        }

        var modules = new List<RemoteModule>(moduleHandles.Value!.Length);
        foreach (var moduleHandle in moduleHandles.Value)
        {
            if (!NativeMethods.GetRemoteModuleInformation(
                processHandle,
                moduleHandle,
                out var moduleInformation,
                checked((uint)Marshal.SizeOf<ModuleInformation>())))
            {
                return NativeResult<IReadOnlyList<RemoteModule>>.Failure(
                    Marshal.GetLastWin32Error());
            }

            var path = ReadRemoteModulePath(moduleHandle);
            if (!path.Succeeded)
            {
                return NativeResult<IReadOnlyList<RemoteModule>>.Failure(path.ErrorCode);
            }

            var baseAddress = Pointer.ToUInt64(moduleInformation.BaseOfDll);
            if (baseAddress == 0 || moduleInformation.SizeOfImage == 0)
            {
                return NativeResult<IReadOnlyList<RemoteModule>>.Failure(
                    NativeErrors.InvalidData);
            }

            modules.Add(new RemoteModule(
                path.Value!,
                baseAddress,
                moduleInformation.SizeOfImage));
        }

        return NativeResult<IReadOnlyList<RemoteModule>>.Success(modules);
    }

    public NativeWaitResult WaitForProcessExit(TimeSpan timeout)
    {
        if (IsDisposed || processHandle.IsClosed || processHandle.IsInvalid)
        {
            return new NativeWaitResult(NativeWaitKind.Failed, NativeErrors.InvalidHandle);
        }

        return NativeWait.Wait(processHandle, timeout);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            processHandle.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private NativeResult<string> ReadProcessUserSid()
    {
        if (!NativeMethods.OpenProcessToken(
            processHandle,
            NativeMethods.TokenQuery,
            out var tokenHandle))
        {
            return NativeResult<string>.Failure(Marshal.GetLastWin32Error());
        }

        using (tokenHandle)
        {
            _ = NativeMethods.GetTokenInformation(
                tokenHandle,
                NativeMethods.TokenUser,
                0,
                0,
                out var requiredLength);
            var sizeError = Marshal.GetLastWin32Error();
            if (requiredLength == 0 || sizeError != NativeErrors.InsufficientBuffer)
            {
                return NativeResult<string>.Failure(sizeError);
            }

            var tokenInformation = Marshal.AllocHGlobal(checked((int)requiredLength));
            try
            {
                if (!NativeMethods.GetTokenInformation(
                    tokenHandle,
                    NativeMethods.TokenUser,
                    tokenInformation,
                    requiredLength,
                    out _))
                {
                    return NativeResult<string>.Failure(Marshal.GetLastWin32Error());
                }

                var sid = Marshal.ReadIntPtr(tokenInformation);
                if (sid == 0 || !NativeMethods.ConvertSidToStringSid(sid, out var sidString))
                {
                    return NativeResult<string>.Failure(Marshal.GetLastWin32Error());
                }

                try
                {
                    var value = Marshal.PtrToStringUni(sidString);
                    return string.IsNullOrWhiteSpace(value)
                        ? NativeResult<string>.Failure(NativeErrors.InvalidSid)
                        : NativeResult<string>.Success(value);
                }
                finally
                {
                    _ = NativeMethods.LocalFree(sidString);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(tokenInformation);
            }
        }
    }

    private NativeResult<string> ReadProcessImagePath()
    {
        var builder = new StringBuilder(32768);
        var size = checked((uint)builder.Capacity);
        if (!NativeMethods.QueryFullProcessImageName(
            processHandle,
            0,
            builder,
            ref size))
        {
            return NativeResult<string>.Failure(Marshal.GetLastWin32Error());
        }

        return CanonicalPath.From(builder.ToString());
    }

    private NativeResult<nint[]> ReadModuleHandles()
    {
        var capacity = 64;
        while (capacity <= 65536)
        {
            var handles = new nint[capacity];
            var bufferBytes = checked((uint)(capacity * IntPtr.Size));
            if (!NativeMethods.EnumProcessModulesEx(
                processHandle,
                handles,
                bufferBytes,
                out var requiredBytes,
                NativeMethods.ListModulesAll))
            {
                return NativeResult<nint[]>.Failure(Marshal.GetLastWin32Error());
            }

            if (requiredBytes % IntPtr.Size != 0)
            {
                return NativeResult<nint[]>.Failure(NativeErrors.InvalidData);
            }

            if (requiredBytes <= bufferBytes)
            {
                var count = checked((int)(requiredBytes / IntPtr.Size));
                if (count == 0)
                {
                    return NativeResult<nint[]>.Failure(NativeErrors.InvalidData);
                }

                if (count != handles.Length)
                {
                    Array.Resize(ref handles, count);
                }

                return NativeResult<nint[]>.Success(handles);
            }

            capacity = checked((int)(requiredBytes / IntPtr.Size) + 16);
        }

        return NativeResult<nint[]>.Failure(NativeErrors.InsufficientBuffer);
    }

    private NativeResult<string> ReadRemoteModulePath(nint moduleHandle)
    {
        for (var capacity = 1024; capacity <= 32768; capacity *= 2)
        {
            var builder = new StringBuilder(capacity);
            var length = NativeMethods.GetRemoteModuleFileName(
                processHandle,
                moduleHandle,
                builder,
                checked((uint)capacity));
            if (length == 0)
            {
                return NativeResult<string>.Failure(Marshal.GetLastWin32Error());
            }

            if (length < capacity - 1)
            {
                return CanonicalPath.From(builder.ToString());
            }
        }

        return NativeResult<string>.Failure(NativeErrors.InsufficientBuffer);
    }

    private static ExplorerProcessArchitecture MapArchitecture(
        ushort processMachine,
        ushort nativeMachine)
    {
        var effectiveMachine = processMachine == NativeMethods.ImageFileMachineUnknown
            ? nativeMachine
            : processMachine;
        return effectiveMachine switch
        {
            NativeMethods.ImageFileMachineI386 => ExplorerProcessArchitecture.X86,
            NativeMethods.ImageFileMachineAmd64 => ExplorerProcessArchitecture.X64,
            NativeMethods.ImageFileMachineArm64 => ExplorerProcessArchitecture.Arm64,
            _ => ExplorerProcessArchitecture.Unknown,
        };
    }
}

internal sealed class Win32ExplorerMutationLease :
    Win32ExplorerReadLease,
    IExplorerMutationLease
{
    internal Win32ExplorerMutationLease(int processId, SafeProcessHandle processHandle)
        : base(processId, processHandle)
    {
    }

    public NativeResult<IRemoteMemory> Allocate(nuint size)
    {
        if (size == 0)
        {
            return NativeResult<IRemoteMemory>.Failure(NativeErrors.InvalidParameter);
        }

        if (IsDisposed || ProcessHandle.IsClosed || ProcessHandle.IsInvalid)
        {
            return NativeResult<IRemoteMemory>.Failure(NativeErrors.InvalidHandle);
        }

        var referenceAdded = false;
        try
        {
            ProcessHandle.DangerousAddRef(ref referenceAdded);
            var rawProcessHandle = ProcessHandle.DangerousGetHandle();
            var address = NativeMethods.VirtualAllocEx(
                rawProcessHandle,
                0,
                size,
                NativeMethods.MemCommit | NativeMethods.MemReserve,
                NativeMethods.PageReadWrite);
            if (address == 0)
            {
                return NativeResult<IRemoteMemory>.Failure(Marshal.GetLastWin32Error());
            }

            var memory = new RemoteMemoryLease(
                this,
                Pointer.ToUInt64(address),
                size,
                remoteAddress => NativeMethods.VirtualFreeEx(
                    rawProcessHandle,
                    Pointer.FromUInt64(remoteAddress),
                    0,
                    NativeMethods.MemRelease),
                ProcessHandle.DangerousRelease);
            referenceAdded = false;
            return NativeResult<IRemoteMemory>.Success(memory);
        }
        catch (ObjectDisposedException)
        {
            return NativeResult<IRemoteMemory>.Failure(NativeErrors.InvalidHandle);
        }
        finally
        {
            if (referenceAdded)
            {
                ProcessHandle.DangerousRelease();
            }
        }
    }

    public NativeResult WriteAll(IRemoteMemory memory, ReadOnlyMemory<byte> bytes)
    {
        if (memory is not RemoteMemoryLease remoteMemory ||
            bytes.IsEmpty ||
            (nuint)bytes.Length > remoteMemory.Size ||
            !remoteMemory.TryGetWritableAddress(this, out var address))
        {
            return NativeResult.Failure(NativeErrors.InvalidParameter);
        }

        if (IsDisposed || ProcessHandle.IsClosed || ProcessHandle.IsInvalid)
        {
            return NativeResult.Failure(NativeErrors.InvalidHandle);
        }

        var buffer = bytes.ToArray();
        if (!NativeMethods.WriteProcessMemory(
            ProcessHandle,
            Pointer.FromUInt64(address),
            buffer,
            checked((nuint)buffer.Length),
            out var bytesWritten))
        {
            return NativeResult.Failure(Marshal.GetLastWin32Error());
        }

        return bytesWritten == (nuint)buffer.Length
            ? NativeResult.Success()
            : NativeResult.Failure(NativeErrors.PartialCopy);
    }

    public NativeResult<IRemoteThread> StartRemoteThread(
        ulong startAddress,
        IRemoteMemory? parameter)
    {
        if (startAddress == 0 ||
            IsDisposed ||
            ProcessHandle.IsClosed ||
            ProcessHandle.IsInvalid)
        {
            return NativeResult<IRemoteThread>.Failure(NativeErrors.InvalidParameter);
        }

        RemoteMemoryLease? remoteParameter = null;
        var parameterAddress = 0UL;
        if (parameter is not null)
        {
            remoteParameter = parameter as RemoteMemoryLease;
            if (remoteParameter is null ||
                !remoteParameter.TryReserveForThread(this, out parameterAddress))
            {
                return NativeResult<IRemoteThread>.Failure(NativeErrors.InvalidParameter);
            }
        }

        var threadHandle = NativeMethods.CreateRemoteThread(
            ProcessHandle,
            0,
            0,
            Pointer.FromUInt64(startAddress),
            Pointer.FromUInt64(parameterAddress),
            0,
            out _);
        if (threadHandle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            threadHandle.Dispose();
            remoteParameter?.CancelThreadStart();
            return NativeResult<IRemoteThread>.Failure(error);
        }

        var thread = new Win32RemoteThread(threadHandle);
        if (remoteParameter is not null)
        {
            remoteParameter.CompleteThreadStart(thread.HasObservedTerminalState);
        }

        return NativeResult<IRemoteThread>.Success(thread);
    }
}

internal sealed class RemoteMemoryLease : IRemoteMemory
{
    private readonly object syncRoot = new();
    private readonly Func<ulong, bool> release;
    private readonly Action releaseOwnerReference;
    private MemoryState state;
    private Func<bool>? hasObservedTerminalState;

    internal RemoteMemoryLease(
        object owner,
        ulong address,
        nuint size,
        Func<ulong, bool> release,
        Action releaseOwnerReference)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Address = address != 0
            ? address
            : throw new ArgumentOutOfRangeException(nameof(address));
        Size = size != 0 ? size : throw new ArgumentOutOfRangeException(nameof(size));
        this.release = release ?? throw new ArgumentNullException(nameof(release));
        this.releaseOwnerReference = releaseOwnerReference ??
            throw new ArgumentNullException(nameof(releaseOwnerReference));
    }

    ~RemoteMemoryLease()
    {
        AbandonCore(suppressFinalize: false);
    }

    public ulong Address { get; }

    public nuint Size { get; }

    internal object Owner { get; }

    public void Abandon() => AbandonCore(suppressFinalize: true);

    public void Dispose()
    {
        var shouldRelease = false;
        lock (syncRoot)
        {
            if (state == MemoryState.Finished)
            {
                GC.SuppressFinalize(this);
                return;
            }

            shouldRelease = state == MemoryState.Available ||
                (state == MemoryState.ThreadBound && hasObservedTerminalState!());
            state = MemoryState.Finished;
        }

        try
        {
            if (shouldRelease)
            {
                _ = release(Address);
            }
        }
        finally
        {
            releaseOwnerReference();
            GC.SuppressFinalize(this);
        }
    }

    internal bool TryGetWritableAddress(object owner, out ulong address)
    {
        lock (syncRoot)
        {
            if (!ReferenceEquals(Owner, owner) || state != MemoryState.Available)
            {
                address = 0;
                return false;
            }

            address = Address;
            return true;
        }
    }

    internal bool TryReserveForThread(object owner, out ulong address)
    {
        lock (syncRoot)
        {
            if (!ReferenceEquals(Owner, owner) || state != MemoryState.Available)
            {
                address = 0;
                return false;
            }

            state = MemoryState.ReservedForThread;
            address = Address;
            return true;
        }
    }

    internal void CancelThreadStart()
    {
        lock (syncRoot)
        {
            if (state == MemoryState.ReservedForThread)
            {
                state = MemoryState.Available;
            }
        }
    }

    internal void CompleteThreadStart(Func<bool> terminalState)
    {
        ArgumentNullException.ThrowIfNull(terminalState);
        lock (syncRoot)
        {
            if (state != MemoryState.ReservedForThread)
            {
                throw new InvalidOperationException("Remote memory was not reserved for a thread.");
            }

            hasObservedTerminalState = terminalState;
            state = MemoryState.ThreadBound;
        }
    }

    private void AbandonCore(bool suppressFinalize)
    {
        lock (syncRoot)
        {
            if (state == MemoryState.Finished)
            {
                if (suppressFinalize)
                {
                    GC.SuppressFinalize(this);
                }

                return;
            }

            state = MemoryState.Finished;
        }

        releaseOwnerReference();
        if (suppressFinalize)
        {
            GC.SuppressFinalize(this);
        }
    }

    private enum MemoryState
    {
        Available,
        ReservedForThread,
        ThreadBound,
        Finished,
    }
}

internal sealed class Win32RemoteThread : IRemoteThread
{
    private readonly SafeWaitHandle threadHandle;
    private int terminalStateObserved;

    internal Win32RemoteThread(SafeWaitHandle threadHandle)
    {
        this.threadHandle = threadHandle ?? throw new ArgumentNullException(nameof(threadHandle));
    }

    public NativeWaitResult Wait(TimeSpan timeout)
    {
        if (threadHandle.IsClosed || threadHandle.IsInvalid)
        {
            return new NativeWaitResult(NativeWaitKind.Failed, NativeErrors.InvalidHandle);
        }

        var result = NativeWait.Wait(threadHandle, timeout);
        if (result.Kind == NativeWaitKind.Signaled)
        {
            Volatile.Write(ref terminalStateObserved, 1);
            if (!NativeMethods.GetExitCodeThread(threadHandle, out var exitCode))
            {
                return new NativeWaitResult(
                    NativeWaitKind.Failed,
                    NativeErrors.Normalize(Marshal.GetLastWin32Error()));
            }

            if (exitCode == NativeMethods.StillActive)
            {
                return new NativeWaitResult(NativeWaitKind.Failed, NativeErrors.InvalidData);
            }

            return new NativeWaitResult(NativeWaitKind.Signaled, 0, exitCode);
        }

        return result;
    }

    public void Dispose() => threadHandle.Dispose();

    internal bool HasObservedTerminalState() =>
        Volatile.Read(ref terminalStateObserved) != 0;
}

internal sealed class Win32NamedEvent : INamedEvent
{
    private readonly SafeWaitHandle eventHandle;
    private readonly NativeEventAccess access;

    internal Win32NamedEvent(SafeWaitHandle eventHandle, NativeEventAccess access)
    {
        this.eventHandle = eventHandle ?? throw new ArgumentNullException(nameof(eventHandle));
        this.access = access;
    }

    public NativeWaitResult Wait(TimeSpan timeout)
    {
        if (access != NativeEventAccess.Wait)
        {
            return new NativeWaitResult(NativeWaitKind.Failed, NativeErrors.AccessDenied);
        }

        return NativeWait.Wait(eventHandle, timeout);
    }

    public NativeResult Signal()
    {
        if (access != NativeEventAccess.Signal)
        {
            return NativeResult.Failure(NativeErrors.AccessDenied);
        }

        if (eventHandle.IsClosed || eventHandle.IsInvalid)
        {
            return NativeResult.Failure(NativeErrors.InvalidHandle);
        }

        return NativeMethods.SetEvent(eventHandle)
            ? NativeResult.Success()
            : NativeResult.Failure(Marshal.GetLastWin32Error());
    }

    public void Dispose() => eventHandle.Dispose();
}

internal static class NativeWait
{
    internal static NativeWaitResult Wait(SafeHandle handle, TimeSpan timeout)
    {
        if (handle.IsClosed || handle.IsInvalid || !TryGetMilliseconds(timeout, out var milliseconds))
        {
            return new NativeWaitResult(
                NativeWaitKind.Failed,
                handle.IsClosed || handle.IsInvalid
                    ? NativeErrors.InvalidHandle
                    : NativeErrors.InvalidParameter);
        }

        var result = NativeMethods.WaitForSingleObject(handle, milliseconds);
        return result switch
        {
            NativeMethods.WaitObject0 => new NativeWaitResult(NativeWaitKind.Signaled, 0),
            NativeMethods.WaitTimeout => new NativeWaitResult(NativeWaitKind.TimedOut, 0),
            NativeMethods.WaitAbandoned =>
                new NativeWaitResult(NativeWaitKind.Abandoned, NativeErrors.AbandonedWait),
            NativeMethods.WaitFailed =>
                new NativeWaitResult(
                    NativeWaitKind.Failed,
                    NativeErrors.Normalize(Marshal.GetLastWin32Error())),
            _ => new NativeWaitResult(NativeWaitKind.Failed, NativeErrors.InvalidData),
        };
    }

    private static bool TryGetMilliseconds(TimeSpan timeout, out uint milliseconds)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            milliseconds = uint.MaxValue;
            return true;
        }

        if (timeout < TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1d)
        {
            milliseconds = 0;
            return false;
        }

        milliseconds = checked((uint)Math.Ceiling(timeout.TotalMilliseconds));
        return true;
    }
}

internal static class CanonicalPath
{
    internal static NativeResult<string> From(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0)
        {
            return NativeResult<string>.Failure(NativeErrors.InvalidData);
        }

        try
        {
            var canonical = Path.GetFullPath(path);
            return Path.IsPathFullyQualified(canonical)
                ? NativeResult<string>.Success(canonical)
                : NativeResult<string>.Failure(NativeErrors.InvalidData);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return NativeResult<string>.Failure(NativeErrors.InvalidData);
        }
    }
}

internal static class Pointer
{
    internal static ulong ToUInt64(nint value) => unchecked((ulong)(nuint)value);

    internal static nint FromUInt64(ulong value) => unchecked((nint)(nuint)value);
}

internal static class NativeErrors
{
    internal const int AccessDenied = 5;
    internal const int InvalidHandle = 6;
    internal const int InvalidData = 13;
    internal const int GeneralFailure = 31;
    internal const int InvalidParameter = 87;
    internal const int InsufficientBuffer = 122;
    internal const int InvalidAddress = 487;
    internal const int AbandonedWait = 735;
    internal const int InvalidSid = 1337;
    internal const int PartialCopy = 299;

    internal static int Normalize(int errorCode) =>
        errorCode > 0 ? errorCode : GeneralFailure;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativeFileTime
{
    private readonly uint lowDateTime;
    private readonly uint highDateTime;

    internal ulong ToUInt64() => ((ulong)highDateTime << 32) | lowDateTime;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ModuleInformation
{
    internal nint BaseOfDll;
    internal uint SizeOfImage;
    internal nint EntryPoint;
}

internal sealed class SafeLibraryHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeLibraryHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => NativeMethods.FreeLibrary(handle);
}

internal static class NativeMethods
{
    internal const uint LoadLibrarySearchSystem32 = 0x00000800;
    internal const uint GetModuleHandleFromAddressFlags = 0x00000006;
    internal const uint ListModulesAll = 0x00000003;
    internal const uint TokenQuery = 0x00000008;
    internal const int TokenUser = 1;
    internal const uint MemCommit = 0x00001000;
    internal const uint MemReserve = 0x00002000;
    internal const uint MemRelease = 0x00008000;
    internal const uint PageReadWrite = 0x00000004;
    internal const uint Synchronize = 0x00100000;
    internal const uint EventModifyState = 0x00000002;
    internal const uint WaitObject0 = 0x00000000;
    internal const uint WaitAbandoned = 0x00000080;
    internal const uint WaitTimeout = 0x00000102;
    internal const uint WaitFailed = 0xffffffff;
    internal const uint StillActive = 0x00000103;
    internal const ushort ImageFileMachineUnknown = 0x0000;
    internal const ushort ImageFileMachineI386 = 0x014c;
    internal const ushort ImageFileMachineAmd64 = 0x8664;
    internal const ushort ImageFileMachineArm64 = 0xaa64;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(
        SafeProcessHandle process,
        out NativeFileTime creationTime,
        out NativeFileTime exitTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWow64Process2(
        SafeProcessHandle process,
        out ushort processMachine,
        out ushort nativeMachine);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        StringBuilder imageName,
        ref uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(
        SafeProcessHandle process,
        uint desiredAccess,
        out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTokenInformation(
        SafeAccessTokenHandle token,
        int informationClass,
        nint tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ConvertSidToStringSid(nint sid, out nint stringSid);

    [DllImport("kernel32.dll")]
    internal static extern nint LocalFree(nint memory);

    [DllImport("kernel32.dll", EntryPoint = "K32EnumProcessModulesEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumProcessModulesEx(
        SafeProcessHandle process,
        [Out] nint[] modules,
        uint bytes,
        out uint bytesNeeded,
        uint filterFlag);

    [DllImport("kernel32.dll", EntryPoint = "K32GetModuleInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetRemoteModuleInformation(
        SafeProcessHandle process,
        nint module,
        out ModuleInformation moduleInformation,
        uint size);

    [DllImport("kernel32.dll", EntryPoint = "K32GetModuleInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCurrentModuleInformation(
        nint process,
        nint module,
        out ModuleInformation moduleInformation,
        uint size);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "K32GetModuleFileNameExW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    internal static extern uint GetRemoteModuleFileName(
        SafeProcessHandle process,
        nint module,
        StringBuilder fileName,
        uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetModuleFileName(
        nint module,
        StringBuilder fileName,
        uint size);

    [DllImport("kernel32.dll")]
    internal static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeLibraryHandle LoadLibraryEx(
        string fileName,
        nint file,
        uint flags);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Ansi,
        ExactSpelling = true,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true,
        SetLastError = true)]
    internal static extern nint GetProcAddress(
        SafeLibraryHandle module,
        string procedureName);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetModuleHandleExW",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetModuleHandleFromAddress(
        uint flags,
        nint address,
        out nint module);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FreeLibrary(nint module);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint VirtualAllocEx(
        nint process,
        nint address,
        nuint size,
        uint allocationType,
        uint protection);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool VirtualFreeEx(
        nint process,
        nint address,
        nuint size,
        uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WriteProcessMemory(
        SafeProcessHandle process,
        nint baseAddress,
        byte[] buffer,
        nuint size,
        out nuint bytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeWaitHandle CreateRemoteThread(
        SafeProcessHandle process,
        nint threadAttributes,
        nuint stackSize,
        nint startAddress,
        nint parameter,
        uint creationFlags,
        out uint threadId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeWaitHandle OpenEvent(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetEvent(SafeWaitHandle eventHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(SafeHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeThread(
        SafeWaitHandle threadHandle,
        out uint exitCode);
}
