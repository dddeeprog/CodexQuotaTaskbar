using System.IO;

namespace CodexQuotaTaskbar.Host.Shortcuts;

public sealed record ManualIconImportResult(string? Key, WebShortcutIcon? Icon, string? ErrorMessage);

/// <summary>Imports only a user-selected local image; originals and original paths are never retained or changed.</summary>
public sealed class WebShortcutManualIconStore
{
    internal const int MaximumImportBytes = 4 * 1024 * 1024;
    internal const int MaximumImportSide = 4096;
    private readonly string cacheDirectory;
    private readonly SemaphoreSlim gate = new(1, 1);

    public WebShortcutManualIconStore(string? cacheDirectory = null) => this.cacheDirectory = Path.GetFullPath(cacheDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaTaskbar", "shortcut-manual-icons"));

    public static bool IsValidKey(string? key) => key is { Length: 36 }
        && key.EndsWith(".png", StringComparison.Ordinal)
        && key.AsSpan(0, 32).IndexOfAnyExcept("0123456789abcdef".AsSpan()) < 0;

    public Task<ManualIconImportResult> ImportAsync(string sourcePath, CancellationToken cancellationToken = default) =>
        Task.Run(() => ImportCoreAsync(sourcePath, cancellationToken), cancellationToken);

    private async Task<ManualIconImportResult> ImportCoreAsync(string sourcePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) return Failure("请选择 PNG、JPEG 或 ICO 图片。");
            var original = Path.GetFullPath(sourcePath);
            if (!SafeLocalPath(original, requireDirectory: false, allowMissing: false))
                return Failure("请选择本地普通图片文件；不读取链接或重定向路径。");
            var bytes = await ReadBoundedAsync(original, MaximumImportBytes, cancellationToken).ConfigureAwait(false);
            if (bytes is null) return Failure("图片不能为空或大于 4 MiB。");
            var mediaType = DetectType(bytes);
            if (mediaType is null) return Failure("仅支持 PNG、JPEG 或 ICO 图片。");
            var converted = await Task.Run(() =>
            {
                var png = IconImageCodec.ConvertToPng(bytes, mediaType, MaximumImportBytes, MaximumImportSide);
                var image = png is null ? null : IconImageCodec.ReadCachedPng(png);
                return (Png: png, Image: image);
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (converted.Png is null || converted.Image is null)
                return Failure("图片损坏或尺寸过大；PNG/JPEG 每边最多 4096 像素。");
            if (!SafeLocalPath(cacheDirectory, requireDirectory: true, allowMissing: true))
                return Failure("图标缓存路径不安全，未导入图片。");
            Directory.CreateDirectory(cacheDirectory);
            if (!SafeLocalPath(cacheDirectory, requireDirectory: true, allowMissing: false))
                return Failure("图标缓存路径不安全，未导入图片。");
            var key = Guid.NewGuid().ToString("N") + ".png";
            var target = Path.Combine(cacheDirectory, key);
            temporary = Path.Combine(cacheDirectory, ".manual-" + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await output.WriteAsync(converted.Png, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!SafeLocalPath(temporary, requireDirectory: false, allowMissing: false)
                || !SafeLocalPath(target, requireDirectory: false, allowMissing: true))
                return Failure("图标缓存路径已变化，未导入图片。");
            File.Move(temporary, target, overwrite: false);
            return new(key, new WebShortcutIcon(null, target) { Image = converted.Image }, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return Failure("无法导入这张图片，原图未更改。请选择可读取的本地图片重试。");
        }
        finally
        {
            if (temporary is not null && SafeLocalPath(temporary, requireDirectory: false, allowMissing: false))
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            gate.Release();
        }
    }

    public Task<WebShortcutIcon?> LoadAsync(string key, CancellationToken cancellationToken = default) =>
        Task.Run(() => LoadCoreAsync(key, cancellationToken), cancellationToken);

    private async Task<WebShortcutIcon?> LoadCoreAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValidKey(key)) return null;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(cacheDirectory, key);
            if (!SafeLocalPath(path, requireDirectory: false, allowMissing: false)) return null;
            var bytes = await ReadBoundedAsync(path, IconImageCodec.MaximumBytes, cancellationToken).ConfigureAwait(false);
            if (bytes is null) return null;
            var image = await Task.Run(() => IconImageCodec.ReadCachedPng(bytes), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return image is null ? null : new WebShortcutIcon(null, path) { Image = image };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
        finally { gate.Release(); }
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        Task.Run(() => DeleteCoreAsync(key, cancellationToken), cancellationToken);

    private async Task DeleteCoreAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValidKey(key)) return;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(cacheDirectory, key);
            cancellationToken.ThrowIfCancellationRequested();
            if (SafeLocalPath(path, requireDirectory: false, allowMissing: false)) File.Delete(path);
        }
        finally { gate.Release(); }
    }

    private static ManualIconImportResult Failure(string message) => new(null, null, message);

    private static string? DetectType(ReadOnlySpan<byte> bytes) => bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
        ? "image/png" : bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xd8
            ? "image/jpeg" : bytes.Length >= 4 && bytes[..4].SequenceEqual(new byte[] { 0, 0, 1, 0 })
                ? "image/x-icon" : null;

    private static async Task<byte[]?> ReadBoundedAsync(string path, int limit, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (input.Length is <= 0 || input.Length > limit) return null;
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (bytes.Length + count > limit) return null;
            bytes.Write(buffer, 0, count);
        }
        return bytes.ToArray();
    }

    // Check the complete ancestor chain so a valid-looking cache filename cannot traverse a junction.
    private static bool SafeLocalPath(string path, bool requireDirectory, bool allowMissing)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (full.StartsWith("\\\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(full)) return false;
            var current = full;
            var isLeaf = true;
            while (!string.IsNullOrEmpty(current))
            {
                try
                {
                    var attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                    var mustBeDirectory = !isLeaf || requireDirectory;
                    if (mustBeDirectory != ((attributes & FileAttributes.Directory) != 0)) return false;
                }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
                {
                    if (isLeaf && !allowMissing) return false;
                }
                isLeaf = false;
                current = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
