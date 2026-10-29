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
    private readonly string? updateTarget;
    private readonly Func<ProcessStartInfo, Process?> startInstaller;
    private readonly TimeSpan verificationTimeout;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, UpdatePackageIdentity> stagedIdentities = new(StringComparer.OrdinalIgnoreCase);

    internal GitHubUpdateService(HttpClient? client = null, string? updateRoot = null, string? targetExecutable = null,
        Func<ProcessStartInfo, Process?>? startInstaller = null, TimeSpan? verificationTimeout = null)
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
        updateTarget = targetExecutable ?? Environment.ProcessPath;
        this.startInstaller = startInstaller ?? Process.Start;
        this.verificationTimeout = verificationTimeout ?? TimeSpan.FromSeconds(90);
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

        var release = GitHubReleaseParser.Parse(json)
            ?? throw new InvalidDataException("发行信息缺少有效的 Windows x64 更新包或 SHA-256 校验信息。");
        EnsureTransactionAllows(release.Version);
        return release is { Version: var version } && version > ProductVersion.Value ? release : null;
    }

    private void EnsureTransactionAllows(Version offered)
    {
        if (updateTarget is null || !File.Exists(UpdateTransaction.ReceiptPath(updateTarget))) return;
        var receipt = UpdateStartup.ReadReceipt(updateTarget);
        if (receipt.Target != Path.GetFullPath(updateTarget) || !Guid.TryParseExact(receipt.Nonce, "N", out _)
            || !Version.TryParse(receipt.NewVersion, out var failed) || !Enum.IsDefined(receipt.Phase)) throw new InvalidDataException();
        if (receipt.Phase == UpdateTransactionPhase.Committed || receipt.Phase == UpdateTransactionPhase.RolledBack && offered > failed) return;
        throw new UpdateApplyException(UpdateApplyDiagnostic.Capture(UpdateApplyStage.Validate, new InvalidOperationException()), null, false, 0)
        { RecoveryOutcome = receipt.Phase == UpdateTransactionPhase.RolledBack
            ? $"当前 v{ProductVersion.Text}，失败版本 v{failed.ToString(3)} 已阻止重复安装"
            : "已有更新事务待恢复" };
    }

    internal async Task VerifyInstallerSourceAsync(string source, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(verificationTimeout);
        try { await VerifyInstallerSourceCoreAsync(source, budget.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException(); }
    }

    private async Task VerifyInstallerSourceCoreAsync(string source, CancellationToken token)
    {
        var identity = UpdatePackageIdentity.Read(source);
        var uri = new Uri($"https://api.github.com/repos/dddeeprog/CodexQuotaTaskbar/releases/tags/v{identity.Version.ToString(3)}");
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(token);
        if (json.Length > 1024 * 1024) throw new InvalidDataException();
        var release = GitHubReleaseParser.Parse(json) ?? throw new InvalidDataException();
        if (release.Version != identity.Version) throw new InvalidDataException();
        // A separate owned directory avoids overwriting the currently running helper.
        var verificationRoot = Path.Combine(updateRoot, "attestation-" + Guid.NewGuid().ToString("N"));
        using var verifier = new GitHubUpdateService(client, verificationRoot);
        try
        {
            var staged = await verifier.DownloadAndStageAsync(release, token);
            if (UpdatePackageIdentity.Read(staged) != identity) throw new InvalidDataException();
        }
        finally
        {
            // Only this invocation owns this directory; never delete a release/helper directory.
            try { if (Directory.Exists(verificationRoot)) Directory.Delete(verificationRoot, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    internal async Task<string> DownloadAndStageAsync(GitHubRelease release, CancellationToken cancellationToken, IProgress<UpdateStatus>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(release);
        UpdatePackageIdentity.ValidateRelease(release);
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
                var lastPercent = -1;
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
                    if (response.Content.Headers.ContentLength is > 0 and var length)
                    {
                        var percent = (int)Math.Min(100, total * 100 / length);
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            progress?.Report(new(UpdateStage.Downloading, "正在下载更新", $"版本 {release.Tag}", percent));
                        }
                    }
                }
                await output.FlushAsync(cancellationToken);
            }

            var actualHash = Convert.ToHexString(hash.GetHashAndReset());
            progress?.Report(new(UpdateStage.Verifying, "正在校验更新", "确认文件完整性后再启动安装。"));
            if (!actualHash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("更新包 SHA-256 校验失败。");
            }

            await ExtractSingleExecutableAsync(archivePath, temporaryExecutable, cancellationToken);
            var identity = UpdatePackageIdentity.Read(temporaryExecutable, release.Version);
            File.Move(temporaryExecutable, stagedExecutable, overwrite: true);
            stagedIdentities[Path.GetFullPath(stagedExecutable)] = identity;
            return stagedExecutable;
        }
        finally
        {
            DeleteFileBestEffort(archivePath);
            DeleteFileBestEffort(temporaryExecutable);
        }
    }

    internal async Task<bool> LaunchInstallerAsync(string stagedExecutable, CancellationToken token)
    {
        // Keep the verified candidate immutable across the final check and start.
        using var candidateGuard = new FileStream(stagedExecutable, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!stagedIdentities.TryGetValue(Path.GetFullPath(stagedExecutable), out var verified)
            || UpdatePackageIdentity.Read(stagedExecutable) != verified) throw new InvalidDataException();
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
        using var self = Process.GetCurrentProcess();
        var nonce = Guid.NewGuid().ToString("N");
        var identity = verified;
        var options = new UpdateApplyOptions(Environment.ProcessId, Path.GetFullPath(target))
        { Nonce = nonce, ParentStartTicks = self.StartTime.ToUniversalTime().Ticks };
        startInfo.ArgumentList.Add(options.ParentStartTicks.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(nonce);
        using var process = startInstaller(startInfo);
        if (process is null)
        {
            return false;
        }
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromMinutes(2))
        {
            token.ThrowIfCancellationRequested();
            if (process.HasExited) throw new InvalidOperationException("更新程序准备失败。");
            if (UpdateStartup.IsPrepared(options, identity)) return true;
            await Task.Delay(100, token);
        }
        throw new TimeoutException();
    }

    internal async Task CleanupAsync(CancellationToken cancellationToken)
    {
        // Release directories can contain a running helper or recovery source.
        // A normal startup has no ownership proof and must not delete them.
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
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
