using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using CodexQuotaTaskbar.Host.Update;

namespace CodexQuotaTaskbar.Host.Tests.Update;

public sealed class OfficialUpdateArchiveFactAttribute : FactAttribute
{
    public OfficialUpdateArchiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CQT_OFFICIAL_UPDATE_ARCHIVE")))
            Skip = "Set CQT_OFFICIAL_UPDATE_ARCHIVE to the verified v0.1.14 portable ZIP; offline staging only.";
    }
}

public sealed class GitHubUpdateServiceTests : IDisposable
{
    private readonly string root = Path.Combine(TestSupport.RepositoryPaths.Root, "artifacts", "reliability", "update", "download-" + Guid.NewGuid().ToString("N"));

    [OfficialUpdateArchiveFact]
    public async Task Known_official_archive_stages_the_exact_executable_without_launch_or_network()
    {
        const string zipHash = "C11C42DC2E367FBEB7051345CA9584523016664A9DCA8B6653151A4273F85E5F";
        const string exeHash = "24FD6B5B7A15068BD709E6665FAA8E156988B792FE6D8814FF45C817B83AB7F8";
        var path = Path.GetFullPath(Environment.GetEnvironmentVariable("CQT_OFFICIAL_UPDATE_ARCHIVE")!);
        using (var file = File.OpenRead(path)) Assert.Equal(zipHash, Convert.ToHexString(await SHA256.HashDataAsync(file)));
        using var http = new HttpClient(new LocalArchiveHandler(path));
        using var service = new GitHubUpdateService(http, root);
        var release = Assert.IsType<GitHubRelease>(GitHubReleaseParser.Parse(GitHubReleaseParserTests.CreateJson("v0.1.14", digest: zipHash)));
        var staged = await service.DownloadAndStageAsync(release, CancellationToken.None);
        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, Path.GetFullPath(staged), StringComparison.OrdinalIgnoreCase);
        using (var file = File.OpenRead(staged)) Assert.Equal(exeHash, Convert.ToHexString(await SHA256.HashDataAsync(file)));
        Assert.Single(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Downloads_hashes_and_extracts_release_matching_real_executable()
    {
        var archive = CreateRealArchive();
        var digest = Convert.ToHexString(SHA256.HashData(archive));
        var nextVersion = ProductVersion.Value;
        using var http = new HttpClient(new FakeHandler(GitHubReleaseParserTests.CreateJson($"v{nextVersion}", digest: digest), archive));
        using var service = new GitHubUpdateService(http, root);

        var release = GitHubReleaseParser.Parse(GitHubReleaseParserTests.CreateJson($"v{nextVersion}", digest: digest));
        var staged = await service.DownloadAndStageAsync(Assert.IsType<GitHubRelease>(release), CancellationToken.None);

        Assert.Equal("CodexQuotaTaskbar.exe", Path.GetFileName(staged));
        Assert.True(File.Exists(staged));
        Assert.Equal(ProductVersion.Value, UpdatePackageIdentity.Read(staged).Version);
    }
    [Fact]
    public async Task Persistent_failed_version_is_blocked_before_new_caller_can_exit()
    {
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "installed.exe");
        var next = new Version(ProductVersion.Value.Major, ProductVersion.Value.Minor, ProductVersion.Value.Build + 1);
        File.WriteAllBytes(UpdateTransaction.ReceiptPath(target), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new UpdateReceipt(
            Guid.NewGuid().ToString("N"), target, new string('A', 64), new string('B', 64), ProductVersion.Text, next.ToString(3), UpdateTransactionPhase.RolledBack)));
        using var http = new HttpClient(new FakeHandler(GitHubReleaseParserTests.CreateJson($"v{next}"), []));
        using var service = new GitHubUpdateService(http, Path.Combine(root, "cache"), target);
        var error = await Assert.ThrowsAsync<UpdateApplyException>(() => service.CheckAsync(default));
        Assert.False(UpdateStatus.Failure(error).CanRetry); Assert.False(Directory.Exists(Path.Combine(root, "cache")));
    }
    [Fact]
    public async Task Modified_but_still_valid_pe_is_never_executed_after_download()
    {
        var archive = CreateRealArchive(); var digest = Convert.ToHexString(SHA256.HashData(archive)); var launches = 0;
        using var http = new HttpClient(new FakeHandler("", archive));
        using var service = new GitHubUpdateService(http, root, startInstaller: _ => { launches++; return null; });
        var release = GitHubReleaseParser.Parse(GitHubReleaseParserTests.CreateJson($"v{ProductVersion.Text}", digest: digest))!;
        var staged = await service.DownloadAndStageAsync(release, default);
        File.AppendAllText(staged, "valid PE with altered overlay");
        Assert.Equal(ProductVersion.Value, UpdatePackageIdentity.Read(staged).Version);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.LaunchInstallerAsync(staged, default));
        Assert.Equal(0, launches);
    }
    [Fact]
    public async Task Self_attestation_has_total_deadline_even_when_response_body_stalls()
    {
        using var http = new HttpClient(new StalledHandler()) { Timeout = TimeSpan.FromMilliseconds(300) };
        using var service = new GitHubUpdateService(http, root, verificationTimeout: TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAsync<TimeoutException>(() => service.VerifyInstallerSourceAsync(HostExe, default));
    }
    [Fact]
    public async Task Self_attestation_preserves_explicit_caller_cancellation()
    {
        using var http = new HttpClient(new StalledHandler());
        using var service = new GitHubUpdateService(http, root, verificationTimeout: TimeSpan.FromMilliseconds(30));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.VerifyInstallerSourceAsync(HostExe, cancelled.Token));
    }
    private sealed class StalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StalledBody() });
    }
    private sealed class StalledBody : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("Body read must receive the cancellation token.");
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) =>
            Task.Delay(Timeout.Infinite, token);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("version")]
    [InlineData("uri")]
    public async Task Legacy_helper_independently_rejects_an_unbound_official_attestation(string mismatch)
    {
        var archive = CreateRealArchive();
        var digest = mismatch == "hash" ? new string('A', 64) : Convert.ToHexString(SHA256.HashData(archive));
        var tag = mismatch == "version" ? "v99.0.0" : $"v{ProductVersion.Text}";
        var json = GitHubReleaseParserTests.CreateJson(tag, digest: digest);
        if (mismatch == "uri") json = json.Replace("dddeeprog/CodexQuotaTaskbar", "other/repo", StringComparison.Ordinal);
        using var http = new HttpClient(new FakeHandler(json, archive));
        using var service = new GitHubUpdateService(http, root);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.VerifyInstallerSourceAsync(HostExe, CancellationToken.None));
    }

    [Fact]
    public async Task Legacy_helper_attests_its_exact_bytes_via_official_release_without_process_start()
    {
        var archive = CreateRealArchive(); var digest = Convert.ToHexString(SHA256.HashData(archive));
        using var http = new HttpClient(new FakeHandler(GitHubReleaseParserTests.CreateJson($"v{ProductVersion.Text}", digest: digest), archive));
        using var service = new GitHubUpdateService(http, root);
        await service.VerifyInstallerSourceAsync(HostExe, CancellationToken.None);
    }

    private static string HostExe => Path.Combine(TestSupport.RepositoryPaths.Root, "src", "CodexQuotaTaskbar.Host", "bin", "Release", "net10.0-windows", "CodexQuotaTaskbar.exe");
    private static byte[] CreateRealArchive()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        using (var output = archive.CreateEntry("CodexQuotaTaskbar.exe").Open()) output.Write(File.ReadAllBytes(HostExe));
        return memory.ToArray();
    }

    [Fact]
    public async Task Mz_prefix_without_real_pe_is_rejected_before_staging()
    {
        var archive = CreateArchive();
        var digest = Convert.ToHexString(SHA256.HashData(archive));
        using var http = new HttpClient(new FakeHandler("", archive));
        using var service = new GitHubUpdateService(http, root);
        var release = GitHubReleaseParser.Parse(GitHubReleaseParserTests.CreateJson("v99.0.0", digest: digest))!;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAndStageAsync(release, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(root, "v99.0.0", "CodexQuotaTaskbar.exe")));
    }

    [Fact]
    public async Task Current_release_is_not_offered_as_an_update()
    {
        using var http = new HttpClient(new FakeHandler(GitHubReleaseParserTests.CreateJson($"v{ProductVersion.Text}"), []));
        using var service = new GitHubUpdateService(http, root);

        Assert.Null(await service.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Older_official_release_does_not_downgrade_a_local_build()
    {
        var older = new Version(ProductVersion.Value.Major, ProductVersion.Value.Minor, ProductVersion.Value.Build - 1);
        using var http = new HttpClient(new FakeHandler(GitHubReleaseParserTests.CreateJson($"v{older}"), []));
        using var service = new GitHubUpdateService(http, root);

        Assert.Null(await service.CheckAsync(CancellationToken.None));
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("CodexQuotaTaskbar-win-x64.zip", "DianDao-Setup-0.1.12.exe")]
    [InlineData("sha256:", "missing:")]
    [InlineData("tag_name", "missing_tag")]
    public async Task Incompatible_release_is_a_failed_check_instead_of_no_update(string before, string after)
    {
        var json = GitHubReleaseParserTests.CreateJson("v99.0.0").Replace(before, after, StringComparison.Ordinal);
        using var http = new HttpClient(new FakeHandler(json, []));
        using var service = new GitHubUpdateService(http, root);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync(CancellationToken.None));
        Assert.False(Directory.Exists(root));
    }

    private static byte[] CreateArchive()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("CodexQuotaTaskbar.exe", CompressionLevel.NoCompression);
            using var stream = entry.Open();
            var executable = new byte[2048];
            executable[0] = (byte)'M';
            executable[1] = (byte)'Z';
            stream.Write(executable);
        }
        return memory.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class LocalArchiveHandler(string path) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(path)) });
    }

    private sealed class FakeHandler(string releaseJson, byte[] archive) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = request.RequestUri!.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releaseJson) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) };
            return Task.FromResult(response);
        }
    }
}
