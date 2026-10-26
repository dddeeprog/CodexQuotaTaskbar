using System.Buffers;

namespace CodexQuotaTaskbar.Host.Bongo;

// One frame has one owner; a mailbox replaces/disposes old frames rather than
// accumulating UI work. Buffers contain only model pixels, never input text.
internal sealed class BongoFrame : IDisposable
{
    private byte[]? pixels;
    private readonly ArrayPool<byte>? pool;
    internal int Width { get; }
    internal int Height { get; }
    internal int ByteCount { get; }
    internal byte[] Pixels => pixels ?? throw new ObjectDisposedException(nameof(BongoFrame));

    internal BongoFrame(int width, int height, byte[] pixels, int byteCount, ArrayPool<byte>? pool = null)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width is < 1 or > 1024 || height is < 1 or > 1024
            || byteCount != (long)width * height * 4 || byteCount > pixels.Length)
            throw new ArgumentException("Invalid bounded Bongo frame.");
        Width = width; Height = height; ByteCount = byteCount;
        this.pixels = pixels; this.pool = pool;
    }

    internal BongoFrame Clone()
    {
        var source = Pixels;
        var copy = ArrayPool<byte>.Shared.Rent(ByteCount);
        source.AsSpan(0, ByteCount).CopyTo(copy);
        return new(Width, Height, copy, ByteCount, ArrayPool<byte>.Shared);
    }

    public void Dispose()
    {
        var owned = Interlocked.Exchange(ref pixels, null);
        if (owned is not null && pool is not null) pool.Return(owned);
    }
}
