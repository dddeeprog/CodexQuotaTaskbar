using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexQuotaTaskbar.Host.Resets;

namespace CodexQuotaTaskbar.Host.Tests.Resets;

public sealed class ResetFeedLocalizationIntegrationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-08T12:00:00Z");

    [Fact]
    public async Task Uses_only_two_public_read_only_endpoints_and_matches_the_sites_translation()
    {
        var requests = new List<Uri>();
        using var service = Client((request, _) =>
        {
            requests.Add(request.RequestUri!);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Content);
            Assert.Null(request.Headers.Authorization);
            Assert.Null(request.Headers.Referrer);
            Assert.False(request.Headers.Contains("Cookie"));
            return Task.FromResult(Json(request.RequestUri == CodexResetFeedClient.FeedUri ? Feed() : Translations()));
        });
        var result = await service.FetchAsync();
        Assert.Equal([CodexResetFeedClient.FeedUri, CodexResetFeedClient.TranslationUri], requests);
        Assert.Equal("已为所有账户重置额度。", result.Latest!.ChineseText);
        Assert.Equal("Reset all accounts. https://t.co/example", result.Latest.Text);
        Assert.Null(result.ErrorMessage);
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Translation_failure_never_invalidates_fresh_reset_facts(HttpStatusCode status)
    {
        using var service = Client((request, _) => Task.FromResult(request.RequestUri == CodexResetFeedClient.FeedUri
            ? Json(Feed()) : new HttpResponseMessage(status)));
        var result = await service.FetchAsync();
        Assert.NotNull(result.Latest);
        Assert.Null(result.Latest.ChineseText);
        Assert.Null(result.ErrorMessage);
        Assert.False(result.IsStale);
    }

    [Theory]
    [InlineData("not json", "application/json")]
    [InlineData("<html>verify</html>", "text/html")]
    [InlineData("{}", "application/json")]
    public async Task Bad_translation_response_preserves_original(string body, string mediaType)
    {
        using var service = Client((request, _) => Task.FromResult(request.RequestUri == CodexResetFeedClient.FeedUri
            ? Json(Feed()) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) }));
        Assert.Null((await service.FetchAsync()).ErrorMessage);
    }

    [Fact]
    public async Task Translation_size_limit_is_enforced_without_content_length()
    {
        using var service = Client((request, _) => Task.FromResult(request.RequestUri == CodexResetFeedClient.FeedUri
            ? Json(Feed()) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new OversizedContent() }));
        var result = await service.FetchAsync();
        Assert.Null(result.Latest!.ChineseText);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task Translation_timeout_is_optional_but_caller_cancellation_propagates()
    {
        using var service = Client((request, _) => request.RequestUri == CodexResetFeedClient.FeedUri
            ? Task.FromResult(Json(Feed())) : throw new TaskCanceledException());
        Assert.Null((await service.FetchAsync()).ErrorMessage);

        using var cancellation = new CancellationTokenSource();
        using var cancelled = Client((request, token) =>
        {
            if (request.RequestUri == CodexResetFeedClient.FeedUri) return Task.FromResult(Json(Feed()));
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unreachable");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.FetchAsync(cancellation.Token));
    }

    [Fact]
    public async Task Translation_can_arrive_after_the_canonical_feed_returns_304()
    {
        var feedCalls = 0;
        var translationCalls = 0;
        using var service = Client((request, _) =>
        {
            if (request.RequestUri == CodexResetFeedClient.TranslationUri)
                return Task.FromResult(translationCalls++ == 0 ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(Translations()));
            if (feedCalls++ != 0)
            {
                Assert.Equal("\"reset-1\"", Assert.Single(request.Headers.IfNoneMatch).ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            }
            var response = Json(Feed());
            response.Headers.ETag = new EntityTagHeaderValue("\"reset-1\"");
            return Task.FromResult(response);
        });
        Assert.Null((await service.FetchAsync()).Latest!.ChineseText);
        Assert.Equal("已为所有账户重置额度。", (await service.FetchAsync()).Latest!.ChineseText);
    }

    [Fact]
    public async Task Retains_matching_translation_on_failure_but_not_after_original_changes()
    {
        var feedCalls = 0;
        var translationCalls = 0;
        using var service = Client((request, _) => Task.FromResult(request.RequestUri == CodexResetFeedClient.FeedUri
            ? Json(Feed(feedCalls++ > 1 ? "Corrected announcement." : null))
            : translationCalls++ == 0 ? Json(Translations()) : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var original = await service.FetchAsync();
        Assert.Equal(original.Latest!.ChineseText, (await service.FetchAsync()).Latest!.ChineseText);
        Assert.Null((await service.FetchAsync()).Latest!.ChineseText);
    }

    [Fact]
    public async Task Translation_rate_limit_does_not_stop_canonical_feed_refresh()
    {
        var clock = new TestClock();
        var feedCalls = 0;
        var translationCalls = 0;
        using var service = Client((request, _) =>
        {
            if (request.RequestUri == CodexResetFeedClient.FeedUri)
            {
                feedCalls++;
                return Task.FromResult(Json(Feed()));
            }
            translationCalls++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(30));
            return Task.FromResult(response);
        }, clock);
        await service.FetchAsync();
        await service.FetchAsync();
        Assert.Equal(2, feedCalls);
        Assert.Equal(1, translationCalls);
        clock.Now += TimeSpan.FromMinutes(31);
        await service.FetchAsync();
        Assert.Equal(2, translationCalls);
    }

    private static string Feed(string? text = null) => JsonSerializer.Serialize(new
    {
        data = new[] { new { id = "123", reset_type = "banked", announced_at = "2026-10-08T03:44:55Z",
            text = text ?? "Reset all accounts. https://t.co/example",
            source = new { type = "x_post", author = "thsottiaux", url = "https://x.com/thsottiaux/status/123" } } },
    });

    private static string Translations() => JsonSerializer.Serialize(new
    {
        events = new[] { new { tweet_id = "123", reset_type = "banked", announced_at = "2026-10-08T03:44:55Z",
            text = "Reset all accounts. https://t.co/example", display_text = "已为所有账户重置额度。 https://t.co/example" } },
    });

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static CodexResetFeedClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, TestClock? clock = null) =>
        new(new Handler(send), clock ?? new TestClock());
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = ResetFeedLocalizationIntegrationTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class OversizedContent : HttpContent
    {
        public OversizedContent() => Headers.ContentType = new MediaTypeHeaderValue("application/json");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(new byte[CodexResetFeedClient.MaximumTranslationResponseBytes + 1]).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(new byte[CodexResetFeedClient.MaximumTranslationResponseBytes + 1]));
    }
}
