using System.Diagnostics;
using System.IO;

namespace CodexQuotaTaskbar.Host.Provider;

internal static class CodexLauncher
{
    internal static void OpenApp()
    {
        var info = new ProcessStartInfo
        {
            FileName = FindExecutable(),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("app");
        Process.Start(info)?.Dispose();
    }

    internal static string FindExecutable() => FindExecutable(
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

    internal static string FindExecutable(IEnumerable<string> pathDirectories)
    {
        foreach (var directory in pathDirectories)
        {
            try
            {
                var normalized = directory.Trim('"');
                var npmNative = Path.Combine(normalized, "node_modules", "@openai", "codex", "node_modules", "@openai",
                    "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe");
                if (File.Exists(Path.Combine(normalized, "codex.cmd")) && File.Exists(npmNative))
                {
                    return npmNative;
                }

                var candidate = Path.Combine(normalized, "codex.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
            }
        }
        return "codex.exe";
    }
}
