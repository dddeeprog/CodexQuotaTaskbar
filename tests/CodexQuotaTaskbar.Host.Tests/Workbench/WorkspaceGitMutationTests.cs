using System.IO;
using CodexQuotaTaskbar.Host.Workbench;

namespace CodexQuotaTaskbar.Host.Tests.Workbench;

public sealed class WorkspaceGitMutationTests
{
    [Fact]
    public async Task Concurrent_writes_serialize_and_reject_the_second_stale_review()
    {
        await using var fixture = new GitTestWorkspace(); await fixture.InitializeAsync(); var git = new WorkspaceGit();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "one.txt"), "one"); await File.WriteAllTextAsync(Path.Combine(fixture.Root, "two.txt"), "two");
        var state = await git.ReadAsync(fixture.Root, default);
        async Task<bool> TryStage(GitChange change)
        { try { await git.StageAsync(state, [change], default); return true; } catch (IOException) { return false; } }
        var results = await Task.WhenAll(state.Changes.Select(TryStage));
        Assert.Single(results, success => success); Assert.Single((await git.ReadAsync(fixture.Root, default)).Changes, c => c.Area == GitArea.Staged);
    }
    [Fact]
    public async Task Same_commit_branch_switch_invalidates_the_review()
    {
        await using var fixture = new GitTestWorkspace(); await fixture.InitializeAsync(); var git = new WorkspaceGit();
        await GitTestWorkspace.RunAsync(fixture.Root, "branch", "other");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "sample.txt"), "changed"); var before = await git.ReadAsync(fixture.Root, default);
        await GitTestWorkspace.RunAsync(fixture.Root, "switch", "other"); var after = await git.ReadAsync(fixture.Root, default);
        Assert.Equal(before.Head, after.Head); Assert.NotEqual(before.Version, after.Version);
        await Assert.ThrowsAsync<IOException>(() => git.StageAsync(before, before.Changes, default));
    }
    [Fact]
    public async Task Signing_failure_is_not_bypassed_or_reconfigured()
    {
        await using var fixture = new GitTestWorkspace(); await fixture.InitializeAsync(); var git = new WorkspaceGit();
        await GitTestWorkspace.RunAsync(fixture.Root, "config", "commit.gpgsign", "true");
        await GitTestWorkspace.RunAsync(fixture.Root, "config", "gpg.program", Path.Combine(fixture.Parent, "no-signer.exe"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "sample.txt"), "changed"); var state = await git.ReadAsync(fixture.Root, default);
        state = await git.StageAsync(state, state.Changes, default);
        await Assert.ThrowsAsync<IOException>(() => git.CommitAsync(state, "must honor signing", default));
        Assert.Equal("true", (await GitTestWorkspace.RunAsync(fixture.Root, "config", "commit.gpgsign")).Trim());
        Assert.Equal(state.Head, (await git.ReadAsync(fixture.Root, default)).Head);
    }
    [Fact]
    public async Task Literal_path_staging_and_unstaging_preserve_other_files_and_working_contents()
    {
        await using var fixture = new GitTestWorkspace(); await fixture.InitializeAsync(); var git = new WorkspaceGit();
        var literal = "中文 [a].txt"; var similar = "中文 a.txt";
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, literal), "literal"); await File.WriteAllTextAsync(Path.Combine(fixture.Root, similar), "other");
        var reviewed = await git.ReadAsync(fixture.Root, default);
        var staged = await git.StageAsync(reviewed, [reviewed.Changes.Single(c => c.Path == literal)], default);
        Assert.Single(staged.Changes, c => c.Area == GitArea.Staged); Assert.Contains(staged.Changes, c => c.Path == similar && c.Area == GitArea.Untracked);
        var unstaged = await git.UnstageAsync(staged, [staged.Changes.Single(c => c.Area == GitArea.Staged)], default);
        Assert.All(unstaged.Changes, c => Assert.Equal(GitArea.Untracked, c.Area));
        Assert.Equal("literal", await File.ReadAllTextAsync(Path.Combine(fixture.Root, literal)));
    }
    [Fact]
    public async Task Unborn_repository_can_unstage_modified_new_file_and_make_first_commit()
    {
        await using var fixture = new GitTestWorkspace(); await fixture.InitializeAsync(false); var git = new WorkspaceGit();
        var path = Path.Combine(fixture.Root, "first.txt"); await File.WriteAllTextAsync(path, "first");
        var state = await git.ReadAsync(fixture.Root, default); state = await git.StageAsync(state, state.Changes, default);
        await File.WriteAllTextAsync(path, "newer"); state = await git.ReadAsync(fixture.Root, default);
        state = await git.UnstageAsync(state, state.Changes.Where(c => c.Area == GitArea.Staged).ToArray(), default);
        Assert.Null(state.Head); Assert.Equal("newer", await File.ReadAllTextAsync(path));
        state = await git.StageAsync(state, state.Changes, default); state = await git.CommitAsync(state, "首次提交\n\n多行说明", default);
        Assert.NotNull(state.Head); Assert.Empty(state.Changes);
    }
    [Fact]
    public async Task Commit_uses_only_staged_content_and_never_stages_newer_working_changes()
    {
        await using var fixture = new GitTestWorkspace(); await fixture.InitializeAsync(); var git = new WorkspaceGit();
        var path = Path.Combine(fixture.Root, "sample.txt"); await File.WriteAllTextAsync(path, "reviewed\n");
        var state = await git.ReadAsync(fixture.Root, default); state = await git.StageAsync(state, state.Changes, default);
        await File.WriteAllTextAsync(path, "keep working\n"); await File.WriteAllTextAsync(Path.Combine(fixture.Root, "keep-untracked.txt"), "untouched");
        state = await git.ReadAsync(fixture.Root, default); var head = state.Head;
        state = await git.CommitAsync(state, "Only staged\n\nLiteral `$() text remains data.", default);
        Assert.NotEqual(head, state.Head); Assert.Equal("reviewed\n", await GitTestWorkspace.RunAsync(fixture.Root, "show", "HEAD:sample.txt"));
        Assert.Equal("keep working\n", await File.ReadAllTextAsync(path));
        Assert.Contains(state.Changes, c => c.Area == GitArea.Untracked); Assert.DoesNotContain(state.Changes, c => c.Area == GitArea.Staged);
    }
    [Fact]
    public async Task A_stale_review_cannot_stage_or_commit_after_external_index_changes()
    {
        await using var fixture = new GitTestWorkspace(); await fixture.InitializeAsync(); var git = new WorkspaceGit();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "sample.txt"), "changed"); var stale = await git.ReadAsync(fixture.Root, default);
        await GitTestWorkspace.RunAsync(fixture.Root, "add", "--", "sample.txt");
        await Assert.ThrowsAsync<IOException>(() => git.StageAsync(stale, stale.Changes, default));
        await Assert.ThrowsAsync<IOException>(() => git.CommitAsync(stale, "must not commit", default));
        Assert.Equal(stale.Head, (await git.ReadAsync(fixture.Root, default)).Head);
    }
    [Fact]
    public async Task Existing_index_lock_is_reported_without_deleting_it()
    {
        await using var fixture = new GitTestWorkspace(); await fixture.InitializeAsync(); var git = new WorkspaceGit();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "sample.txt"), "changed"); var state = await git.ReadAsync(fixture.Root, default);
        var lockPath = Path.Combine(state.GitDirectory, "index.lock"); await File.WriteAllTextAsync(lockPath, "synthetic lock");
        await Assert.ThrowsAsync<IOException>(() => git.StageAsync(state, state.Changes, default));
        Assert.Equal("synthetic lock", await File.ReadAllTextAsync(lockPath)); File.Delete(lockPath);
    }
    [Fact]
    public async Task Failing_hook_preserves_head_and_index_without_skipping_the_hook()
    {
        await using var fixture = new GitTestWorkspace(); await fixture.InitializeAsync(); var git = new WorkspaceGit();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "sample.txt"), "changed"); var state = await git.ReadAsync(fixture.Root, default);
        state = await git.StageAsync(state, state.Changes, default);
        await File.WriteAllTextAsync(Path.Combine(state.GitDirectory, "hooks", "pre-commit"), "#!/bin/sh\necho SYNTHETIC-HOOK-REJECT >&2\nexit 1\n");
        var error = await Assert.ThrowsAsync<IOException>(() => git.CommitAsync(state, "must fail", default)); Assert.Contains("SYNTHETIC-HOOK-REJECT", error.Message);
        var after = await git.ReadAsync(fixture.Root, default); Assert.Equal(state.Head, after.Head); Assert.Contains(after.Changes, c => c.Area == GitArea.Staged);
    }
    [Fact]
    public async Task Detached_worktree_commit_changes_only_that_worktrees_head_and_index()
    {
        await using var fixture = new GitTestWorkspace(); await fixture.InitializeAsync();
        var first = await fixture.AddWorktreeAsync("one"); var second = await fixture.AddWorktreeAsync("two"); var git = new WorkspaceGit();
        var mainBefore = await git.ReadAsync(fixture.Root, default); var secondBefore = await git.ReadAsync(second, default);
        await File.WriteAllTextAsync(Path.Combine(first, "sample.txt"), "only first\n");
        var state = await git.ReadAsync(first, default); state = await git.StageAsync(state, state.Changes, default); state = await git.CommitAsync(state, "Only first worktree", default);
        Assert.NotEqual(mainBefore.Head, state.Head); Assert.Equal(mainBefore.Head, (await git.ReadAsync(fixture.Root, default)).Head);
        Assert.Equal(secondBefore.Version, (await git.ReadAsync(second, default)).Version); Assert.Empty(state.Changes);
    }
    [Fact]
    public async Task Deleted_and_renamed_files_can_be_staged_then_unstaged_without_restoring_files()
    {
        await using var fixture = new GitTestWorkspace(); await fixture.InitializeAsync(); var git = new WorkspaceGit();
        File.Move(Path.Combine(fixture.Root, "sample.txt"), Path.Combine(fixture.Root, "renamed.txt"));
        var state = await git.ReadAsync(fixture.Root, default); state = await git.StageAsync(state, state.Changes, default);
        Assert.Contains(state.Changes, c => c.Area == GitArea.Staged && c.OriginalPath == "sample.txt");
        state = await git.UnstageAsync(state, state.Changes, default);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "sample.txt"))); Assert.True(File.Exists(Path.Combine(fixture.Root, "renamed.txt")));
    }
    [Fact]
    public async Task Merge_in_progress_is_read_only_even_if_the_index_is_resolved()
    {
        await using var fixture = new GitTestWorkspace(); await fixture.InitializeAsync(); var git = new WorkspaceGit();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "sample.txt"), "changed"); var state = await git.ReadAsync(fixture.Root, default);
        state = await git.StageAsync(state, state.Changes, default); var marker = Path.Combine(state.GitDirectory, "MERGE_HEAD");
        await File.WriteAllTextAsync(marker, state.Head + "\n"); state = await git.ReadAsync(fixture.Root, default);
        Assert.True(state.OperationInProgress); await Assert.ThrowsAsync<IOException>(() => git.CommitAsync(state, "must not commit", default)); File.Delete(marker);
    }
}
