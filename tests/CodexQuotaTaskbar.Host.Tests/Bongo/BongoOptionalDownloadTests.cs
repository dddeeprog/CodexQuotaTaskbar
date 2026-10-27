using System.IO;
using System.Net;
using System.Net.Http;
using CodexQuotaTaskbar.Host.Bongo;

namespace CodexQuotaTaskbar.Host.Tests.Bongo;

public sealed class BongoOptionalDownloadTests
{
    [Fact]
    public async Task No_consent_makes_zero_requests()
    {
        var handler = new Fake(_ => throw new Exception("Network must not run")); using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => BongoOptionalDownload.ReadAsync(client, BongoOptionalDownload.Address, () => Task.FromResult(false), CancellationToken.None));
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task Only_fixed_version_asset_is_accepted_without_cookies_or_user_data()
    {
        var handler = new Fake(request => { Assert.Equal(BongoOptionalDownload.Address, request.RequestUri); Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie")); return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }; });
        using var client = new HttpClient(handler);
        Assert.Equal(new byte[] { 1, 2, 3 }, await BongoOptionalDownload.ReadAsync(client, BongoOptionalDownload.Address, () => Task.FromResult(true), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => BongoOptionalDownload.ReadAsync(client, new("https://example.com/other.zip"), () => Task.FromResult(true), CancellationToken.None));
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData("http://github.com/file")]
    [InlineData("https://example.com/file")]
    [InlineData("https://github.com:444/file")]
    [InlineData("https://user@github.com/file")]
    public async Task Unsafe_redirects_are_rejected(string target)
    {
        var handler = new Fake(_ => { var r = new HttpResponseMessage(HttpStatusCode.Redirect); r.Headers.Location = new(target); return r; }); using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => BongoOptionalDownload.ReadAsync(client, BongoOptionalDownload.Address, () => Task.FromResult(true), CancellationToken.None));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Oversize_failed_and_revoked_downloads_do_not_return_installable_bytes()
    {
        using var huge = new HttpClient(new Fake(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[BongoPayload.MaximumPayloadBytes + 1]) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => BongoOptionalDownload.ReadAsync(huge, BongoOptionalDownload.Address, () => Task.FromResult(true), CancellationToken.None));
        using var denied = new HttpClient(new Fake(_ => new(HttpStatusCode.Forbidden)));
        await Assert.ThrowsAsync<HttpRequestException>(() => BongoOptionalDownload.ReadAsync(denied, BongoOptionalDownload.Address, () => Task.FromResult(true), CancellationToken.None));
        var checks = 0; using var revoked = new HttpClient(new Fake(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => BongoOptionalDownload.ReadAsync(revoked, BongoOptionalDownload.Address, () => Task.FromResult(++checks == 1), CancellationToken.None));
    }

    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests++; return Task.FromResult(reply(request)); }
    }
}
