using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexQuotaTaskbar.Host.Workbench;

internal enum GitArea { Staged, Worktree, Untracked, Conflict }
internal sealed record GitChange(GitArea Area, string Path, string? OriginalPath, string Status)
{
    public override string ToString() => $"{Area switch { GitArea.Staged => "已暂存", GitArea.Worktree => "未暂存", GitArea.Untracked => "新文件", _ => "冲突" }}  {Status}  {Path}";
}
internal sealed record GitWorkspace(string Root, string GitDirectory, string? Head, string Branch,
    string Version, bool OperationInProgress, IReadOnlyList<GitChange> Changes);
internal sealed record GitDiffRow(string? OldLine, string? NewLine, string OldText, string NewText, string Kind)
{
    public string OldBackground => !System.Windows.SystemParameters.HighContrast && Kind is "delete" or "replace" ? "#452D31" : "Transparent";
    public string NewBackground => !System.Windows.SystemParameters.HighContrast && Kind is "add" or "replace" ? "#263E35" : "Transparent";
}
internal sealed record GitDiff(IReadOnlyList<GitDiffRow> Rows, string? Notice);
internal sealed record GitCommandResult(int ExitCode, string Output, string Error);

internal sealed partial class WorkspaceGit
{
    private const int MaximumOutput = 8 * 1024 * 1024;
    private readonly string executable;
    internal WorkspaceGit(string executable = "git.exe") => this.executable = executable;

    internal Task<GitWorkspace> ReadAsync(string directory, CancellationToken token)
        => Task.Run(() => ReadCoreAsync(directory, token), token);
    private async Task<GitWorkspace> ReadCoreAsync(string directory, CancellationToken token)
    {
        WorkspacePaths.Validate(directory, directory);
        var identity = await CheckedAsync(directory, ["rev-parse", "--path-format=absolute", "--show-toplevel", "--absolute-git-dir"], token);
        var parts = identity.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2) throw new IOException("无法确认仓库的实际目录。");
        var root = Path.GetFullPath(parts[0]); var gitDirectory = Path.GetFullPath(parts[1]);
        WorkspacePaths.Validate(root, root);
        var headResult = await RunAsync(root, ["rev-parse", "--verify", "HEAD"], token);
        var head = headResult.ExitCode == 0 ? headResult.Output.Trim() : null;
        var branchResult = await RunAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], token);
        var branch = branchResult.ExitCode == 0 ? branchResult.Output.Trim() : "detached HEAD";
        var status = await CheckedAsync(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], token);
        var index = await CheckedAsync(root, ["ls-files", "--stage", "-z"], token);
        var changes = ParseStatus(status);
        var busy = new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-apply", "rebase-merge" }
            .Any(name => File.Exists(Path.Combine(gitDirectory, name)) || Directory.Exists(Path.Combine(gitDirectory, name)));
        var version = new StringBuilder(root).Append('\0').Append(gitDirectory).Append('\0').Append(head).Append('\0').Append(branch).Append('\0').Append(index).Append(status).Append(busy);
        foreach (var path in changes.Select(c => c.Path).Distinct(StringComparer.Ordinal))
        {
            var info = new FileInfo(Path.Combine(root, path));
            try { version.Append('\0').Append(path).Append(':').Append(info.Exists ? info.Length : -1).Append(':').Append(info.LastWriteTimeUtc.Ticks); }
            catch (IOException) { version.Append(":unavailable"); }
        }
        return new(root, gitDirectory, head, branch, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(version.ToString()))), busy, changes);
    }

    internal static IReadOnlyList<GitChange> ParseStatus(string status)
    {
        var result = new List<GitChange>(); var records = status.Split('\0');
        for (var i = 0; i < records.Length; i++)
        {
            var record = records[i]; if (record.Length < 4) continue;
            var x = record[0]; var y = record[1]; var path = record[3..]; string? original = null;
            if (x is 'R' or 'C' || y is 'R' or 'C') { if (++i >= records.Length) throw new IOException("Git 重命名状态不完整。"); original = records[i]; }
            var pair = record[..2];
            if (pair is "DD" or "AU" or "UD" or "UA" or "DU" or "AA" or "UU") result.Add(new(GitArea.Conflict, path, original, pair));
            else if (pair == "??") result.Add(new(GitArea.Untracked, path, null, "?"));
            else
            {
                if (x is not (' ' or '?')) result.Add(new(GitArea.Staged, path, original, x.ToString()));
                if (y is not (' ' or '?')) result.Add(new(GitArea.Worktree, path, original, y.ToString()));
            }
        }
        return result;
    }

    internal async Task<IReadOnlyList<string>> WorktreesAsync(GitWorkspace repository, CancellationToken token)
    {
        var output = await CheckedAsync(repository.Root, ["worktree", "list", "--porcelain", "-z"], token);
        return output.Split('\0').Where(s => s.StartsWith("worktree ", StringComparison.Ordinal)).Select(s => Path.GetFullPath(s[9..])).ToArray();
    }

    internal Task<GitDiff> DiffAsync(GitWorkspace repository, GitChange change, CancellationToken token)
        => Task.Run(() => DiffCoreAsync(repository, change, token), token);
    private async Task<GitDiff> DiffCoreAsync(GitWorkspace repository, GitChange change, CancellationToken token)
    {
        if (!repository.Changes.Contains(change)) throw new IOException("所选改动已过期，请刷新。");
        if (change.Area == GitArea.Untracked) return new([], "新文件尚无 Git 历史，可在文件视图中预览后暂存。");
        var args = new List<string> { "diff", "--no-color", "--no-ext-diff", "--no-textconv", "--find-renames", "--unified=3" };
        if (change.Area == GitArea.Staged) args.Add("--cached");
        args.Add("--"); args.Add(change.Path); if (change.OriginalPath is { } old) args.Add(old);
        var patch = await CheckedAsync(repository.Root, args, token);
        if (Encoding.UTF8.GetByteCount(patch) > WorkspaceFiles.MaximumTextBytes) return new([], "差异超过 2 MiB，请使用外部工具查看。");
        return ParseDiff(patch);
    }

    internal static GitDiff ParseDiff(string patch)
    {
        var rows = new List<GitDiffRow>(); var removed = new List<(int Line, string Text)>(); var added = new List<(int Line, string Text)>();
        var oldLine = 0; var newLine = 0; var inside = false;
        void Flush()
        {
            for (var i = 0; i < Math.Max(removed.Count, added.Count); i++)
            {
                var left = i < removed.Count ? removed[i] : ((int Line, string Text)?)null;
                var right = i < added.Count ? added[i] : ((int Line, string Text)?)null;
                rows.Add(new(left?.Line.ToString(), right?.Line.ToString(), left?.Text ?? "", right?.Text ?? "", left is null ? "add" : right is null ? "delete" : "replace"));
            }
            removed.Clear(); added.Clear();
        }
        foreach (var line in patch.Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal)) { Flush(); inside = false; continue; }
            var match = Regex.Match(line, @"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@");
            if (match.Success)
            {
                Flush(); oldLine = int.Parse(match.Groups[1].Value); newLine = int.Parse(match.Groups[2].Value); inside = true;
                rows.Add(new(null, null, line, line, "header")); continue;
            }
            if (!inside || line.Length == 0) continue;
            switch (line[0])
            {
                case '-': removed.Add((oldLine++, line[1..].TrimEnd('\r'))); break;
                case '+': added.Add((newLine++, line[1..].TrimEnd('\r'))); break;
                case ' ': Flush(); rows.Add(new((oldLine++).ToString(), (newLine++).ToString(), line[1..].TrimEnd('\r'), line[1..].TrimEnd('\r'), "context")); break;
            }
        }
        Flush();
        return new(rows, rows.Count > 0 ? null : patch.Contains("Binary files", StringComparison.Ordinal) ? "二进制文件已改变，请使用外部工具查看。" : string.IsNullOrWhiteSpace(patch) ? "当前没有文本差异。" : patch.Trim());
    }

    private async Task<string> CheckedAsync(string root, IReadOnlyList<string> args, CancellationToken token)
    {
        var result = await RunAsync(root, args, token);
        if (result.ExitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(result.Error) ? "Git 操作未完成。" : result.Error.Trim());
        return result.Output;
    }
    internal async Task<GitCommandResult> RunAsync(string root, IReadOnlyList<string> arguments, CancellationToken token, bool mutation = false, string? input = null)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in new[] { "--no-optional-locks", "--literal-pathspecs", "-C", root }) start.ArgumentList.Add(arg);
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        if (input is not null) start.StandardInputEncoding = new UTF8Encoding(false);
        using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new IOException("无法启动 Git。"); }
        catch (System.ComponentModel.Win32Exception error) { throw new IOException("无法启动本机 Git，请检查是否已安装。", error); }
        var output = DrainAsync(process.StandardOutput); var errors = DrainAsync(process.StandardError);
        if (input is not null)
        {
            try { await process.StandardInput.WriteAsync(input); process.StandardInput.Close(); }
            catch (IOException) { /* Git may reject before reading stdin. Await its actual exit and stderr below. */ }
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (!mutation) timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try { await process.WaitForExitAsync(mutation ? CancellationToken.None : timeout.Token); }
        catch (OperationCanceledException)
        {
            // Only cancellable read processes are terminated. Git writes always run to completion.
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(); await Task.WhenAll(output, errors); throw;
        }
        var stdout = await output; var stderr = await errors;
        if (stdout.Truncated && !mutation) throw new IOException("Git 返回结果超过读取上限，请缩小仓库范围或使用外部工具。");
        return new(process.ExitCode, stdout.Text, stderr.Text + (stderr.Truncated ? "\n（错误信息已截断）" : ""));
    }
    private static async Task<(string Text, bool Truncated)> DrainAsync(StreamReader reader)
    {
        var text = new StringBuilder(); var buffer = new char[8192]; int count; var truncated = false;
        while ((count = await reader.ReadAsync(buffer)) != 0)
        { var keep = Math.Min(count, MaximumOutput - text.Length); if (keep > 0) text.Append(buffer, 0, keep); if (keep < count) truncated = true; }
        return (text.ToString(), truncated);
    }
}
