using System.IO;

namespace CodexQuotaTaskbar.Host.Workbench;

internal interface IWorkspaceRecycleBin { void Recycle(string path); }

internal sealed partial class WorkspaceFiles
{
    private readonly IWorkspaceRecycleBin recycleBin;
    internal WorkspaceFiles(IWorkspaceRecycleBin? recycleBin = null) => this.recycleBin = recycleBin ?? new NativeRecycleBin();

    internal static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.EndsWith('.')
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".."
            || name.Equals(".git", StringComparison.OrdinalIgnoreCase)) throw new IOException("名称无效，请只输入文件或文件夹名称。");
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9'))
            throw new IOException("此名称由 Windows 保留，请换一个名称。");
    }
    internal Task<string> CreateAsync(string root, string parent, string name, bool directory, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested(); ValidateName(name);
        parent = WorkspacePaths.Validate(root, parent);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("目标文件夹不存在。");
        var target = WorkspacePaths.Validate(root, Path.Combine(parent, name), false);
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("同名项目已存在，未覆盖。");
        if (directory) Directory.CreateDirectory(target);
        else { using var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write); }
        return target;
    }, token);

    internal Task<string> RenameAsync(string root, string path, string name, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested(); ValidateName(name);
        path = WorkspacePaths.Validate(root, path, false);
        var destination = WorkspacePaths.Validate(root, Path.Combine(Path.GetDirectoryName(path)!, name), false);
        if (path == destination) return path;
        if (!string.Equals(path, destination, StringComparison.OrdinalIgnoreCase) && (File.Exists(destination) || Directory.Exists(destination)))
            throw new IOException("同名项目已存在，未覆盖。");
        if (Directory.Exists(path)) Directory.Move(path, destination); else File.Move(path, destination, false);
        return destination;
    }, token);

    internal async Task RecycleAsync(string root, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); path = WorkspacePaths.Validate(root, path, false);
        // Verify the complete directory before a recursive shell operation. Never enter a link or recycle a nested repository.
        await Task.Run(() =>
        {
            var pending = new Stack<string>();
            if (Directory.Exists(path)) pending.Push(path);
            while (pending.TryPop(out var directory))
                foreach (var item in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested(); WorkspacePaths.Validate(root, item, false);
                    if (Directory.Exists(item)) pending.Push(item);
                }
        }, token);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { token.ThrowIfCancellationRequested(); recycleBin.Recycle(WorkspacePaths.Validate(root, path, false)); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true, Name = "DianDaoRecycle" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await completion.Task;
    }
}
