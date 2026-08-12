using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using CodexQuotaTaskbar.Host.Update;

namespace CodexQuotaTaskbar.Host.Tests.Update;

public sealed class GitHubUpdateServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CodexQuotaTaskbar.UpdateTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Downloads_hashes_and_extracts_newer_official_release()
    {
        var archive = CreateArchive();
        var digest = Convert.ToHexString(SHA256.HashData(archive));
        using var http = new HttpClient(new FakeHandler(GitHubReleaseParserTests.CreateJson("v0.1.8", digest: digest), archive));
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
        using var http = new HttpClient(new FakeHandler(GitHubReleaseParserTests.CreateJson("v0.1.7"), []));
        using var service = new GitHubUpdateService(http, root);

        Assert.Null(await service.CheckAsync(CancellationToken.None));
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
