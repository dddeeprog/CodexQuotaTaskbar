using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexQuotaTaskbar.Host.Shortcuts;

namespace CodexQuotaTaskbar.Host.Tests.Shortcuts;

public sealed class IconImageCodecTests
{
    [Theory]
    [InlineData(16, 8)]
    [InlineData(64, 64)]
    [InlineData(256, 128)]
    public void Accepted_png_is_reencoded_to_a_small_frozen_image_without_upscaling(int width, int height)
    {
        var source = Png(width, height);
        var bytes = Assert.IsType<byte[]>(IconImageCodec.ConvertToPng(source, "image/png"));
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(IconImageCodec.ReadCachedPng(bytes));
        Assert.True(bitmap.IsFrozen);
        Assert.InRange(bitmap.PixelWidth, 1, 64);
        Assert.InRange(bitmap.PixelHeight, 1, 64);
        Assert.Equal(Math.Min(64, width), bitmap.PixelWidth);
        Assert.Equal((double)width / height, (double)bitmap.PixelWidth / bitmap.PixelHeight, 2);
        Assert.InRange(bytes.Length, 1, IconImageCodec.MaximumBytes);
    }

    [Fact]
    public void Jpeg_and_png_backed_ico_are_converted_to_png()
    {
        var png = Png(32, 32);
        var ico = Ico(png, 32, 32);
        Assert.NotNull(IconImageCodec.ReadCachedPng(Assert.IsType<byte[]>(IconImageCodec.ConvertToPng(ico, "image/vnd.microsoft.icon"))));

        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(16, 8, 96, 96, PixelFormats.Bgr24, null, new byte[16 * 8 * 3], 16 * 3)));
        using var output = new MemoryStream();
        encoder.Save(output);
        Assert.NotNull(IconImageCodec.ReadCachedPng(Assert.IsType<byte[]>(IconImageCodec.ConvertToPng(output.ToArray(), "image/jpeg"))));
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("image/gif")]
    [InlineData("text/html")]
    [InlineData("application/octet-stream")]
    public void Unsupported_media_types_cannot_be_sniffed_into_executable_or_other_content(string mediaType)
    {
        Assert.Null(IconImageCodec.ConvertToPng(Png(1, 1), mediaType));
    }

    [Fact]
    public void Bytes_must_match_the_declared_supported_image_format()
    {
        Assert.Null(IconImageCodec.ConvertToPng(Png(1, 1), "image/jpeg"));
        Assert.Null(IconImageCodec.ConvertToPng("<svg onload='anything'/>"u8.ToArray(), "image/png"));
        Assert.Null(IconImageCodec.ConvertToPng([], "image/png"));
        Assert.Null(IconImageCodec.ConvertToPng(new byte[IconImageCodec.MaximumBytes + 1], "image/png"));
    }

    [Theory]
    [InlineData(16, 0)]
    [InlineData(16, 1025)]
    [InlineData(20, 1025)]
    [InlineData(16, int.MaxValue)]
    public void Png_header_dimensions_are_bounded_before_decode(int offset, int value)
    {
        var bytes = Png(1, 1);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset, 4), value);
        Assert.Null(IconImageCodec.ConvertToPng(bytes, "image/png"));
    }

    [Fact]
    public void Cached_png_must_already_be_small_and_is_never_treated_as_a_new_source()
    {
        Assert.Null(IconImageCodec.ReadCachedPng(Png(65, 1)));
        Assert.Null(IconImageCodec.ReadCachedPng([]));
        Assert.Null(IconImageCodec.ReadCachedPng(new byte[IconImageCodec.MaximumBytes + 1]));
        var valid = Png(1, 1);
        Array.Resize(ref valid, 24);
        Assert.Null(IconImageCodec.ReadCachedPng(valid));
    }

    [Fact]
    public void Ico_directory_count_offsets_sizes_and_embedded_dimensions_are_validated()
    {
        var valid = Ico(Png(32, 32), 32, 32);
        var excessiveCount = valid.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(excessiveCount.AsSpan(4), 65);
        Assert.Null(IconImageCodec.ConvertToPng(excessiveCount, "image/x-icon"));
        var invalidOffset = valid.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(invalidOffset.AsSpan(18), uint.MaxValue);
        Assert.Null(IconImageCodec.ConvertToPng(invalidOffset, "image/x-icon"));
        var oversizedResource = valid.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(oversizedResource.AsSpan(14), uint.MaxValue);
        Assert.Null(IconImageCodec.ConvertToPng(oversizedResource, "image/x-icon"));
        Assert.Null(IconImageCodec.ConvertToPng(Ico(Png(1024, 1), 32, 32), "image/x-icon"));
        Assert.Null(IconImageCodec.ConvertToPng(Ico(Png(16, 16), 32, 32), "image/x-icon"));
    }

    [Fact]
    public void Ico_dib_height_and_width_cannot_override_the_safe_directory_dimensions()
    {
        var dib = new byte[40];
        BinaryPrimitives.WriteInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 65536);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 32);
        Assert.Null(IconImageCodec.ConvertToPng(Ico(dib, 32, 32), "image/x-icon"));
    }

    [Fact]
    public void Invalid_or_oversized_jpeg_headers_return_no_icon()
    {
        byte[] frame = [0xff, 0xd8, 0xff, 0xc0, 0, 8, 8, 0, 1, 0xff, 0xff, 1];
        Assert.Null(IconImageCodec.ConvertToPng(frame, "image/jpeg"));
        byte[] truncated = [0xff, 0xd8, 0xff, 0xe0, 0xff, 0xff];
        Assert.Null(IconImageCodec.ConvertToPng(truncated, "image/jpeg"));
        Assert.Null(IconImageCodec.ConvertToPng([0xff, 0xd8, 0xff, 0xd9], "image/jpeg"));
    }

    private static byte[] Png(int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static byte[] Ico(byte[] image, int width, int height)
    {
        var result = new byte[22 + image.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), 1);
        result[6] = width == 256 ? (byte)0 : (byte)width;
        result[7] = height == 256 ? (byte)0 : (byte)height;
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(12), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(14), (uint)image.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(18), 22);
        image.CopyTo(result, 22);
        return result;
    }
}
