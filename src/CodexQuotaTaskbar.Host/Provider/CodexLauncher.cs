using System.Diagnostics;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace CodexQuotaTaskbar.Host.Provider;

internal static class CodexLauncher
{
    private const string PackageRepositoryKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

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

    internal static string FindExecutable(IEnumerable<string> pathDirectories) =>
        FindExecutable(pathDirectories, FindDesktopPackageRoots(), GetDesktopCodexCacheRoot());

    internal static string FindExecutable(
        IEnumerable<string> pathDirectories,
        IEnumerable<string> desktopPackageRoots,
        string desktopCodexCacheRoot)
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

        foreach (var root in desktopPackageRoots)
        {
            foreach (var relativePath in new[]
                     {
                         Path.Combine("app", "resources", "codex.exe"),
                         Path.Combine("resources", "codex.exe"),
                     })
            {
                try
                {
                    var candidate = Path.Combine(root, relativePath);
                    if (File.Exists(candidate))
                    {
                        var cached = CopyDesktopCodexToCache(candidate, root, desktopCodexCacheRoot);
                        if (cached is not null)
                        {
                            return cached;
                        }
                    }
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or SecurityException or UnauthorizedAccessException)
                {
                }
            }
        }

        return "codex.exe";
    }

    private static string GetDesktopCodexCacheRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexQuotaTaskbar",
        "Runtime",
        "Codex");

    private static string? CopyDesktopCodexToCache(string source, string packageRoot, string cacheRoot)
    {
        var packageName = Path.GetFileName(Path.TrimEndingDirectorySeparator(packageRoot));
        if (string.IsNullOrWhiteSpace(packageName) || packageName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        var fullCacheRoot = Path.GetFullPath(cacheRoot);
        var packageCache = Path.GetFullPath(Path.Combine(fullCacheRoot, packageName));
        if (!packageCache.StartsWith(fullCacheRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        Directory.CreateDirectory(packageCache);
        var destination = Path.Combine(packageCache, "codex.exe");
        var sourceLength = new FileInfo(source).Length;
        if (File.Exists(destination) && new FileInfo(destination).Length == sourceLength)
        {
            return destination;
        }

        var temporary = Path.Combine(packageCache, $"codex-{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(source, temporary, overwrite: false);
            if (new FileInfo(temporary).Length != sourceLength)
            {
                return null;
            }

            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static IReadOnlyList<string> FindDesktopPackageRoots()
    {
        var roots = new List<string>();
        try
        {
            using var packages = Registry.CurrentUser.OpenSubKey(PackageRepositoryKey);
            if (packages is null)
            {
                return roots;
            }

            foreach (var packageName in packages.GetSubKeyNames())
            {
                if (!packageName.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var package = packages.OpenSubKey(packageName);
                if (package?.GetValue("PackageRootFolder") is string root && !string.IsNullOrWhiteSpace(root))
                {
                    roots.Add(root);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or SecurityException or UnauthorizedAccessException)
        {
        }

        return roots;
    }
}
