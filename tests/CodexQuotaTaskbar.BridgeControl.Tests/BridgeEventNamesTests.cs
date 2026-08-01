using CodexQuotaTaskbar.BridgeControl.Injection;
using System.Buffers.Binary;

namespace CodexQuotaTaskbar.BridgeControl.Tests;

public sealed class BridgeEventNamesTests
{
    [Fact]
    public void Matches_the_native_event_contract_and_guid_memory_byte_order()
    {
        var binding = new ExplorerInstanceBinding(
            0x1234,
            0x0123456789abcdef,
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));

        var names = BridgeEventNames.For(binding);

        const string prefix =
            @"Local\CQTB.Probe.v1.00001234.0123456789abcdef.33221100554477668899aabbccddeeff.";
        Assert.Equal(prefix + "StartReleased", names.StartReleased);
        Assert.Equal(prefix + "Ready", names.Ready);
        Assert.Equal(prefix + "Shutdown", names.Shutdown);
        Assert.Equal(prefix + "Quiesced", names.Quiesced);
    }

    [Theory]
    [InlineData(0, 1UL, "00112233-4455-6677-8899-aabbccddeeff")]
    [InlineData(1, 0UL, "00112233-4455-6677-8899-aabbccddeeff")]
    [InlineData(1, 1UL, "00000000-0000-0000-0000-000000000000")]
    public void Rejects_an_invalid_binding(int processId, ulong creationTime, string activationId)
    {
        var binding = new ExplorerInstanceBinding(
            processId,
            creationTime,
            Guid.Parse(activationId));

        Assert.Throws<ArgumentException>(() => BridgeEventNames.For(binding));
    }

    [Fact]
    public void Start_request_is_exactly_56_little_endian_bytes_with_zero_reserved_words()
    {
        var activation = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var binding = new ExplorerInstanceBinding(
            0x1234,
            0x0123456789abcdef,
            activation);

        var request = BridgeStartRequest.Build(binding);

        Assert.Equal(56, request.Length);
        Assert.Equal(56U, BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(0x00)));
        Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(0x04)));
        Assert.Equal(32U, BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(0x08)));
        Assert.Equal(0x1234U, BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(0x0c)));
        Assert.Equal(
            0x0123456789abcdefUL,
            BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(0x10)));
        Assert.Equal(activation.ToByteArray(), request.AsSpan(0x18, 16).ToArray());
        Assert.All(request.AsSpan(0x28, 16).ToArray(), value => Assert.Equal(0, value));
    }
}
