using System.Buffers.Binary;

namespace CodexQuotaTaskbar.BridgeControl.Injection;

internal readonly record struct ExplorerInstanceBinding(
    int ExplorerProcessId,
    ulong ExplorerCreationTimeFileTime100Nanoseconds,
    Guid ActivationId)
{
    internal bool IsValid =>
        ExplorerProcessId > 0 &&
        ExplorerCreationTimeFileTime100Nanoseconds > 0 &&
        ActivationId != Guid.Empty;
}

internal enum BridgeLifecyclePhase
{
    ControlEvents = 0,
    InitializeDiagnostics = 1,
    InitializeTaskbarThreads = 2,
    AdviseWatcher = 3,
    Ready = 4,
}

internal sealed record BridgeEventNames(
    string StartReleased,
    string Ready,
    string Shutdown,
    string Quiesced)
{
    private const string Prefix = @"Local\CQTB.Probe.v1";

    internal static BridgeEventNames For(ExplorerInstanceBinding binding)
    {
        var instance = InstanceFor(binding);
        return new BridgeEventNames(
            $"{instance}.StartReleased",
            $"{instance}.Ready",
            $"{instance}.Shutdown",
            $"{instance}.Quiesced");
    }

    internal static string ForPhase(
        ExplorerInstanceBinding binding,
        BridgeLifecyclePhase phase)
    {
        if (!Enum.IsDefined<BridgeLifecyclePhase>(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        return $"{InstanceFor(binding)}.Phase.{phase}";
    }

    private static string InstanceFor(ExplorerInstanceBinding binding)
    {
        if (!binding.IsValid)
        {
            throw new ArgumentException("The Explorer instance binding is invalid.", nameof(binding));
        }

        var activationBytes = binding.ActivationId.ToByteArray();
        var instance = string.Create(
            provider: null,
            $"{Prefix}.{binding.ExplorerProcessId:x8}." +
            $"{binding.ExplorerCreationTimeFileTime100Nanoseconds:x16}." +
            $"{Convert.ToHexString(activationBytes).ToLowerInvariant()}");
        return instance;
    }
}

internal static class BridgeStartRequest
{
    internal const int Size = 56;
    internal const uint AbiVersion = 1;
    private const uint BindingSize = 32;

    internal static byte[] Build(ExplorerInstanceBinding binding)
    {
        if (!binding.IsValid)
        {
            throw new ArgumentException("The Explorer instance binding is invalid.", nameof(binding));
        }

        var request = new byte[Size];
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(0x00), Size);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(0x04), AbiVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(0x08), BindingSize);
        BinaryPrimitives.WriteUInt32LittleEndian(
            request.AsSpan(0x0c),
            checked((uint)binding.ExplorerProcessId));
        BinaryPrimitives.WriteUInt64LittleEndian(
            request.AsSpan(0x10),
            binding.ExplorerCreationTimeFileTime100Nanoseconds);
        binding.ActivationId.ToByteArray().CopyTo(request, 0x18);
        return request;
    }
}
