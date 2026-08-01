using System.Reflection;
using System.Runtime.InteropServices;
using CodexQuotaTaskbar.BridgeControl.Injection;

namespace CodexQuotaTaskbar.BridgeControl.Tests;

public sealed class ExplorerProcessApiContractTests
{
    [Fact]
    public void Boundary_exposes_only_lease_scoped_process_and_event_operations()
    {
        var apiMethods = typeof(IExplorerProcessApi)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => method.Name)
            .ToArray();

        Assert.Contains("OpenForCollection", apiMethods);
        Assert.Contains("OpenForInjection", apiMethods);
        Assert.Contains("ResolveLocalSystemExport", apiMethods);
        Assert.Contains("TryOpenEvent", apiMethods);
        Assert.Contains("DelayAsync", apiMethods);
        Assert.DoesNotContain(
            apiMethods,
            name => name.Contains("FreeLibrary", StringComparison.OrdinalIgnoreCase));

        var readLeaseMethods = typeof(IExplorerReadLease)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => method.Name)
            .ToArray();

        Assert.Contains("Snapshot", readLeaseMethods);
        Assert.Contains("EnumerateModules", readLeaseMethods);
        Assert.Contains("WaitForProcessExit", readLeaseMethods);
        Assert.DoesNotContain("Allocate", readLeaseMethods);
        Assert.DoesNotContain("WriteAll", readLeaseMethods);
        Assert.DoesNotContain("StartRemoteThread", readLeaseMethods);
        Assert.DoesNotContain(
            readLeaseMethods,
            name => name.Contains("FreeLibrary", StringComparison.OrdinalIgnoreCase));

        var mutationMethods = typeof(IExplorerMutationLease)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => method.Name)
            .ToArray();

        Assert.Contains("Allocate", mutationMethods);
        Assert.Contains("WriteAll", mutationMethods);
        Assert.Contains("StartRemoteThread", mutationMethods);
        Assert.True(typeof(IExplorerReadLease).IsAssignableFrom(typeof(IExplorerMutationLease)));
        Assert.DoesNotContain(
            mutationMethods,
            name => name.Contains("FreeLibrary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Injection_open_uses_the_exact_reviewed_rights_and_collection_is_read_only()
    {
        const uint createThread = 0x0002;
        const uint virtualMemoryOperation = 0x0008;
        const uint virtualMemoryRead = 0x0010;
        const uint virtualMemoryWrite = 0x0020;
        const uint queryInformation = 0x0400;
        const uint queryLimitedInformation = 0x1000;
        const uint synchronize = 0x00100000;

        const uint expectedInjection =
            createThread |
            virtualMemoryOperation |
            virtualMemoryRead |
            virtualMemoryWrite |
            queryInformation |
            queryLimitedInformation |
            synchronize;
        const uint expectedCollection =
            virtualMemoryRead |
            queryInformation |
            queryLimitedInformation |
            synchronize;

        Assert.Equal(expectedInjection, Win32ExplorerProcessApi.InjectionAccessMask);
        Assert.Equal(expectedCollection, Win32ExplorerProcessApi.CollectionAccessMask);
        Assert.Equal(
            0u,
            Win32ExplorerProcessApi.CollectionAccessMask &
            (createThread | virtualMemoryOperation | virtualMemoryWrite));
    }

    [Fact]
    public void Evidence_contract_preserves_raw_identity_and_full_x64_module_addresses()
    {
        var snapshot = new ExplorerProcessSnapshot(
            123,
            0xfedcba9876543210,
            7,
            "S-1-5-21-1",
            ExplorerProcessArchitecture.X64,
            @"C:\Windows\explorer.exe",
            hasExited: false);
        var module = new RemoteModule(
            @"C:\Windows\System32\kernelbase.dll",
            0xfedcba9876543000,
            0x123000);
        var export = new LocalSystemExport(
            @"C:\Windows\System32\kernelbase.dll",
            0x1234,
            0x456000);

        Assert.Equal(0xfedcba9876543210UL, snapshot.CreationTimeFileTime100Nanoseconds);
        Assert.Equal(0xfedcba9876543000UL, module.BaseAddress);
        Assert.Equal(0x123000U, module.Size);
        Assert.Equal(0x1234U, export.RelativeVirtualAddress);
        Assert.Equal(0x456000U, export.ImageSize);
    }

    [Fact]
    public void Remote_memory_can_be_abandoned_without_releasing_target_pages()
    {
        var releases = 0;
        var cleanup = 0;
        using var memory = new RemoteMemoryLease(
            owner: new object(),
            address: 0x12340000,
            size: 256,
            release: _ =>
            {
                releases++;
                return true;
            },
            releaseOwnerReference: () => cleanup++);

        memory.Abandon();

        Assert.Equal(0, releases);
        Assert.Equal(1, cleanup);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void Thread_bound_memory_is_normally_released_only_after_terminal_wait(
        bool terminalObserved,
        int expectedReleases)
    {
        var releases = 0;
        var cleanup = 0;
        using var memory = new RemoteMemoryLease(
            owner: new object(),
            address: 0x12340000,
            size: 256,
            release: _ =>
            {
                releases++;
                return true;
            },
            releaseOwnerReference: () => cleanup++);

        Assert.True(memory.TryReserveForThread(memory.Owner, out _));
        memory.CompleteThreadStart(() => terminalObserved);
        memory.Dispose();

        Assert.Equal(expectedReleases, releases);
        Assert.Equal(1, cleanup);
    }

    [Fact]
    public void Wait_and_named_event_contracts_are_typed_and_minimal()
    {
        Assert.Equal(
            new[]
            {
                NativeWaitKind.Signaled,
                NativeWaitKind.TimedOut,
                NativeWaitKind.Abandoned,
                NativeWaitKind.Failed,
            },
            Enum.GetValues<NativeWaitKind>());

        Assert.Equal(
            new[] { NativeEventAccess.Wait, NativeEventAccess.Signal },
            Enum.GetValues<NativeEventAccess>());
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(IRemoteThread)));
        Assert.Equal(
            new[] { "Wait" },
            typeof(IRemoteThread)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(method => method.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(INamedEvent)));
        Assert.Equal(
            new[] { "Signal", "Wait" },
            typeof(INamedEvent)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(method => method.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public void Typed_wait_carries_a_thread_exit_code_without_reinterpreting_it_as_an_address()
    {
        var threadWait = new NativeWaitResult(
            NativeWaitKind.Signaled,
            errorCode: 0,
            exitCode: 0);
        var ordinaryWait = new NativeWaitResult(
            NativeWaitKind.Signaled,
            errorCode: 0);

        Assert.Equal(0U, threadWait.ExitCode);
        Assert.Null(ordinaryWait.ExitCode);
        Assert.Equal(typeof(uint?), typeof(NativeWaitResult).GetProperty("ExitCode")!.PropertyType);
    }

    [Fact]
    public void Module_enumeration_binds_the_supported_kernel32_entry_point()
    {
        var method = typeof(NativeMethods).GetMethod(
            "EnumProcessModulesEx",
            BindingFlags.Static | BindingFlags.NonPublic);
        var import = method!.GetCustomAttribute<DllImportAttribute>();

        Assert.Equal("K32EnumProcessModulesEx", import!.EntryPoint);
    }
}
