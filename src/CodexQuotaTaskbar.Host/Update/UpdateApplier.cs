using System.Diagnostics;
using System.IO;

namespace CodexQuotaTaskbar.Host.Update;

internal static class UpdateApplier
{
    internal static async Task ApplyAsync(UpdateApplyOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            using var parent = Process.GetProcessById(options.ParentProcessId);
            await parent.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(45), cancellationToken);
        }
        catch (ArgumentException)
        {
        }

        var source = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定更新程序路径。");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                ReplaceExecutable(source, options.TargetExecutable);
                break;
            }
            catch (IOException) when (attempt < 20)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }
        Process.Start(new ProcessStartInfo
        {
            FileName = options.TargetExecutable,
            WorkingDirectory = Path.GetDirectoryName(options.TargetExecutable)!,
            UseShellExecute = true,
        })?.Dispose();
    }

    internal static void ReplaceExecutable(string source, string target)
    {
        var sourcePath = Path.GetFullPath(source);
        var targetPath = Path.GetFullPath(target);
        if (!File.Exists(sourcePath) || !File.Exists(targetPath) ||
            !Path.GetExtension(targetPath).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            sourcePath.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("更新源或目标无效。");
        }

        var directory = Path.GetDirectoryName(targetPath) ?? throw new InvalidOperationException("更新目标目录无效。");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(sourcePath, temporary, overwrite: false);
            File.Move(temporary, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
