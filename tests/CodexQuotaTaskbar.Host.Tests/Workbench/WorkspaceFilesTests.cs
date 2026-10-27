using System.IO;
using CodexQuotaTaskbar.Host.Workbench;

namespace CodexQuotaTaskbar.Host.Tests.Workbench;

public sealed class WorkspaceFilesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "DianDaoWorkbench-tests", Guid.NewGuid().ToString("N"));
    private readonly WorkspaceFiles files = new();
    public WorkspaceFilesTests() => Directory.CreateDirectory(root);
    [Fact]
    public async Task Tree_and_search_skip_git_and_preserve_unicode_paths()
    {
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        await File.WriteAllTextAsync(Path.Combine(root, ".git", "private.txt"), "needle");
        var path = Path.Combine(root, "中文 [1].cs"); await File.WriteAllTextAsync(path, "first\nneedle 中文\nlast");
        Assert.Single(await files.ListAsync(root, root, default));
        Assert.Single((await files.SearchAsync(root, "中文", false, default)).Hits);
        var hit = Assert.Single((await files.SearchAsync(root, "needle", true, default)).Hits);
        Assert.Equal(path, hit.Path); Assert.Equal(2, hit.Line);
    }
    [Fact]
    public async Task Text_preview_and_search_are_bounded_and_binary_is_not_text()
    {
        var large = Path.Combine(root, "large.txt");
        await File.WriteAllBytesAsync(large, new byte[WorkspaceFiles.MaximumTextBytes + 1]);
        Assert.NotNull((await files.PreviewAsync(root, large, default)).Notice);
        var binary = Path.Combine(root, "binary.bin"); await File.WriteAllBytesAsync(binary, [0, 1, 2]);
        Assert.Null((await files.PreviewAsync(root, binary, default)).Text);
        var matches = Path.Combine(root, "matches.txt"); await File.WriteAllLinesAsync(matches, Enumerable.Repeat("needle", 700));
        var result = await files.SearchAsync(root, "needle", true, default);
        Assert.Equal(500, result.Hits.Count); Assert.True(result.Truncated);
    }
    [Fact]
    public async Task Image_decode_is_frozen_bounded_on_both_axes_and_does_not_upscale()
    {
        foreach (var height in new[] { 64, 3200 })
        {
            var pixels = new byte[16 * height * 4];
            var source = System.Windows.Media.Imaging.BitmapSource.Create(16, height, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, pixels, 16 * 4);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
            using var stream = new MemoryStream(); encoder.Save(stream);
            var bitmap = await WorkspaceFiles.DecodeImageAsync(stream.ToArray(), default);
            Assert.True(bitmap.IsFrozen);
            Assert.Equal(height <= 1600 ? 16 : 8, bitmap.PixelWidth);
            Assert.Equal(Math.Min(height, 1600), bitmap.PixelHeight);
        }
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorkspaceFiles.DecodeImageAsync([], cancel.Token));
    }
    [Fact]
    public async Task Cancelled_reads_and_outside_paths_are_rejected()
    {
        Assert.Throws<IOException>(() => WorkspacePaths.Validate(root, Path.Combine(root, "..", "outside.txt")));
        Assert.Throws<IOException>(() => WorkspacePaths.Validate(root, Path.Combine(root, ".git", "index")));
        var internalRoot = Path.Combine(root, ".git"); Directory.CreateDirectory(internalRoot);
        Assert.Throws<IOException>(() => WorkspacePaths.Validate(internalRoot, internalRoot));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.ListAsync(root, root, cancel.Token));
    }
    [Fact]
    public async Task Directory_junction_is_not_followed()
    {
        var target = Path.Combine(root, "real"); Directory.CreateDirectory(target);
        var link = Path.Combine(root, "link");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(link); start.ArgumentList.Add(target);
        using var process = System.Diagnostics.Process.Start(start)!; await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        Assert.Throws<IOException>(() => WorkspacePaths.Validate(root, Path.Combine(link, "x")));
        Directory.Delete(link); // remove only this test's junction, never traverse its target
    }
    public void Dispose()
    {
        var full = Path.GetFullPath(root); var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DianDaoWorkbench-tests"));
        if (!full.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid test cleanup root.");
        Directory.Delete(full, true);
    }
}
