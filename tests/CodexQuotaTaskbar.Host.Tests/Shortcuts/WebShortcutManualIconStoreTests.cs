using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexQuotaTaskbar.Host.Shortcuts;

namespace CodexQuotaTaskbar.Host.Tests.Shortcuts;

public sealed class ManualIconLinkFactAttribute : FactAttribute
{
    public ManualIconLinkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CQT_SHORTCUT_MANUAL_LINK_TESTS") != "1")
            Skip = "Opt-in isolated filesystem link test; never requests link-creation privileges.";
    }
}

public sealed class WebShortcutManualIconStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "diandao-manual-icon-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> links = [];
    private string CacheDirectory => Path.Combine(directory, "cache");
    private const string ValidKey = "0123456789abcdef0123456789abcdef.png";

    [Theory]
    [InlineData("png")]
    [InlineData("jpeg")]
    [InlineData("ico")]
    public async Task Real_supported_images_are_copied_as_small_frozen_pngs_and_reload_after_restart(string format)
    {
        var sourceBytes = format switch
        {
            "jpeg" => Jpeg(256, 128),
            "ico" => Ico(Png(32, 32), 32, 32),
            _ => Png(256, 128),
        };
        // The content determines the format; a user-selected local file need not have the expected extension.
        var source = await SourceAsync("selected.image", sourceBytes);
        var store = new WebShortcutManualIconStore(CacheDirectory);

        var result = await store.ImportAsync(source);

        Assert.Null(result.ErrorMessage);
        Assert.True(WebShortcutManualIconStore.IsValidKey(result.Key));
        var icon = Assert.IsType<WebShortcutIcon>(result.Icon);
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(icon.Image);
        Assert.True(bitmap.IsFrozen);
        Assert.InRange(bitmap.PixelWidth, 1, 64);
        Assert.InRange(bitmap.PixelHeight, 1, 64);
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(source));
        Assert.Equal(Path.Combine(CacheDirectory, result.Key!), icon.CachedPngPath);
        var stored = await File.ReadAllBytesAsync(icon.CachedPngPath!);
        Assert.True(stored.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        Assert.InRange(stored.Length, 1, IconImageCodec.MaximumBytes);
        Assert.Single(Directory.GetFiles(CacheDirectory));
        Assert.DoesNotContain(Directory.GetFiles(CacheDirectory), path => !path.EndsWith(".png", StringComparison.Ordinal));

        var reopened = await new WebShortcutManualIconStore(CacheDirectory).LoadAsync(result.Key!);
        var loaded = Assert.IsType<WebShortcutIcon>(reopened);
        Assert.True(Assert.IsAssignableFrom<BitmapSource>(loaded.Image).IsFrozen);
        Assert.Equal(icon.CachedPngPath, loaded.CachedPngPath);
    }

    [Fact]
    public async Task Manual_png_accepts_a_large_but_bounded_source_without_relaxing_the_http_codec_defaults()
    {
        var png = Png(2048, 1024);
        Assert.Null(IconImageCodec.ConvertToPng(png, "image/png"));
        var path = await SourceAsync("large.png", png);

        var imported = await new WebShortcutManualIconStore(CacheDirectory).ImportAsync(path);

        Assert.Null(imported.ErrorMessage);
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(Assert.IsType<WebShortcutIcon>(imported.Icon).Image);
        Assert.Equal(64, bitmap.PixelWidth);
        Assert.Equal(32, bitmap.PixelHeight);
        Assert.True(bitmap.IsFrozen);
        Assert.Equal(png, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Manual_jpeg_accepts_larger_dimensions_than_the_automatic_network_icon_limit()
    {
        var jpeg = Jpeg(2048, 1024);
        Assert.Null(IconImageCodec.ConvertToPng(jpeg, "image/jpeg"));
        var path = await SourceAsync("large.jpeg", jpeg);

        var imported = await new WebShortcutManualIconStore(CacheDirectory).ImportAsync(path);

        Assert.Null(imported.ErrorMessage);
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(Assert.IsType<WebShortcutIcon>(imported.Icon).Image);
        Assert.Equal(64, bitmap.PixelWidth);
        Assert.Equal(32, bitmap.PixelHeight);
    }

    [Fact]
    public async Task Deleting_an_import_removes_only_its_own_cache_file_and_never_the_selected_original()
    {
        var original = Png(40, 40);
        var source = await SourceAsync("personal-picture.png", original);
        var sourceHash = SHA256.HashData(original);
        var store = new WebShortcutManualIconStore(CacheDirectory);
        var imported = await store.ImportAsync(source);
        Assert.Null(imported.ErrorMessage);
        var unrelated = Path.Combine(CacheDirectory, "do-not-delete.txt");
        await File.WriteAllTextAsync(unrelated, "keep");

        await store.DeleteAsync(imported.Key!);

        Assert.Null(await new WebShortcutManualIconStore(CacheDirectory).LoadAsync(imported.Key!));
        Assert.False(File.Exists(Path.Combine(CacheDirectory, imported.Key!)));
        Assert.Equal(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated));
        Assert.Empty(Directory.GetFiles(CacheDirectory, "*.tmp"));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef.png", true)]
    [InlineData("ffffffffffffffffffffffffffffffff.png", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0123456789abcdef0123456789abcde.png", false)]
    [InlineData("0123456789abcdef0123456789abcdef0.png", false)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF.png", false)]
    [InlineData("0123456789abcdef0123456789abcdef.PNG", false)]
    [InlineData("g123456789abcdef0123456789abcdef.png", false)]
    [InlineData("../0123456789abcdef0123456789abcdef.png", false)]
    [InlineData("..\\0123456789abcdef0123456789abcdef.png", false)]
    [InlineData("C:\\0123456789abcdef0123456789abcdef.png", false)]
    public void Only_exact_lowercase_owned_cache_keys_are_accepted(string? key, bool valid) =>
        Assert.Equal(valid, WebShortcutManualIconStore.IsValidKey(key));

    [Theory]
    [InlineData("../source/original.png")]
    [InlineData("..\\source\\original.png")]
    [InlineData("original.png")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF.png")]
    public async Task Invalid_cache_keys_cannot_read_or_delete_a_local_source(string key)
    {
        var source = await SourceAsync("original.png", Png(8, 8));
        var before = await File.ReadAllBytesAsync(source);
        var store = new WebShortcutManualIconStore(CacheDirectory);

        Assert.Null(await store.LoadAsync(key));
        await store.DeleteAsync(key);

        Assert.Equal(before, await File.ReadAllBytesAsync(source));
        Assert.False(Directory.Exists(CacheDirectory));
    }

    [Fact]
    public async Task Corrupt_or_oversized_cached_images_are_not_decoded_into_the_ui()
    {
        Directory.CreateDirectory(CacheDirectory);
        var path = Path.Combine(CacheDirectory, ValidKey);
        var store = new WebShortcutManualIconStore(CacheDirectory);
        await File.WriteAllTextAsync(path, "not an icon");
        Assert.Null(await store.LoadAsync(ValidKey));
        Assert.Equal("not an icon", await File.ReadAllTextAsync(path));

        await File.WriteAllBytesAsync(path, new byte[IconImageCodec.MaximumBytes + 1]);
        Assert.Null(await store.LoadAsync(ValidKey));
        Assert.Equal(IconImageCodec.MaximumBytes + 1, new FileInfo(path).Length);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("svg")]
    [InlineData("gif")]
    [InlineData("random")]
    [InlineData("truncated-png")]
    public async Task Unsupported_or_bad_image_content_does_not_create_a_cached_icon(string kind)
    {
        var bytes = kind switch
        {
            "empty" => Array.Empty<byte>(),
            "svg" => Encoding.UTF8.GetBytes("<svg onload='must-not-run'/>"),
            "gif" => "GIF89a"u8.ToArray(),
            "truncated-png" => Png(2, 2)[..24],
            _ => Encoding.UTF8.GetBytes("a regular document"),
        };
        var source = await SourceAsync("selected.png", bytes);

        var result = await new WebShortcutManualIconStore(CacheDirectory).ImportAsync(source);

        Assert.NotNull(result.ErrorMessage);
        Assert.Null(result.Key);
        Assert.Null(result.Icon);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
        Assert.False(Directory.Exists(CacheDirectory) && Directory.EnumerateFiles(CacheDirectory).Any());
    }

    [Fact]
    public async Task The_four_mebibyte_file_limit_preserves_the_source_and_creates_no_partial_cache()
    {
        var bytes = new byte[4 * 1024 * 1024 + 1];
        var source = await SourceAsync("too-large.png", bytes);

        var result = await new WebShortcutManualIconStore(CacheDirectory).ImportAsync(source);

        Assert.NotNull(result.ErrorMessage);
        Assert.Null(result.Key);
        Assert.Equal(bytes.Length, new FileInfo(source).Length);
        Assert.False(Directory.Exists(CacheDirectory) && Directory.EnumerateFiles(CacheDirectory).Any());
    }

    [Fact]
    public async Task Source_dimensions_above_4096_are_rejected_before_the_cached_image_is_created()
    {
        var source = await SourceAsync("wide.png", Png(4097, 1));

        var result = await new WebShortcutManualIconStore(CacheDirectory).ImportAsync(source);

        Assert.NotNull(result.ErrorMessage);
        Assert.Null(result.Key);
        Assert.False(Directory.Exists(CacheDirectory) && Directory.EnumerateFiles(CacheDirectory).Any());
    }

    [Fact]
    public async Task A_missing_selected_file_reports_a_recoverable_import_error()
    {
        var result = await new WebShortcutManualIconStore(CacheDirectory).ImportAsync(Path.Combine(directory, "missing.png"));

        Assert.NotNull(result.ErrorMessage);
        Assert.Null(result.Key);
        Assert.Null(result.Icon);
        Assert.False(Directory.Exists(CacheDirectory));
    }

    [Fact]
    public async Task A_cancelled_import_load_or_delete_does_not_modify_existing_files()
    {
        var original = Png(16, 16);
        var source = await SourceAsync("source.png", original);
        var store = new WebShortcutManualIconStore(CacheDirectory);
        var imported = await store.ImportAsync(source);
        Assert.Null(imported.ErrorMessage);
        var cachedPath = Path.Combine(CacheDirectory, imported.Key!);
        var cached = await File.ReadAllBytesAsync(cachedPath);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ImportAsync(source, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(imported.Key!, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteAsync(imported.Key!, cancelled.Token));

        Assert.Equal(cached, await File.ReadAllBytesAsync(cachedPath));
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
        Assert.Empty(Directory.GetFiles(CacheDirectory, "*.tmp"));
    }

    [ManualIconLinkFact]
    public async Task Cache_directory_links_are_not_followed_for_load_import_or_delete()
    {
        Directory.CreateDirectory(directory);
        var foreign = Path.Combine(directory, "foreign");
        Directory.CreateDirectory(foreign);
        var protectedPath = Path.Combine(foreign, ValidKey);
        await File.WriteAllBytesAsync(protectedPath, Png(16, 16));
        Directory.CreateSymbolicLink(CacheDirectory, foreign);
        links.Add(CacheDirectory);
        var source = await SourceAsync("selected.png", Png(32, 32));
        var before = await File.ReadAllBytesAsync(protectedPath);
        var store = new WebShortcutManualIconStore(CacheDirectory);

        Assert.Null(await store.LoadAsync(ValidKey));
        await store.DeleteAsync(ValidKey);
        Assert.NotNull((await store.ImportAsync(source)).ErrorMessage);

        Assert.Equal(before, await File.ReadAllBytesAsync(protectedPath));
        Assert.Single(Directory.GetFiles(foreign));
    }

    private async Task<string> SourceAsync(string fileName, byte[] bytes)
    {
        var sourceDirectory = Path.Combine(directory, "source");
        Directory.CreateDirectory(sourceDirectory);
        var path = Path.Combine(sourceDirectory, fileName);
        await File.WriteAllBytesAsync(path, bytes);
        return path;
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

    private static byte[] Jpeg(int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, new byte[width * height * 3], width * 3);
        var encoder = new JpegBitmapEncoder();
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

    public void Dispose()
    {
        var resolved = Path.GetFullPath(directory);
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("diandao-manual-icon-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing cleanup outside this test's isolated temporary directory.");
        foreach (var link in links)
            if (Directory.Exists(link)) Directory.Delete(link);
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
