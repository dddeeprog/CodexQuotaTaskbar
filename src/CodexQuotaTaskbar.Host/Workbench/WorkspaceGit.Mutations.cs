using System.Collections.Concurrent;
using System.IO;

namespace CodexQuotaTaskbar.Host.Workbench;

internal sealed partial class WorkspaceGit
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteGates = new(StringComparer.OrdinalIgnoreCase);

    internal Task<GitWorkspace> StageAsync(GitWorkspace reviewed, IReadOnlyList<GitChange> changes, CancellationToken token)
        => MutateAsync(reviewed, changes, false, null, token);
    internal Task<GitWorkspace> UnstageAsync(GitWorkspace reviewed, IReadOnlyList<GitChange> changes, CancellationToken token)
        => MutateAsync(reviewed, changes, true, null, token);
    internal Task<GitWorkspace> CommitAsync(GitWorkspace reviewed, string message, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("请填写提交说明。", nameof(message));
        return MutateAsync(reviewed, [], false, message, token);
    }

    private async Task<GitWorkspace> MutateAsync(GitWorkspace reviewed, IReadOnlyList<GitChange> changes, bool unstage, string? message, CancellationToken token)
    {
        var gate = WriteGates.GetOrAdd(reviewed.Root, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var current = await ReadAsync(reviewed.Root, token).ConfigureAwait(false);
            if (current.Version != reviewed.Version || current.GitDirectory != reviewed.GitDirectory)
                throw new IOException("仓库或改动已被其他操作更新，请刷新并重新核对后操作。");
            if (current.OperationInProgress || current.Changes.Any(c => c.Area == GitArea.Conflict))
                throw new IOException("仓库正在合并、变基或存在冲突，请先在原工具中完成，工作台仅提供浏览。");
            var arguments = new List<string>();
            if (message is not null)
            {
                if (!current.Changes.Any(c => c.Area == GitArea.Staged)) throw new IOException("没有已暂存的改动，未创建提交。");
                arguments.AddRange(["commit", "--file=-"]);
            }
            else
            {
                if (changes.Count == 0 || changes.Any(c => !reviewed.Changes.Contains(c)
                    || (unstage ? c.Area != GitArea.Staged : c.Area is not (GitArea.Worktree or GitArea.Untracked))))
                    throw new IOException("请选择对应分区中的文件，当前选择不适用于此操作。");
                var paths = changes.SelectMany(c => c.OriginalPath is { } old ? new[] { c.Path, old } : new[] { c.Path }).Distinct(StringComparer.Ordinal).ToArray();
                foreach (var path in paths)
                {
                    var full = WorkspacePaths.Validate(current.Root, Path.Combine(current.Root, path), false);
                    if (!unstage && Directory.Exists(full)) throw new IOException("目录或子模块需在其实际仓库中操作，未自动暂存整个目录。");
                }
                if (!unstage) arguments.AddRange(["add", "--"]);
                else if (current.Head is null) arguments.AddRange(["rm", "--cached", "--force", "--ignore-unmatch", "--"]);
                else arguments.AddRange(["restore", "--staged", "--"]);
                arguments.AddRange(paths);
            }
            token.ThrowIfCancellationRequested();
            var result = await RunAsync(current.Root, arguments, CancellationToken.None, mutation: true, input: message).ConfigureAwait(false);
            if (result.ExitCode != 0) throw new IOException(result.Error.Trim() is { Length: > 0 } error ? error : "Git 未能完成操作，请检查仓库状态。");
            try { return await ReadAsync(current.Root, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or OperationCanceledException)
            { throw new IOException("Git 已返回成功，但状态刷新未完成。请先刷新核对，不要重复提交。", error); }
        }
        finally { gate.Release(); }
    }
}
