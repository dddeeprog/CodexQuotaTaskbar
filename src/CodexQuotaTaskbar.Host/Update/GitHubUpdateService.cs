using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace CodexQuotaTaskbar.Host.Update;

internal sealed class GitHubUpdateService : IDisposable
{
    private const long MaximumDownloadBytes = 256L * 1024 * 1024;
    private const long MaximumExecutableBytes = 160L * 1024 * 1024;
    private static readonly Uri LatestReleaseUri = new("https://api.github.com/repos/dddeeprog/CodexQuotaTaskbar/releases/latest");
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly string updateRoot;

    internal GitHubUpdateService(HttpClient? client = null, string? updateRoot = null)
    {
        ownsClient = client is null;
        this.client = client ?? new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
        });
        this.client.DefaultRequestHeaders.UserAgent.ParseAdd($"CodexQuotaTaskbar/{ProductVersion.Text}");
        this.updateRoot = updateRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexQuotaTaskbar",
            "Updates");
    }

    internal async Task<GitHubRelease?> CheckAsync(CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(LatestReleaseUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (json.Length > 1024 * 1024)
        {
            throw new InvalidDataException("GitHub Release 响应异常。 ");
        }

        var release = GitHubReleaseParser.Parse(json);
        return release is { Version: var version } && version > ProductVersion.Value ? release : null;
    }

    internal async Task<string> DownloadAndStageAsync(GitHubRelease release, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        var releaseDirectory = GetReleaseDirectory(release.Tag);
        Directory.CreateDirectory(releaseDirectory);
        var archivePath = Path.Combine(releaseDirectory, $".{Guid.NewGuid():N}.zip");
        var temporaryExecutable = Path.Combine(releaseDirectory, $".{Guid.NewGuid():N}.exe");
        var stagedExecutable = Path.Combine(releaseDirectory, GitHubReleaseParser.ExecutableName);

        try
        {
            using var response = await client.GetAsync(release.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumDownloadBytes)
            {
                throw new InvalidDataException("更新包超过允许大小。");
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > MaximumDownloadBytes)
                    {
                        throw new InvalidDataException("更新包超过允许大小。");
                    }
                    hash.AppendData(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
            }

            var actualHash = Convert.ToHexString(hash.GetHashAndReset());
            if (!actualHash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("更新包 SHA-256 校验失败。");
            }

            await ExtractSingleExecutableAsync(archivePath, temporaryExecutable, cancellationToken);
            File.Move(temporaryExecutable, stagedExecutable, overwrite: true);
            return stagedExecutable;
        }
        finally
        {
            DeleteFileBestEffort(archivePath);
            DeleteFileBestEffort(temporaryExecutable);
        }
    }

    internal bool LaunchInstaller(string stagedExecutable)
    {
        var target = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(target) || !File.Exists(stagedExecutable))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = stagedExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--apply-update");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(target);
        var process = Process.Start(startInfo);
        if (process is null)
        {
            return false;
        }
        process.Dispose();
        return true;
    }

    internal async Task CleanupAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        if (!Directory.Exists(updateRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(updateRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private string GetReleaseDirectory(string tag)
    {
        if (tag.Length is < 2 or > 32 || tag.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
        {
            throw new InvalidDataException("更新版本标签无效。");
        }

        var fullRoot = Path.GetFullPath(updateRoot);
        var directory = Path.GetFullPath(Path.Combine(fullRoot, tag));
        if (!directory.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("更新目录无效。");
        }
        return directory;
    }

    private static async Task ExtractSingleExecutableAsync(string archivePath, string outputPath, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count != 1 ||
            archive.Entries[0].FullName != GitHubReleaseParser.ExecutableName ||
            archive.Entries[0].Length is < 1024 or > MaximumExecutableBytes)
        {
            throw new InvalidDataException("更新包结构无效。");
        }

        await using (var input = archive.Entries[0].Open())
        await using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
        {
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }

        await using var check = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (check.ReadByte() != 'M' || check.ReadByte() != 'Z')
        {
            throw new InvalidDataException("更新程序不是有效的 Windows 可执行文件。");
        }
    }

    private static void DeleteFileBestEffort(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (ownsClient)
        {
            client.Dispose();
        }
    }
}
