using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using CodexQuotaTaskbar.Host.Bongo;

namespace CodexQuotaTaskbar.Host.Tests.Bongo;

// Managed contract tests only: no payload resolver, process, HWND or real input.
public sealed class BongoRuntimeContractTests
{
    [Fact]
    public async Task A_rejected_startup_guard_does_not_leave_a_child_or_block_the_next_attempt()
    {
        var calls = 0;
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");
        await using var runtime = new BongoRuntime(_ => Task.FromResult(new BongoPayloadPaths(missing, Path.GetTempPath())),
            () => { calls++; throw new InvalidOperationException("Synthetic denied gate."); });
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await runtime.StartAsync(64, 64, singleFrame: true);
            Assert.Null(runtime.ActiveProcessId);
            Assert.True(runtime.ExitConfirmed);
            Assert.False(runtime.IsRunning);
        }
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task An_executable_that_does_not_exist_cannot_become_an_uncertain_child()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");
        Assert.False(File.Exists(missing));
        await using var runtime = new BongoRuntime(_ => Task.FromResult(new BongoPayloadPaths(missing, Path.GetTempPath())));
        await runtime.StartAsync(64, 64, singleFrame: true);
        Assert.Null(runtime.ActiveProcessId);
        Assert.True(runtime.ExitConfirmed);
    }

    [Theory]
    [InlineData(64, 64)]
    [InlineData(192, 112)]
    [InlineData(384, 224)]
    [InlineData(1024, 1024)]
    public void Valid_header_returns_only_the_exact_bounded_payload_length(int width, int height)
    {
        var packet = Header((uint)width, (uint)height, (uint)(width * height * 4));
        Assert.Equal(width * height * 4, BongoRuntime.DecodeHeader(packet, width, height));
    }

    [Theory]
    [InlineData(63, 64, 63 * 64 * 4)]
    [InlineData(64, 63, 64 * 63 * 4)]
    [InlineData(1025, 64, 1025 * 64 * 4)]
    [InlineData(64, 1025, 64 * 1025 * 4)]
    [InlineData(64, 64, 0)]
    [InlineData(64, 64, 16383)]
    [InlineData(64, 64, 16385)]
    public void Invalid_dimensions_or_payload_lengths_are_rejected_before_allocation(int width, int height, int bytes)
    {
        Assert.Throws<InvalidDataException>(() => BongoRuntime.DecodeHeader(
            Header((uint)width, (uint)height, (uint)bytes), width, height));
    }

    [Fact]
    public void Wrong_magic_truncation_mismatch_and_unsigned_extremes_cannot_negotiate_another_frame()
    {
        var packet = Header(64, 64, 16384);
        packet[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => BongoRuntime.DecodeHeader(packet, 64, 64));
        packet = Header(64, 64, 16384);
        Assert.Throws<InvalidDataException>(() => BongoRuntime.DecodeHeader(packet[..15], 64, 64));
        Assert.Throws<InvalidDataException>(() => BongoRuntime.DecodeHeader(packet, 128, 64));
        Assert.Throws<InvalidDataException>(() => BongoRuntime.DecodeHeader(Header(uint.MaxValue, uint.MaxValue, uint.MaxValue), 64, 64));
    }

    [Fact]
    public void A_pooled_frame_returns_its_buffer_exactly_once_and_becomes_inaccessible()
    {
        var pool = new CountingPool();
        var pixels = pool.Rent(16);
        var frame = new BongoFrame(2, 2, pixels, 16, pool);

        frame.Dispose();
        frame.Dispose();

        Assert.Same(pixels, Assert.Single(pool.Returned));
        Assert.Throws<ObjectDisposedException>(() => frame.Pixels);
    }

    [Fact]
    public void Cloning_copies_only_frame_bytes_and_has_independent_lifetime()
    {
        var pool = new CountingPool();
        var pixels = pool.Rent(32);
        Array.Fill(pixels, (byte)99);
        pixels[0] = 12;
        var original = new BongoFrame(2, 2, pixels, 16, pool);
        using var clone = original.Clone();

        Assert.Equal(16, clone.ByteCount);
        Assert.Equal(12, clone.Pixels[0]);
        clone.Pixels[0] = 27;
        Assert.Equal(12, original.Pixels[0]);
        original.Dispose();
        Assert.Same(pixels, Assert.Single(pool.Returned));
        Assert.Equal(27, clone.Pixels[0]);
        Assert.Throws<ObjectDisposedException>(() => original.Clone());
    }

    [Theory]
    [InlineData(0, 2, 0)]
    [InlineData(2, 0, 0)]
    [InlineData(1025, 1, 16)]
    [InlineData(1, 1025, 16)]
    [InlineData(2, 2, 15)]
    [InlineData(2, 2, 17)]
    [InlineData(3, 3, 36)]
    public void A_rejected_frame_does_not_take_ownership_of_the_callers_buffer(int width, int height, int bytes)
    {
        var pool = new CountingPool();
        var pixels = pool.Rent(32);
        Assert.Throws<ArgumentException>(() => new BongoFrame(width, height, pixels, bytes, pool));
        Assert.Empty(pool.Returned);
    }

    private static byte[] Header(uint width, uint height, uint count)
    {
        var packet = new byte[16];
        "BCF1"u8.CopyTo(packet);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), width);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), height);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), count);
        return packet;
    }

    private sealed class CountingPool : ArrayPool<byte>
    {
        internal List<byte[]> Returned { get; } = [];
        public override byte[] Rent(int minimumLength) => new byte[minimumLength];
        public override void Return(byte[] array, bool clearArray = false) => Returned.Add(array);
    }
}
