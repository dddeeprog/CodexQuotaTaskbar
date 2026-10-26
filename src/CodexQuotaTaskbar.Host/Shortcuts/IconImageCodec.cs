using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexQuotaTaskbar.Host.Shortcuts;

// Header bounds precede WIC decoding; only a small, re-encoded PNG reaches the UI/cache.
internal static class IconImageCodec
{
    internal const int MaximumBytes = 128 * 1024;
    internal const int MaximumSourceSide = 1024;
    internal const int MaximumOutputSide = 64;
    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];

    internal static byte[]? ConvertToPng(byte[] bytes, string mediaType, int maximumBytes = MaximumBytes, int maximumSourceSide = MaximumSourceSide)
    {
        if (maximumBytes < 1 || maximumSourceSide < 1 || bytes.Length == 0 || bytes.Length > maximumBytes) return null;
        var type = mediaType.Split(';', 2)[0].Trim().ToLowerInvariant();
        var valid = type switch
        {
            "image/png" => ValidPng(bytes, maximumSourceSide),
            "image/jpeg" => ValidJpeg(bytes, maximumSourceSide),
            "image/x-icon" or "image/vnd.microsoft.icon" => ValidIco(bytes),
            _ => false,
        };
        if (!valid) return null;
        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count is 0 or > 64) return null;
            BitmapSource bitmap = decoder.Frames[0];
            if (bitmap.PixelWidth <= 0 || bitmap.PixelWidth > maximumSourceSide || bitmap.PixelHeight <= 0 || bitmap.PixelHeight > maximumSourceSide) return null;
            var scale = Math.Min(1d, (double)MaximumOutputSide / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight));
            if (scale < 1) bitmap = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
            bitmap.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.Length <= MaximumBytes ? output.ToArray() : null;
        }
        catch (Exception error) when (error is IOException or FormatException or NotSupportedException or ArgumentException or InvalidOperationException or COMException)
        {
            return null;
        }
    }

    internal static BitmapSource? ReadCachedPng(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaximumBytes || !ValidPng(bytes, MaximumOutputSide)) return null;
        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            var decoder = new PngBitmapDecoder(input, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count != 1) return null;
            var bitmap = decoder.Frames[0];
            if (bitmap.PixelWidth is <= 0 or > MaximumOutputSide || bitmap.PixelHeight is <= 0 or > MaximumOutputSide) return null;
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception error) when (error is IOException or FormatException or NotSupportedException or ArgumentException or InvalidOperationException or COMException)
        {
            return null;
        }
    }

    private static bool ValidPng(ReadOnlySpan<byte> data, int maximumSide) => data.Length >= 33
        && data[..8].SequenceEqual(PngSignature) && BinaryPrimitives.ReadUInt32BigEndian(data[8..]) == 13
        && data.Slice(12, 4).SequenceEqual("IHDR"u8)
        && BinaryPrimitives.ReadUInt32BigEndian(data[16..]) is > 0 and var width && width <= maximumSide
        && BinaryPrimitives.ReadUInt32BigEndian(data[20..]) is > 0 and var height && height <= maximumSide;

    private static bool ValidJpeg(ReadOnlySpan<byte> data, int maximumSide)
    {
        if (data.Length < 4 || data[0] != 0xff || data[1] != 0xd8) return false;
        var position = 2;
        while (position < data.Length)
        {
            if (data[position++] != 0xff) return false;
            while (position < data.Length && data[position] == 0xff) position++;
            if (position >= data.Length) return false;
            var marker = data[position++];
            if (marker is 0xd9 or 0xda or 0) return false;
            if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
            if (position + 2 > data.Length) return false;
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
            if (length < 2 || position + length > data.Length) return false;
            if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
            {
                if (length < 8) return false;
                var height = BinaryPrimitives.ReadUInt16BigEndian(data[(position + 3)..]);
                var width = BinaryPrimitives.ReadUInt16BigEndian(data[(position + 5)..]);
                return width > 0 && width <= maximumSide && height > 0 && height <= maximumSide;
            }
            position += length;
        }
        return false;
    }

    private static bool ValidIco(ReadOnlySpan<byte> data)
    {
        if (data.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(data) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(data[2..]) != 1) return false;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        var directoryEnd = 6 + 16 * count;
        if (count is 0 or > 64 || data.Length < directoryEnd) return false;
        for (var index = 0; index < count; index++)
        {
            var entry = data.Slice(6 + 16 * index, 16);
            var width = entry[0] == 0 ? 256 : entry[0];
            var height = entry[1] == 0 ? 256 : entry[1];
            var size = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (size == 0 || offset < directoryEnd || (ulong)offset + size > (ulong)data.Length) return false;
            var image = data.Slice((int)offset, (int)size);
            if (image.Length >= 8 && image[..8].SequenceEqual(PngSignature))
            {
                if (!ValidPng(image, 256) || BinaryPrimitives.ReadUInt32BigEndian(image[16..]) != width
                    || BinaryPrimitives.ReadUInt32BigEndian(image[20..]) != height) return false;
            }
            else
            {
                if (image.Length < 40) return false;
                var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(image);
                var dibWidth = BinaryPrimitives.ReadInt32LittleEndian(image[4..]);
                var dibHeight = BinaryPrimitives.ReadInt32LittleEndian(image[8..]);
                var planes = BinaryPrimitives.ReadUInt16LittleEndian(image[12..]);
                var depth = BinaryPrimitives.ReadUInt16LittleEndian(image[14..]);
                var compression = BinaryPrimitives.ReadUInt32LittleEndian(image[16..]);
                var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(image[20..]);
                var paletteSize = BinaryPrimitives.ReadUInt32LittleEndian(image[32..]);
                if (headerSize is < 40 || headerSize > image.Length || dibWidth != width || dibHeight != height * 2
                    || planes != 1 || depth is not (1 or 4 or 8 or 16 or 24 or 32) || compression is not (0 or 3)
                    || declaredSize > image.Length - headerSize || paletteSize > 256) return false;
            }
        }
        return true;
    }
}
