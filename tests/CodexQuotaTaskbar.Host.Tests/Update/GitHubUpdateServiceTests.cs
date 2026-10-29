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
    private readonly string root = Path.Combine(Path.GetTempPath(), "CodexQuotaTaskbar.UpdateTests", Guid.NewGuid().ToString("N"));

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
    public async Task Downloads_hashes_and_extracts_newer_official_release()
    {
        var archive = CreateArchive();
        var digest = Convert.ToHexString(SHA256.HashData(archive));
        var nextVersion = new Version(ProductVersion.Value.Major, ProductVersion.Value.Minor, ProductVersion.Value.Build + 1);
        using var http = new HttpClient(new FakeHandler(GitHubReleaseParserTests.CreateJson($"v{nextVersion}", digest: digest), archive));
        using var service = new GitHubUpdateService(http, root);

        var release = await service.CheckAsync(CancellationToken.None);
        var staged = await service.DownloadAndStageAsync(Assert.IsType<GitHubRelease>(release), CancellationToken.None);

        Assert.Equal("CodexQuotaTaskbar.exe", Path.GetFileName(staged));
        Assert.True(File.Exists(staged));
        Assert.Equal('M', File.ReadAllText(staged)[0]);
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
