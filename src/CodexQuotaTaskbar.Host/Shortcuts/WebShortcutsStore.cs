using System.IO;
using System.Text.Json;

namespace CodexQuotaTaskbar.Host.Shortcuts;

/// <summary>Stores only this application's explicitly chosen website shortcuts.</summary>
public sealed class WebShortcutsStore
{
    internal const int MaximumItems = 48;
    internal const int MaximumFileBytes = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 8 };
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    private WebShortcutsSnapshot current = Defaults();

    public WebShortcutsStore(string? filePath = null) => path = Path.GetFullPath(filePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaTaskbar", "web-shortcuts.json"));

    public async Task<WebShortcutsSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var read = await ReadAsync(cancellationToken).ConfigureAwait(false);
            current = read.Snapshot;
            return current;
        }
        finally { gate.Release(); }
    }

    public async Task<WebShortcutsSnapshot> SaveAsync(IReadOnlyList<WebShortcut> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryNormalizeItems(items, out var normalized))
                return current with { ErrorMessage = "网站列表无效或超过 48 项，未覆盖已保存内容。" };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new StoreDocument(1, normalized), JsonOptions);
            if (bytes.Length > MaximumFileBytes)
                return current with { ErrorMessage = "网站列表过大，未覆盖已保存内容。" };

            // Recheck the file immediately before a user-requested write. Corrupt originals are retained.
            var previous = await ReadAsync(cancellationToken).ConfigureAwait(false);
            current = previous.Snapshot;
            if (previous.Unreadable)
                return current with { ErrorMessage = "无法安全读取原网站配置，未保存更改，请先检查配置文件。" };
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            if (previous.Corrupt && previous.Bytes is { } original)
            {
                var backup = path + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N") + ".bak";
                await using var backupFile = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
                await backupFile.WriteAsync(original, cancellationToken).ConfigureAwait(false);
                await backupFile.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            current = new(Array.AsReadOnly(normalized));
            return current;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return current with { ErrorMessage = "网站配置未能保存，已保留原内容，请稍后重试。" };
        }
        finally
        {
            if (temporary is not null)
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            gate.Release();
        }
    }

    private async Task<ReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        byte[]? bytes = null;
        try
        {
            if (Directory.Exists(path)) return new(current with { ErrorMessage = "网站配置路径不是文件。" }, false, true, null);
            await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous))
            {
                if (input.Length > MaximumFileBytes)
                    return new(current with { ErrorMessage = "网站配置超过安全大小，原文件已保留。" }, false, true, null);
                using var buffer = new MemoryStream();
                var block = new byte[4096];
                while (true)
                {
                    var count = await input.ReadAsync(block, cancellationToken).ConfigureAwait(false);
                    if (count == 0) break;
                    if (buffer.Length + count > MaximumFileBytes)
                        return new(current with { ErrorMessage = "网站配置超过安全大小，原文件已保留。" }, false, true, null);
                    buffer.Write(block, 0, count);
                }
                bytes = buffer.ToArray();
            }
            var document = JsonSerializer.Deserialize<StoreDocument>(bytes, JsonOptions);
            if (document is null || document.Schema != 1 || document.Items is null
                || !TryNormalizeItems(document.Items, out var items))
                return new(current with { ErrorMessage = "网站配置格式异常，原文件已保留；保存新列表前会先备份。" }, true, false, bytes);
            return new(new(Array.AsReadOnly(items)), false, false, bytes);
        }
        catch (JsonException)
        {
            return new(current with { ErrorMessage = "网站配置损坏，原文件已保留；保存新列表前会先备份。" }, true, false, bytes);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(Defaults(), false, false, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new(current with { ErrorMessage = "暂时无法读取网站配置，原文件未更改。" }, false, true, null);
        }
    }

    private static bool TryNormalizeItems(IReadOnlyList<WebShortcut> source, out WebShortcut[] items)
    {
        items = [];
        if (source.Count > MaximumItems) return false;
        var result = new List<WebShortcut>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var urls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in source)
        {
            if (item is null || !Guid.TryParse(item.Id, out var id) || item.TargetUri is null
                || item.ManualIconKey is not null && !WebShortcutManualIconStore.IsValidKey(item.ManualIconKey)
                || !WebShortcut.TryNormalizeUrl(item.TargetUri.OriginalString, out var uri, out _)) return false;
            var canonicalId = id.ToString("D");
            if (!ids.Add(canonicalId)) return false;
            var target = uri!;
            if (!urls.Add(target.AbsoluteUri)) continue;
            var name = string.Join(' ', (item.Name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (name.Length == 0) name = WebShortcut.DisplayNameFor(target);
            if (name.Length > 80) name = name[..80];
            result.Add(new(canonicalId, name, target) { ManualIconKey = item.ManualIconKey });
        }
        items = result.ToArray();
        return true;
    }

    private static WebShortcutsSnapshot Defaults(string? error = null) => new(Array.AsReadOnly(new[]
    {
        new WebShortcut("a8d54abc-11e3-4bbc-9af8-27954c45e33c", "重置公告", new Uri("https://codex-resets.com/zh-CN")),
    }), error);

    private sealed record StoreDocument(int Schema, WebShortcut[] Items);
    private sealed record ReadResult(WebShortcutsSnapshot Snapshot, bool Corrupt, bool Unreadable, byte[]? Bytes);
}
