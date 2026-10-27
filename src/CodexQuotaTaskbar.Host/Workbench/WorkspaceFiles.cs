using System.IO;
using System.Text;

namespace CodexQuotaTaskbar.Host.Workbench;

internal sealed record WorkspaceEntry(string Path, string Name, bool IsDirectory, bool IsLink);
internal sealed record WorkspaceSearchHit(string Path, int? Line, string Text)
{ public override string ToString() => Line is { } line ? $"{System.IO.Path.GetFileName(Path)}:{line}  {Text}" : Text; }
internal sealed record WorkspaceSearchResult(IReadOnlyList<WorkspaceSearchHit> Hits, bool Truncated);
internal sealed record WorkspacePreview(string Path, string? Text, byte[]? Image, string? Notice);

internal static class WorkspacePaths
{
    internal static string Validate(string root, string path, bool allowRoot = true)
    {
        root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
        path = System.IO.Path.GetFullPath(path);
        var relative = System.IO.Path.GetRelativePath(root, path);
        if (System.IO.Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || (!allowRoot && relative == ".")) throw new IOException("操作路径不在所选工作区内，或正在操作工作区根目录。");
        if (relative.Split(System.IO.Path.DirectorySeparatorChar).Any(p => p.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Git 内部目录不能从工作台直接操作。");
        for (var current = path; !string.IsNullOrEmpty(current); current = System.IO.Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("此路径经过符号链接或目录联接，请直接选择真实目录。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return path;
    }
}

internal sealed partial class WorkspaceFiles
{
    internal const int MaximumTextBytes = 2 * 1024 * 1024;
    internal const int MaximumSearchHits = 500;
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico" };
    private static readonly HashSet<string> SearchExcludedDirectories = new(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", "bin", "obj", "target", ".vs" };

    internal Task<IReadOnlyList<WorkspaceEntry>> ListAsync(string root, string path, CancellationToken token) => Task.Run<IReadOnlyList<WorkspaceEntry>>(() =>
    {
        path = WorkspacePaths.Validate(root, path);
        var entries = new List<WorkspaceEntry>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            token.ThrowIfCancellationRequested();
            if (System.IO.Path.GetFileName(entry).Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var attributes = File.GetAttributes(entry);
                entries.Add(new(entry, System.IO.Path.GetFileName(entry), (attributes & FileAttributes.Directory) != 0, (attributes & FileAttributes.ReparsePoint) != 0));
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }, token);

    internal Task<WorkspacePreview> PreviewAsync(string root, string path, CancellationToken token) => Task.Run(async () =>
    {
        path = WorkspacePaths.Validate(root, path, false);
        var image = ImageExtensions.Contains(System.IO.Path.GetExtension(path));
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, true);
        var maximum = image ? 20 * 1024 * 1024 : MaximumTextBytes;
        if (stream.Length > maximum) return new WorkspacePreview(path, null, null, image ? "图片超过 20 MiB，请用默认程序打开。" : "文件超过 2 MiB，请用默认程序打开。");
        using var memory = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (memory.Length + count > maximum) return new WorkspacePreview(path, null, null, "文件已超过预览上限，请用默认程序打开。");
            memory.Write(buffer, 0, count);
        }
        if (image) return new WorkspacePreview(path, null, memory.ToArray(), null);
        memory.Position = 0;
        try
        {
            using var reader = new StreamReader(memory, new UTF8Encoding(false, true), true);
            var text = await reader.ReadToEndAsync(token);
            return text.Contains('\0') ? new WorkspacePreview(path, null, null, "此文件不是可预览的文本，请用默认程序打开。") : new WorkspacePreview(path, text, null, null);
        }
        catch (DecoderFallbackException) { return new WorkspacePreview(path, null, null, "编码不受预览支持，请用默认程序打开。"); }
    }, token);

    internal Task<WorkspaceSearchResult> SearchAsync(string root, string query, bool content, CancellationToken token) => Task.Run(async () =>
    {
        root = WorkspacePaths.Validate(root, root);
        var hits = new List<WorkspaceSearchHit>();
        if (string.IsNullOrWhiteSpace(query)) return new WorkspaceSearchResult(hits, false);
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            IReadOnlyList<WorkspaceEntry> entries;
            try { entries = await ListAsync(root, directory, token); }
            catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.IsLink) continue;
                if (entry.IsDirectory) { if (!SearchExcludedDirectories.Contains(entry.Name)) pending.Push(entry.Path); continue; }
                if (!content)
                {
                    var relative = System.IO.Path.GetRelativePath(root, entry.Path);
                    if (relative.Contains(query, StringComparison.OrdinalIgnoreCase)) hits.Add(new(entry.Path, null, relative));
                }
                else
                {
                    try
                    {
                        var preview = await PreviewAsync(root, entry.Path, token);
                        if (preview.Text is not { } text) continue;
                        using var reader = new StringReader(text); string? line; var number = 0;
                        while ((line = reader.ReadLine()) is not null)
                        {
                            token.ThrowIfCancellationRequested(); number++;
                            if (line.Contains(query, StringComparison.OrdinalIgnoreCase)) hits.Add(new(entry.Path, number, line.Length > 240 ? line[..240] : line));
                            if (hits.Count >= MaximumSearchHits) return new WorkspaceSearchResult(hits, true);
                        }
                    }
                    catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                if (hits.Count >= MaximumSearchHits) return new WorkspaceSearchResult(hits, true);
            }
        }
        return new WorkspaceSearchResult(hits, false);
    }, token);
}
