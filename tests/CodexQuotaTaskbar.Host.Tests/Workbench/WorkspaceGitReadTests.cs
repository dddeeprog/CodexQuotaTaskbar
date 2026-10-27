using System.IO;
using CodexQuotaTaskbar.Host.Workbench;

namespace CodexQuotaTaskbar.Host.Tests.Workbench;

public sealed class WorkspaceGitReadTests
{
    [Fact]
    public void Porcelain_handles_rename_unicode_spaces_and_mixed_index_worktree_rows()
    {
        var rows = WorkspaceGit.ParseStatus("RM 新 [1].txt\0old.txt\0?? x y.txt\0UU conflict.txt\0");
        Assert.Equal(4, rows.Count);
        Assert.Equal("old.txt", rows[0].OriginalPath); Assert.Equal(GitArea.Staged, rows[0].Area);
        Assert.Equal(GitArea.Worktree, rows[1].Area); Assert.Equal("x y.txt", rows[2].Path); Assert.Equal(GitArea.Conflict, rows[3].Area);
    }
    [Fact]
    public async Task Reads_staged_and_unstaged_diff_without_changing_the_index()
    {
        await using var fixture = new GitTestWorkspace(); await fixture.InitializeAsync(); var git = new WorkspaceGit();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "sample.txt"), "staged\n"); await GitTestWorkspace.RunAsync(fixture.Root, "add", "--", "sample.txt");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "sample.txt"), "working\n");
        var before = await GitTestWorkspace.RunAsync(fixture.Root, "ls-files", "--stage");
        var snapshot = await git.ReadAsync(fixture.Root, default); Assert.Equal(2, snapshot.Changes.Count);
        var staged = await git.DiffAsync(snapshot, snapshot.Changes.Single(c => c.Area == GitArea.Staged), default);
        var working = await git.DiffAsync(snapshot, snapshot.Changes.Single(c => c.Area == GitArea.Worktree), default);
        Assert.Contains(staged.Rows, r => r.NewText == "staged"); Assert.Contains(working.Rows, r => r.NewText == "working");
        Assert.Equal(before, await GitTestWorkspace.RunAsync(fixture.Root, "ls-files", "--stage"));
    }
    [Fact]
    public async Task Main_two_worktrees_and_nested_repo_keep_distinct_git_directories()
    {
        await using var fixture = new GitTestWorkspace(); await fixture.InitializeAsync();
        var first = await fixture.AddWorktreeAsync("worktree-a"); var second = await fixture.AddWorktreeAsync("worktree-b");
        var nested = Path.Combine(fixture.Root, "nested"); Directory.CreateDirectory(nested); await GitTestWorkspace.RunAsync(nested, "init", "-b", "nested");
        var git = new WorkspaceGit(); var main = await git.ReadAsync(fixture.Root, default);
        var a = await git.ReadAsync(first, default); var b = await git.ReadAsync(second, default); var child = await git.ReadAsync(nested, default);
        Assert.Equal(Path.GetFullPath(first), a.Root); Assert.Equal(Path.GetFullPath(second), b.Root);
        Assert.NotEqual(main.GitDirectory, a.GitDirectory); Assert.NotEqual(a.GitDirectory, b.GitDirectory);
        Assert.Null(child.Head); Assert.Equal("nested", child.Branch); Assert.Equal("detached HEAD", a.Branch);
        Assert.Equal(3, (await git.WorktreesAsync(main, default)).Count);
    }
    [Fact]
    public async Task External_index_or_working_file_changes_invalidate_the_review_version()
    {
        await using var fixture = new GitTestWorkspace(); await fixture.InitializeAsync(); var git = new WorkspaceGit();
        var before = await git.ReadAsync(fixture.Root, default);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "sample.txt"), "changed\n"); var modified = await git.ReadAsync(fixture.Root, default);
        Assert.NotEqual(before.Version, modified.Version);
        await GitTestWorkspace.RunAsync(fixture.Root, "add", "--", "sample.txt");
        Assert.NotEqual(modified.Version, (await git.ReadAsync(fixture.Root, default)).Version);
    }
    [Fact]
    public async Task Missing_git_is_a_clear_file_feature_independent_error()
    {
        await using var fixture = new GitTestWorkspace(); await fixture.InitializeAsync();
        var error = await Assert.ThrowsAsync<IOException>(() => new WorkspaceGit(Path.Combine(fixture.Parent, "missing.exe")).ReadAsync(fixture.Root, default));
        Assert.Contains("Git", error.Message);
    }
}
