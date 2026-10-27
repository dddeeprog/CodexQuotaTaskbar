using System.Diagnostics;
using System.IO;
using System.Text;

namespace CodexQuotaTaskbar.Host.Tests.Workbench;

internal sealed class GitTestWorkspace : IAsyncDisposable
{
    internal string Parent { get; } = Path.Combine(Path.GetTempPath(), "DianDaoWorkbench-git-tests", Guid.NewGuid().ToString("N"));
    internal string Root => Path.Combine(Parent, "main");
    private readonly List<string> worktrees = [];
    internal async Task InitializeAsync(bool initialCommit = true)
    {
        Directory.CreateDirectory(Root); await RunAsync(Root, "init", "-b", "main");
        await RunAsync(Root, "config", "user.name", "Workbench synthetic tests");
        await RunAsync(Root, "config", "user.email", "workbench-tests@example.invalid");
        await RunAsync(Root, "config", "commit.gpgsign", "false");
        if (initialCommit)
        {
            await File.WriteAllTextAsync(Path.Combine(Root, "sample.txt"), "original\n");
            await RunAsync(Root, "add", "--", "sample.txt"); await RunAsync(Root, "commit", "-m", "Synthetic initial commit");
        }
    }
    internal async Task<string> AddWorktreeAsync(string name)
    {
        var target = Path.Combine(Parent, name);
        await RunAsync(Root, "worktree", "add", "--detach", target); worktrees.Add(target); return target;
    }
    internal static async Task<string> RunAsync(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.ArgumentList.Add("-C"); start.ArgumentList.Add(root); foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); var message = await error;
        Assert.True(process.ExitCode == 0, message); return await output;
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var worktree in worktrees) await RunAsync(Root, "worktree", "remove", worktree);
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DianDaoWorkbench-git-tests"));
        var resolved = Path.GetFullPath(Parent);
        if (!resolved.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid synthetic Git cleanup root.");
        foreach (var file in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(resolved, true);
    }
}
