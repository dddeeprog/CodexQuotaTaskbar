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

internal sealed record BridgeEventNames(
    string StartReleased,
    string Ready,
    string Shutdown,
    string Quiesced)
{
    private const string Prefix = @"Local\CQTB.Probe.v1";

    internal static BridgeEventNames For(ExplorerInstanceBinding binding)
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

        return new BridgeEventNames(
            $"{instance}.StartReleased",
            $"{instance}.Ready",
            $"{instance}.Shutdown",
            $"{instance}.Quiesced");
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
