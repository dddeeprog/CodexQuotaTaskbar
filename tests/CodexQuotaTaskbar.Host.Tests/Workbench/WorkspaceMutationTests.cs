using System.IO;
using CodexQuotaTaskbar.Host.Workbench;

namespace CodexQuotaTaskbar.Host.Tests.Workbench;

public sealed class WorkspaceMutationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "DianDaoWorkbench-tests", Guid.NewGuid().ToString("N"));
    public WorkspaceMutationTests() => Directory.CreateDirectory(root);
    [Theory]
    [InlineData("..")][InlineData(".git")][InlineData("x/y")][InlineData("NUL.txt")][InlineData("COM1")][InlineData(" name")]
    public void Names_cannot_escape_or_use_reserved_internal_names(string name) => Assert.Throws<IOException>(() => WorkspaceFiles.ValidateName(name));
    [Fact]
    public async Task Create_and_rename_never_overwrite_a_collision()
    {
        var files = new WorkspaceFiles();
        var first = await files.CreateAsync(root, root, "中文 [1].txt", false, default);
        await File.WriteAllTextAsync(first, "preserve");
        await Assert.ThrowsAsync<IOException>(() => files.CreateAsync(root, root, "中文 [1].txt", false, default));
        await files.CreateAsync(root, root, "other.txt", false, default);
        await Assert.ThrowsAsync<IOException>(() => files.RenameAsync(root, first, "other.txt", default));
        var renamed = await files.RenameAsync(root, first, "中文 [2].txt", default);
        Assert.Equal("preserve", await File.ReadAllTextAsync(renamed)); Assert.False(File.Exists(first));
    }
    [Fact]
    public async Task Recycling_failure_preserves_the_source_and_root_is_protected()
    {
        var path = Path.Combine(root, "keep.txt"); await File.WriteAllTextAsync(path, "keep");
        var files = new WorkspaceFiles(new FailingRecycle());
        await Assert.ThrowsAsync<IOException>(() => files.RecycleAsync(root, path, default));
        Assert.True(File.Exists(path));
        await Assert.ThrowsAsync<IOException>(() => files.RecycleAsync(root, root, default));
        Assert.False(NativeRecycleBin.IsRecycling(0)); Assert.True(NativeRecycleBin.IsRecycling(0x80));
    }
    [Fact]
    public async Task Nested_repository_directory_is_not_recursively_recycled()
    {
        var directory = Path.Combine(root, "repo"); Directory.CreateDirectory(Path.Combine(directory, ".git"));
        var files = new WorkspaceFiles(new FailingRecycle());
        await Assert.ThrowsAsync<IOException>(() => files.RecycleAsync(root, directory, default));
        Assert.True(Directory.Exists(directory));
    }
    [Fact]
    public async Task Native_recycle_sends_only_this_tests_small_file_to_the_recycle_bin()
    {
        var path = Path.Combine(root, "DianDao-disposable-recycle-proof.txt");
        await File.WriteAllTextAsync(path, "Synthetic test file, no user data.");
        await new WorkspaceFiles().RecycleAsync(root, path, default);
        Assert.False(File.Exists(path));
    }
    private sealed class FailingRecycle : IWorkspaceRecycleBin { public void Recycle(string path) => throw new IOException("Synthetic recycle unavailable."); }
    public void Dispose()
    {
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DianDaoWorkbench-tests"));
        if (!Path.GetFullPath(root).StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid test cleanup root.");
        Directory.Delete(root, true);
    }
}
