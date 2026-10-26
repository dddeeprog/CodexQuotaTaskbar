using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexQuotaTaskbar.Host.Shortcuts;

namespace CodexQuotaTaskbar.Host.Tests.Shortcuts;

public sealed class WebShortcutIconServiceTests
{
    [Fact]
    public async Task Bookmark_path_query_fragment_and_credentials_are_not_sent_to_the_site_or_icon_host()
    {
        using var fixture = new Fixture((request, _) =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.Null(request.Headers.Referrer);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.DoesNotContain("private", request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
            return Task.FromResult(request.RequestUri.AbsolutePath == "/" ? Html("<title>Example &amp; Site</title><link rel='icon' href='/brand.png'>") : Png());
        });
        var result = await fixture.Service.FetchAsync(new Uri("https://example.com/private/path?private=secret#private"));
        Assert.Equal(new[] { "https://example.com/", "https://example.com/brand.png" }, fixture.Requests.ToArray());
        Assert.Equal("Example & Site", result.Name);
        Assert.NotNull(result.Image);
        Assert.True(result.Image.IsFrozen);
        Assert.True(File.Exists(result.CachedPngPath));
    }

    [Fact]
    public void Metadata_uses_declared_icons_in_document_order_and_resolves_base_relative_entities()
    {
        var metadata = Discover("""
            <title> An &amp; example <b>title</b> </title><base href='/assets/'>
            <link REL='shortcut icon' href='small.png?x=1&amp;y=2'>
            <link rel='apple-touch-icon' href='/touch.png'><link rel=icon href=small.png?x=1&amp;y=2>
            """, "https://example.com/landing/");
        Assert.Equal("An & example title", metadata.Name);
        Assert.Equal(new[] { "https://example.com/assets/small.png?x=1&y=2", "https://example.com/touch.png" }, metadata.Icons.Select(uri => uri.AbsoluteUri));
    }

    [Fact]
    public void Comments_scripts_svg_and_dangerous_links_are_not_site_icon_declarations()
    {
        var metadata = Discover("""
            <!-- <link rel=icon href='/comment.png'> -->
            <script>var s="<link rel=icon href='/script.png'>";</script>
            <style>.x:before{content:"<link rel=icon href='/style.png'>"}</style>
            <link rel=icon href='data:image/png;base64,anything'><link rel=icon href='file:///private.ico'>
            <link rel=icon href='/only.svg' type='image/svg+xml'><link rel=icon href='javascript:alert(1)'>
            """);
        Assert.Empty(metadata.Icons);
    }

    [Fact]
    public async Task Invalid_declared_images_fall_back_to_the_origin_favicon_without_preventing_a_shortcut()
    {
        using var fixture = new Fixture((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/" => Html("<title>Example</title><link rel=icon href='/broken.png'>"),
            "/broken.png" => Bytes("<script>not an image</script>"u8.ToArray(), "image/png"),
            "/favicon.ico" => Png(),
            _ => new(HttpStatusCode.NotFound),
        }));
        var result = await fixture.Service.FetchAsync(new Uri("https://example.com/"));
        Assert.Equal("Example", result.Name);
        Assert.NotNull(result.Image);
        Assert.EndsWith("/favicon.ico", fixture.Requests.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Metadata_failure_still_tries_the_origin_favicon()
    {
        using var fixture = new Fixture((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/" ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Png()));
        var result = await fixture.Service.FetchAsync(new Uri("https://example.com/saved/page"));
        Assert.NotNull(result.Image);
        Assert.Equal(new[] { "https://example.com/", "https://example.com/favicon.ico" }, fixture.Requests.ToArray());
    }

    [Fact]
    public async Task Failure_keeps_the_plain_site_name_and_returns_a_placeholder_instead_of_throwing()
    {
        using var fixture = new Fixture((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/" ? Html("<title> Example &amp; Site </title>") : new(HttpStatusCode.NotFound)));
        var result = await fixture.Service.FetchAsync(new Uri("https://example.com/"));
        Assert.Equal("Example & Site", result.Name);
        Assert.Null(result.Image);
        Assert.Null(result.CachedPngPath);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/notepad.exe")]
    [InlineData("data:text/html,hello")]
    [InlineData("https://user:password@example.com/")]
    [InlineData("https://example.com:8443/")]
    public async Task Unsupported_automatic_lookup_targets_return_placeholders_without_network_calls(string url)
    {
        using var fixture = new Fixture((_, _) => throw new InvalidOperationException("This input must not reach HTTP."));
        var value = await fixture.Service.FetchAsync(new Uri(url));
        Assert.Null(value.Image);
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task A_redirect_to_a_private_route_is_rechecked_before_any_request_to_that_route()
    {
        var checkedTargets = new ConcurrentQueue<string>();
        using var fixture = new Fixture((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/"
            ? Redirect("https://127.0.0.1/secret") : new(HttpStatusCode.NotFound)), allowed: (uri, _) =>
        {
            checkedTargets.Enqueue(uri.AbsoluteUri);
            return Task.FromResult(uri.Host != "127.0.0.1");
        });
        var value = await fixture.Service.FetchAsync(new Uri("https://example.com/"));
        Assert.Null(value.Image);
        Assert.Contains("https://127.0.0.1/secret", checkedTargets);
        Assert.DoesNotContain(fixture.Requests, url => new Uri(url).Host == "127.0.0.1");
    }

    [Fact]
    public async Task Redirects_cannot_turn_a_site_lookup_into_a_third_party_favicon_service_request()
    {
        using var fixture = new Fixture((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/"
            ? Redirect("https://icons.duckduckgo.com/ip3/example.com.ico") : Png()));
        Assert.NotNull((await fixture.Service.FetchAsync(new Uri("https://example.com/"))).Image);
        Assert.Equal(new[] { "https://example.com/", "https://example.com/favicon.ico" }, fixture.Requests.ToArray());
    }

    [Fact]
    public async Task Relative_icons_use_the_final_redirected_page_address()
    {
        using var fixture = new Fixture((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/" => Redirect("/landing/"),
            "/landing/" => Html("<link rel=icon href='brand.png'>"),
            "/landing/brand.png" => Png(),
            _ => new(HttpStatusCode.NotFound),
        }));
        var value = await fixture.Service.FetchAsync(new Uri("https://example.com/"));
        Assert.NotNull(value.Image);
        Assert.Equal("https://example.com/landing/brand.png", fixture.Requests.Last());
    }

    [Fact]
    public async Task Https_downgrades_and_unbounded_redirect_chains_are_not_followed()
    {
        using var downgrade = new Fixture((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/" ? Redirect("http://example.com/unprotected") : new(HttpStatusCode.NotFound)));
        Assert.Null((await downgrade.Service.FetchAsync(new Uri("https://example.com/"))).Image);
        Assert.All(downgrade.Requests, url => Assert.Equal("https", new Uri(url).Scheme));

        var redirects = 0;
        using var loop = new Fixture((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/favicon.ico" ? new HttpResponseMessage(HttpStatusCode.NotFound) : Redirect("/hop-" + Interlocked.Increment(ref redirects))));
        Assert.Null((await loop.Service.FetchAsync(new Uri("https://example.com/"))).Image);
        Assert.InRange(loop.Requests.Count, 1, 5);
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("text/html")]
    [InlineData("application/octet-stream")]
    public async Task Unsupported_icon_types_are_rejected_even_if_the_body_happens_to_be_png(string mediaType)
    {
        using var fixture = new Fixture((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/" ? Html("<link rel=icon href='/icon'>") : Bytes(PngBytes(), mediaType)));
        Assert.Null((await fixture.Service.FetchAsync(new Uri("https://example.com/"))).Image);
        Assert.Empty(Directory.EnumerateFiles(fixture.Cache));
    }

    [Fact]
    public async Task Response_byte_bounds_apply_even_without_a_content_length()
    {
        using var fixture = new Fixture((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/" ? Html("<title>Bounded</title><link rel=icon href='/big'>")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(new byte[WebShortcutIconService.MaximumIconBytes + 1], "image/png") }));
        var value = await fixture.Service.FetchAsync(new Uri("https://example.com/"));
        Assert.Null(value.Image);
        Assert.Equal("Bounded", value.Name);
    }

    [Fact]
    public async Task A_second_service_reuses_the_normalized_png_cache_without_http()
    {
        using var fixture = new Fixture((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/" ? Html("<link rel=icon href='/icon.png'>") : Png()));
        var first = await fixture.Service.FetchAsync(new Uri("https://example.com/"));
        Assert.NotNull(first.Image);
        using var restarted = new WebShortcutIconService(new FakeHandler((_, _) => throw new InvalidOperationException("Fresh disk cache must avoid HTTP.")), fixture.Cache,
            networkAllowed: (_, _) => Task.FromResult(true));
        var value = await restarted.FetchAsync(new Uri("https://example.com/different-path#saved"));
        Assert.NotNull(value.Image);
        Assert.True(value.Image.IsFrozen);
        Assert.Equal(first.CachedPngPath, value.CachedPngPath);
    }

    [Fact]
    public async Task Corrupt_disk_cache_is_ignored_and_refetched_without_losing_the_shortcut()
    {
        using var fixture = new Fixture((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/" ? Html("<link rel=icon href='/icon.png'>") : Png()));
        var first = await fixture.Service.FetchAsync(new Uri("https://example.com/"));
        await File.WriteAllTextAsync(first.CachedPngPath!, "corrupt");
        var calls = 0;
        using var restarted = new WebShortcutIconService(new FakeHandler((request, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(request.RequestUri!.AbsolutePath == "/" ? Html("<link rel=icon href='/icon.png'>") : Png());
        }), fixture.Cache, networkAllowed: (_, _) => Task.FromResult(true));
        Assert.NotNull((await restarted.FetchAsync(new Uri("https://example.com/"))).Image);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Concurrent_shortcuts_share_the_origin_lookup_and_one_canceled_waiter_does_not_abort_it()
    {
        var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/")
            {
                reached.TrySetResult(true);
                await resume.Task.WaitAsync(token);
                return Html("<link rel=icon href='/icon.png'>");
            }
            return Png();
        });
        using var canceled = new CancellationTokenSource();
        var first = fixture.Service.FetchAsync(new Uri("https://example.com/private"), canceled.Token);
        var second = fixture.Service.FetchAsync(new Uri("https://example.com/other"));
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(3));
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        resume.SetResult(true);
        Assert.NotNull((await second).Image);
        Assert.NotNull((await fixture.Service.FetchAsync(new Uri("https://example.com/third"))).Image);
        Assert.Equal(2, fixture.Requests.Count);
    }

    [Fact]
    public async Task Different_origins_never_run_more_than_two_downloads_at_once()
    {
        var active = 0;
        var maximum = 0;
        var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(async (request, token) =>
        {
            var now = Interlocked.Increment(ref active);
            int observed;
            do { observed = maximum; if (observed >= now) break; } while (Interlocked.CompareExchange(ref maximum, now, observed) != observed);
            try
            {
                if (now == 2) reached.TrySetResult(true);
                await resume.Task.WaitAsync(token);
                return request.RequestUri!.AbsolutePath == "/" ? Html("<link rel=icon href='/icon.png'>") : Png();
            }
            finally { Interlocked.Decrement(ref active); }
        });
        var results = Enumerable.Range(1, 6).Select(index => fixture.Service.FetchAsync(new Uri($"https://site-{index}.example/"))).ToArray();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, Volatile.Read(ref maximum));
        resume.SetResult(true);
        Assert.All(await Task.WhenAll(results), value => Assert.NotNull(value.Image));
        Assert.Equal(2, maximum);
    }

    [Theory]
    [InlineData("0.0.0.0", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("172.16.4.5", false)]
    [InlineData("192.168.1.2", false)]
    [InlineData("192.0.2.1", false)]
    [InlineData("198.51.100.1", false)]
    [InlineData("203.0.113.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("::ffff:192.168.1.2", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("2002:0a00:0001::1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void Only_public_addresses_are_eligible_for_automatic_metadata_connections(string address, bool allowed) =>
        Assert.Equal(allowed, WebShortcutIconService.IsPublicAddress(IPAddress.Parse(address)));

    private static (string? Name, IReadOnlyList<Uri> Icons) Discover(string html, string page = "https://example.com/") =>
        WebShortcutIconService.Discover(Encoding.UTF8.GetBytes(html), new Uri(page));

    private static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };
    private static HttpResponseMessage Png() => Bytes(PngBytes(), "image/png");
    private static HttpResponseMessage Bytes(byte[] bytes, string type)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new(type);
        return response;
    }
    private static HttpResponseMessage Redirect(string target) => new(HttpStatusCode.Found) { Headers = { Location = new Uri(target, UriKind.RelativeOrAbsolute) } };
    private static byte[] PngBytes()
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 20, 80, 180, 255 }, 4)));
        using var bytes = new MemoryStream();
        encoder.Save(bytes);
        return bytes.ToArray();
    }

    private sealed class Fixture : IDisposable
    {
        public string Cache { get; } = Path.Combine(Path.GetTempPath(), "diandao-icon-service-tests-" + Guid.NewGuid().ToString("N"));
        public ConcurrentQueue<string> Requests { get; } = new();
        public WebShortcutIconService Service { get; }
        public Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
            Func<Uri, CancellationToken, Task<bool>>? allowed = null)
        {
            Directory.CreateDirectory(Cache);
            Service = new(new FakeHandler((request, token) =>
            {
                Requests.Enqueue(request.RequestUri!.AbsoluteUri);
                return handler(request, token);
            }), Cache, networkAllowed: allowed ?? ((_, _) => Task.FromResult(true)));
        }
        public void Dispose()
        {
            Service.Dispose();
            var full = Path.GetFullPath(Cache);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()) + "diandao-icon-service-tests-", full, StringComparison.OrdinalIgnoreCase);
            if (!Directory.Exists(full)) return;
            foreach (var file in Directory.EnumerateFiles(full)) File.Delete(file);
            Directory.Delete(full, recursive: false);
        }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] bytes;
        public UnknownLengthContent(byte[] bytes, string type)
        {
            this.bytes = bytes;
            Headers.ContentType = new(type);
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }
}
