using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexQuotaTaskbar.Host.Resets;

namespace CodexQuotaTaskbar.Host.Tests.Resets;

public sealed class CodexResetFeedClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Loading_and_unavailable_states_do_not_invent_a_latest_announcement()
    {
        Assert.True(ResetFeedSnapshot.Loading.IsLoading);
        Assert.Null(ResetFeedSnapshot.Loading.Latest);
        Assert.Null(ResetFeedSnapshot.Unavailable().Latest);
        Assert.Null(ResetFeedSnapshot.Unavailable().LastUpdatedAt);
        Assert.NotNull(ResetFeedSnapshot.Unavailable().ErrorMessage);
    }

    [Fact]
    public void Real_handler_disables_credentials_cookies_and_redirects()
    {
        using var handler = CodexResetFeedClient.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Null(handler.Credentials);
        Assert.Equal(TimeSpan.FromSeconds(8), handler.ConnectTimeout);
    }

    [Fact]
    public async Task Fetches_only_the_public_endpoint_without_account_headers()
    {
        using var service = new CodexResetFeedClient(new FakeHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://codex-resets.com/api/v1/resets?limit=3&order=desc", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Content);
            Assert.Null(request.Headers.Authorization);
            Assert.Null(request.Headers.Referrer);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal(["Accept", "User-Agent"], request.Headers.Select(header => header.Key).ToArray());
            return Task.FromResult(JsonResponse(Feed(Announcement())));
        }), new TestClock());

        var snapshot = await service.FetchAsync();

        Assert.Equal(Now, snapshot.LastUpdatedAt);
        Assert.False(snapshot.IsStale);
        Assert.False(snapshot.IsLoading);
        Assert.Null(snapshot.ErrorMessage);
        var announcement = Assert.Single(snapshot.Announcements);
        Assert.Equal(ResetKind.Banked, announcement.Kind);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 3, 44, 55, TimeSpan.Zero), announcement.AnnouncedAt);
        Assert.Equal("https://x.com/thsottiaux/status/123", announcement.SourceUri.AbsoluteUri);
        Assert.False(announcement.IsObserved);
    }

    [Fact]
    public void Sorts_deduplicates_and_limits_to_three_recent_announcements()
    {
        var parsed = Parse(Feed(
            Announcement("1", "regular", "2026-10-02T01:00:00Z"),
            Announcement("2", "banked", "2026-10-08T01:00:00Z"),
            Announcement("3", "regular", "2026-10-07T01:00:00Z"),
            Announcement("2", "banked", "2026-10-08T01:00:00Z"),
            Announcement("4", "regular", "2026-10-01T01:00:00Z")));

        Assert.Equal(["2", "3", "1"], parsed.Select(item => item.Id).ToArray());
        Assert.Equal(ResetKind.Regular, parsed[1].Kind);
    }

    [Fact]
    public void Does_not_treat_future_announcements_or_forecasts_as_executed_resets()
    {
        var future = Parse(Feed(Announcement(at: "2026-10-09T01:00:00Z")));
        Assert.Empty(future);
        Assert.Throws<InvalidDataException>(() => Parse("{\"data\":{\"active_watch\":{\"reset_chance_percent\":100}}}"));
    }

    [Fact]
    public void Observations_are_labelled_and_link_to_the_tracker_not_untrusted_external_urls()
    {
        var json = "{\"data\":[{\"id\":\"observed-123\",\"reset_type\":\"regular\",\"announced_at\":\"2026-10-08T01:00:00Z\",\"text\":\"Observed reset\",\"source\":{\"type\":\"observed\",\"url\":\"file:///C:/private\"}}]}";
        var announcement = Assert.Single(Parse(json));
        Assert.True(announcement.IsObserved);
        Assert.Equal(CodexResetFeedClient.SiteUri, announcement.SourceUri);
    }

    [Fact]
    public void Preserves_original_preview_without_format_controls_or_broken_surrogates()
    {
        var announcement = Assert.Single(Parse(Feed(Announcement(text: "First\n\tsecond\u202E third"))));
        Assert.Equal("First second third", announcement.Text);

        var longText = new string('a', 238) + "🦊" + new string('b', 100);
        announcement = Assert.Single(Parse(Feed(Announcement(text: longText))));
        Assert.Equal(new string('a', 238) + "…", announcement.Text);
        Assert.True(announcement.Text.Length <= 240);
    }

    [Theory]
    [InlineData("http://x.com/thsottiaux/status/123")]
    [InlineData("https://x.com.evil.test/thsottiaux/status/123")]
    [InlineData("https://user@x.com/thsottiaux/status/123")]
    [InlineData("https://x.com:444/thsottiaux/status/123")]
    [InlineData("https://x.com/another/status/123")]
    [InlineData("https://x.com/thsottiaux/status/999")]
    [InlineData("https://x.com/thsottiaux/status/123?account=private")]
    [InlineData("https://x.com/thsottiaux/status/123#spoof")]
    [InlineData("javascript:alert(1)")]
    public void Rejects_unsafe_or_unrelated_source_links(string url)
    {
        Assert.Throws<InvalidDataException>(() => Parse(Feed(Announcement(sourceUrl: url))));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":[null]}")]
    [InlineData("{\"data\":[{}]}")]
    public void Rejects_missing_or_malformed_fields(string json)
    {
        Assert.Throws<InvalidDataException>(() => Parse(json));
    }

    [Fact]
    public void Rejects_unknown_reset_kinds_and_invalid_dates()
    {
        Assert.Throws<InvalidDataException>(() => Parse(Feed(Announcement(kind: "predicted"))));
        Assert.Throws<InvalidDataException>(() => Parse(Feed(Announcement(at: "not a date"))));
        Assert.Throws<InvalidDataException>(() => Parse(Feed(Announcement(at: "2026-10-08T03:44:55"))));
    }

    [Theory]
    [InlineData("\\uD800")]
    [InlineData("\\uDC00")]
    public async Task Invalid_unicode_in_public_fields_is_an_unavailable_state_not_an_unhandled_exception(string escape)
    {
        var json = Feed(Announcement(text: "invalid-marker")).Replace("invalid-marker", escape, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => Parse(json));
        using var service = Client(JsonResponse(json));
        Assert.NotNull((await service.FetchAsync()).ErrorMessage);
    }

    [Fact]
    public async Task Empty_feed_is_a_successful_empty_state_not_an_error()
    {
        using var service = Client(JsonResponse(Feed()));
        var snapshot = await service.FetchAsync();
        Assert.Empty(snapshot.Announcements);
        Assert.Null(snapshot.ErrorMessage);
        Assert.Equal(Now, snapshot.LastUpdatedAt);
    }

    [Fact]
    public async Task Revalidates_with_etag_and_retains_data_after_not_modified()
    {
        var clock = new TestClock();
        var calls = 0;
        using var service = new CodexResetFeedClient(new FakeHandler((request, _) =>
        {
            if (calls++ == 0)
            {
                var response = JsonResponse(Feed(Announcement()));
                response.Headers.ETag = new EntityTagHeaderValue("\"feed-1\"");
                return Task.FromResult(response);
            }
            Assert.Equal("\"feed-1\"", Assert.Single(request.Headers.IfNoneMatch).ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
        }), clock);

        var original = await service.FetchAsync();
        clock.Now += TimeSpan.FromMinutes(15);
        var cached = await service.FetchAsync();

        Assert.Equal(original.Latest, cached.Latest);
        Assert.Equal(clock.Now, cached.LastUpdatedAt);
        Assert.False(cached.IsStale);
    }

    [Fact]
    public async Task Failure_preserves_last_successful_data_and_does_not_leak_server_details()
    {
        var calls = 0;
        using var service = new CodexResetFeedClient(new FakeHandler((_, _) => calls++ == 0
            ? Task.FromResult(JsonResponse(Feed(Announcement())))
            : throw new HttpRequestException("private account detail must not reach UI")), new TestClock());

        var original = await service.FetchAsync();
        var stale = await service.FetchAsync();

        Assert.True(stale.IsStale);
        Assert.Equal(original.LastUpdatedAt, stale.LastUpdatedAt);
        Assert.Equal(original.Latest, stale.Latest);
        Assert.NotNull(stale.ErrorMessage);
        Assert.DoesNotContain("private", stale.ErrorMessage);
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NotModified)]
    public async Task Unsuccessful_or_redirect_responses_are_an_honest_unavailable_state(HttpStatusCode status)
    {
        using var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri("https://unrelated.invalid/");
        using var service = Client(response);
        var result = await service.FetchAsync();
        Assert.Null(result.Latest);
        Assert.NotNull(result.ErrorMessage);
        Assert.False(result.IsStale);
    }

    [Fact]
    public async Task Rate_limit_retry_after_is_respected_without_sleeping()
    {
        var clock = new TestClock();
        var calls = 0;
        using var service = new CodexResetFeedClient(new FakeHandler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(30));
            return Task.FromResult(response);
        }), clock);

        Assert.NotNull((await service.FetchAsync()).ErrorMessage);
        Assert.NotNull((await service.FetchAsync()).ErrorMessage);
        Assert.Equal(1, calls);
        clock.Now += TimeSpan.FromMinutes(31);
        await service.FetchAsync();
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Response_size_is_bounded_even_when_content_length_is_missing()
    {
        using var service = Client(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(new byte[CodexResetFeedClient.MaximumResponseBytes + 1]),
        });

        Assert.NotNull((await service.FetchAsync()).ErrorMessage);
    }

    [Fact]
    public async Task Rejects_oversized_content_length_without_reading_body()
    {
        var response = JsonResponse(Feed(Announcement()));
        response.Content.Headers.ContentLength = CodexResetFeedClient.MaximumResponseBytes + 1;
        using var service = Client(response);
        Assert.NotNull((await service.FetchAsync()).ErrorMessage);
    }

    [Fact]
    public async Task Rejects_html_and_malformed_json()
    {
        using var html = Client(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>challenge</html>") });
        Assert.NotNull((await html.FetchAsync()).ErrorMessage);
        using var invalid = Client(JsonResponse("not json"));
        Assert.NotNull((await invalid.FetchAsync()).ErrorMessage);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_converted_into_an_error_snapshot()
    {
        using var cancellation = new CancellationTokenSource();
        using var service = new CodexResetFeedClient(new FakeHandler((_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unreachable");
        }), new TestClock());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.FetchAsync(cancellation.Token));
    }

    [Fact]
    public async Task Internal_timeout_returns_an_unavailable_state()
    {
        using var service = new CodexResetFeedClient(new FakeHandler((_, _) => throw new TaskCanceledException()), new TestClock());
        var snapshot = await service.FetchAsync();
        Assert.Contains("超时", snapshot.ErrorMessage);
        Assert.Null(snapshot.Latest);
    }

    private static IReadOnlyList<ResetAnnouncement> Parse(string json) =>
        CodexResetFeedClient.Parse(Encoding.UTF8.GetBytes(json), Now);

    private static CodexResetFeedClient Client(HttpResponseMessage response) =>
        new(new FakeHandler((_, _) => Task.FromResult(response)), new TestClock());

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static string Feed(params string[] announcements) => "{\"data\":[" + string.Join(',', announcements) + "]}";

    private static string Announcement(string id = "123", string kind = "banked", string at = "2026-10-08T03:44:55Z", string text = "A reset was announced.", string? sourceUrl = null) =>
        JsonSerializer.Serialize(new
        {
            id,
            reset_type = kind,
            announced_at = at,
            text,
            source = new { type = "x_post", author = "thsottiaux", url = sourceUrl ?? $"https://x.com/thsottiaux/status/{id}" },
        });

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = CodexResetFeedClientTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri == CodexResetFeedClient.TranslationUri
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
                : send(request, cancellationToken);
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] data;

        public UnknownLengthContent(byte[] data)
        {
            this.data = data;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(data).AsTask();

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(data));

    }
}
