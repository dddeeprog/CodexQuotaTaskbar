using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexQuotaTaskbar.Host.Resets;

namespace CodexQuotaTaskbar.Host.Tests.Resets;

public sealed class ResetScheduleClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Reads_only_documented_public_status_without_account_or_cache_busting_headers()
    {
        using var service = new ResetScheduleClient(new FakeHandler((request, _) =>
        {
            Assert.Equal("https://codex-resets.com/api/v1/status", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Content);
            Assert.Null(request.Headers.Authorization);
            Assert.Null(request.Headers.Referrer);
            Assert.Equal(["Accept", "User-Agent"], request.Headers.Select(h => h.Key).ToArray());
            return Task.FromResult(Json(Status(Schedule())));
        }), new Clock());
        var value = await service.FetchAsync();
        Assert.False(value.IsStale);
        Assert.False(value.IsLoading);
        Assert.Equal(Now, value.LastUpdatedAt);
        Assert.Equal(Now, value.SourceGeneratedAt);
        Assert.Equal("123", value.Schedule!.Id);
        Assert.Equal(ResetKind.Regular, value.Schedule.Kind);
        Assert.Equal(Now.AddHours(1), value.Schedule.ScheduledFor);
        Assert.Equal("https://x.com/thsottiaux/status/123", value.Schedule.SourceUri.AbsoluteUri);
    }

    [Fact]
    public void Handler_has_no_redirects_credentials_or_cookies()
    {
        using var handler = CodexResetFeedClient.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Null(handler.Credentials);
    }

    [Fact]
    public void Null_schedule_is_an_explicit_current_no_arrangement_not_a_failure()
    {
        var snapshot = Parse(Status("null"));
        Assert.Null(snapshot.Schedule);
        Assert.NotNull(snapshot.LastUpdatedAt);
        Assert.False(snapshot.IsStale);
        Assert.Null(snapshot.ErrorMessage);
    }

    [Fact]
    public void Null_deadline_is_preserved_without_inventing_a_countdown()
    {
        var snapshot = Parse(Status(Schedule(noDeadline: true)));
        Assert.NotNull(snapshot.Schedule);
        Assert.Null(snapshot.Schedule.ScheduledFor);
    }

    [Fact]
    public void Passed_deadline_remains_a_pending_announcement_never_an_executed_reset()
    {
        var snapshot = Parse(Status(Schedule(deadline: Now.AddMinutes(-2))));
        Assert.NotNull(snapshot.Schedule);
        Assert.True(snapshot.Schedule.ScheduledFor is { } due && due < Now);
        Assert.False(snapshot.IsStale);
    }

    [Fact]
    public void Old_source_generation_is_marked_stale_even_after_a_successful_http_read()
    {
        var snapshot = Parse(Status(Schedule(), Now.AddMinutes(-3)));
        Assert.True(snapshot.IsStale);
        Assert.NotNull(snapshot.Schedule);
        Assert.Equal(Now.AddMinutes(-3), snapshot.SourceGeneratedAt);
        Assert.Contains("较旧", snapshot.ErrorMessage);
    }

    [Fact]
    public async Task Repeated_and_concurrent_calls_share_one_http_read_and_do_not_queue_extra_work()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var service = new ResetScheduleClient(new FakeHandler(async (_, token) =>
        {
            Interlocked.Increment(ref requests);
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return Json(Status(Schedule()));
        }), new Clock());
        var first = service.FetchAsync();
        await entered.Task;
        var others = Enumerable.Range(0, 12).Select(_ => service.FetchAsync()).ToArray();
        release.SetResult();
        var result = await first;
        var joined = await Task.WhenAll(others);
        Assert.All(joined, value => Assert.Equal(result, value));
        Assert.Equal(result, await service.FetchAsync());
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task Etag_revalidates_after_sixty_seconds_and_304_keeps_the_same_announcement()
    {
        var clock = new Clock();
        var count = 0;
        using var service = new ResetScheduleClient(new FakeHandler((request, _) =>
        {
            if (++count == 1)
            {
                Assert.Empty(request.Headers.IfNoneMatch);
                var response = Json(Status(Schedule()));
                response.Headers.ETag = new EntityTagHeaderValue("\"schedule-one\"", true);
                return Task.FromResult(response);
            }
            Assert.Equal("W/\"schedule-one\"", Assert.Single(request.Headers.IfNoneMatch).ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
        }), clock);
        var first = await service.FetchAsync();
        clock.Now = Now.AddSeconds(59);
        Assert.Equal(first, await service.FetchAsync());
        clock.Now = Now.AddSeconds(60);
        var second = await service.FetchAsync();
        Assert.Same(first.Schedule, second.Schedule);
        Assert.Equal(clock.Now, second.LastUpdatedAt);
        Assert.False(second.IsStale);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Canceling_a_queued_caller_does_not_cancel_the_inflight_request_or_start_another()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var service = new ResetScheduleClient(new FakeHandler(async (_, token) =>
        {
            requests++;
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return Json(Status(Schedule()));
        }), new Clock());
        var first = service.FetchAsync();
        await entered.Task;
        using var cancellation = new CancellationTokenSource();
        var queued = service.FetchAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        release.SetResult();
        Assert.NotNull((await first).Schedule);
        Assert.NotNull((await service.FetchAsync()).Schedule);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task A_304_without_a_successful_baseline_is_an_error()
    {
        using var service = Client(new HttpResponseMessage(HttpStatusCode.NotModified));
        var result = await service.FetchAsync();
        Assert.Null(result.Schedule);
        Assert.Null(result.LastUpdatedAt);
        Assert.NotNull(result.ErrorMessage);
    }

    [Theory]
    [InlineData(429, false)]
    [InlineData(429, true)]
    [InlineData(503, false)]
    [InlineData(503, true)]
    public async Task Retry_after_delta_or_date_is_respected_by_repeated_manual_calls(int code, bool useDate)
    {
        var clock = new Clock();
        var requests = 0;
        using var service = new ResetScheduleClient(new FakeHandler((_, _) =>
        {
            requests++;
            var response = new HttpResponseMessage((HttpStatusCode)code);
            response.Headers.RetryAfter = useDate
                ? new RetryConditionHeaderValue(Now.AddMinutes(10))
                : new RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return Task.FromResult(response);
        }), clock);
        var first = await service.FetchAsync();
        Assert.Contains("繁忙", first.ErrorMessage);
        clock.Now = Now.AddMinutes(9);
        await service.FetchAsync();
        Assert.Equal(1, requests);
        clock.Now = Now.AddMinutes(10);
        await service.FetchAsync();
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task Failures_retain_the_last_schedule_as_stale_then_recover_to_a_fresh_no_schedule()
    {
        var clock = new Clock();
        var count = 0;
        using var service = new ResetScheduleClient(new FakeHandler((_, _) =>
        {
            count++;
            if (count == 2) throw new HttpRequestException("offline");
            return Task.FromResult(Json(Status(count == 1 ? Schedule() : "null", clock.Now)));
        }), clock);
        var success = await service.FetchAsync();
        clock.Now = Now.AddMinutes(1);
        var failed = await service.FetchAsync();
        Assert.Same(success.Schedule, failed.Schedule);
        Assert.Equal(Now, failed.LastUpdatedAt);
        Assert.True(failed.IsStale);
        Assert.NotNull(failed.ErrorMessage);
        clock.Now = Now.AddMinutes(2);
        var recovered = await service.FetchAsync();
        Assert.Null(recovered.Schedule);
        Assert.False(recovered.IsStale);
        Assert.Null(recovered.ErrorMessage);
        Assert.Equal(clock.Now, recovered.LastUpdatedAt);
    }

    [Fact]
    public async Task Rate_limited_cached_data_expires_locally_without_a_new_http_request()
    {
        var clock = new Clock();
        var requests = 0;
        using var service = new ResetScheduleClient(new FakeHandler((_, _) =>
        {
            if (++requests == 1) return Task.FromResult(Json(Status(Schedule())));
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(2));
            return Task.FromResult(response);
        }), clock);
        await service.FetchAsync();
        clock.Now = Now.AddMinutes(1);
        await service.FetchAsync();
        clock.Now = Now.AddMinutes(4);
        var cached = await service.FetchAsync();
        Assert.True(cached.IsStale);
        Assert.Equal(Now, cached.LastUpdatedAt);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task Timeout_is_reported_but_lifetime_cancellation_propagates_and_releases_the_gate()
    {
        using var timeoutService = new ResetScheduleClient(new FakeHandler((_, _) => throw new TaskCanceledException()), new Clock());
        Assert.Contains("超时", (await timeoutService.FetchAsync()).ErrorMessage);
        var clock = new Clock();
        using var cancellation = new CancellationTokenSource();
        var count = 0;
        using var service = new ResetScheduleClient(new FakeHandler((_, token) =>
        {
            if (++count == 1)
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            return Task.FromResult(Json(Status(Schedule(), clock.Now)));
        }), clock);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.FetchAsync(cancellation.Token));
        Assert.NotNull((await service.FetchAsync()).Schedule);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Disposal_cancels_inflight_read_without_a_disposed_gate_release_race()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ResetScheduleClient(new FakeHandler(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json(Status("null"));
        }), new Clock());
        var pending = service.FetchAsync();
        await entered.Task;
        service.Dispose();
        Assert.NotNull((await pending).ErrorMessage);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.FetchAsync());
        service.Dispose();
    }

    [Theory]
    [InlineData("https://evil.example/thsottiaux/status/123")]
    [InlineData("https://x.com.evil.example/thsottiaux/status/123")]
    [InlineData("https://x.com/other/status/123")]
    [InlineData("https://x.com/thsottiaux/status/124")]
    [InlineData("https://x.com/thsottiaux/status/123?next=https://evil.example")]
    [InlineData("https://x.com/thsottiaux/status/123#other")]
    [InlineData("https://user@x.com/thsottiaux/status/123")]
    [InlineData("http://x.com/thsottiaux/status/123")]
    [InlineData("file:///C:/private")]
    public void Rejects_untrusted_or_mismatched_post_links(string url) =>
        Assert.Throws<InvalidDataException>(() => Parse(Status(Schedule(url: url))));

    [Fact]
    public void Observation_source_can_only_link_to_the_fixed_credit_site()
    {
        var schedule = Schedule(source: new { type = "observed", url = "file:///C:/private" });
        Assert.Equal(ResetScheduleClient.SiteUri, Parse(Status(schedule)).Schedule!.SourceUri);
        Assert.True(ResetScheduleClient.IsAllowedSource(ResetScheduleClient.SiteUri));
        Assert.False(ResetScheduleClient.IsAllowedSource(new Uri("/relative", UriKind.Relative)));
    }

    [Fact]
    public void Rejects_missing_or_ambiguous_schedule_fields_and_invalid_timestamps()
    {
        foreach (var json in new[]
        {
            "{}", "{\"data\":{},\"meta\":{}}",
            Status(Schedule()).Replace("\"scheduled_reset\":", "\"scheduled_reset\":null,\"scheduled_reset\":"),
            Status(Schedule()).Replace("\"status\":\"scheduled\"", "\"status\":\"completed\""),
            Status(Schedule()).Replace("\"api_version\":\"v1\"", "\"api_version\":\"v2\""),
            Status(Schedule()).Replace(JsonSerializer.Serialize(Now.ToString("O")), "\"2026-10-23T16:00:00\""),
            Status(Schedule(), Now.AddMinutes(6)),
            Status(Schedule(deadline: Now.AddHours(-5))),
        }) Assert.Throws<InvalidDataException>(() => Parse(json));
    }

    [Fact]
    public async Task Html_malformed_json_and_oversized_declared_or_streamed_content_fail_closed()
    {
        var declared = Json(Status(Schedule()));
        declared.Content.Headers.ContentLength = ResetScheduleClient.MaximumResponseBytes + 1;
        var streamed = new UnknownLengthContent(new byte[ResetScheduleClient.MaximumResponseBytes * 10]);
        var unknown = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = streamed,
        };
        foreach (var response in new[]
        {
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>challenge</html>") },
            Json("not-json"), declared, unknown,
        })
        {
            using var service = Client(response);
            var value = await service.FetchAsync();
            Assert.NotNull(value.ErrorMessage);
            Assert.Null(value.Schedule);
            Assert.Null(value.LastUpdatedAt);
        }
        Assert.InRange(streamed.BytesRead, ResetScheduleClient.MaximumResponseBytes + 1, ResetScheduleClient.MaximumResponseBytes + 8192);
    }

    private static ResetScheduleSnapshot Parse(string json) => ResetScheduleClient.Parse(Encoding.UTF8.GetBytes(json), Now);
    private static ResetScheduleClient Client(HttpResponseMessage response) => new(new FakeHandler((_, _) => Task.FromResult(response)), new Clock());
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static string Status(string schedule, DateTimeOffset? generated = null) =>
        "{\"data\":{\"scheduled_reset\":" + schedule + "},\"meta\":{\"api_version\":\"v1\",\"generated_at\":" + JsonSerializer.Serialize((generated ?? Now).ToString("O")) + "}}";
    private static string Schedule(string? url = null, DateTimeOffset? deadline = default, object? source = null, bool noDeadline = false) => JsonSerializer.Serialize(new
    {
        id = "123", status = "scheduled", reset_type = "regular", announced_at = Now.AddHours(-2).ToString("O"),
        scheduled_for = noDeadline ? null : (deadline ?? Now.AddHours(1)).ToString("O"), text = "Public scheduled announcement.",
        source = source ?? new { type = "x_post", author = "thsottiaux", url = url ?? "https://x.com/thsottiaux/status/123" },
    });

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = ResetScheduleClientTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly ProbeStream input;
        public long BytesRead => input.BytesRead;
        internal UnknownLengthContent(byte[] bytes)
        {
            input = new ProbeStream(bytes);
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => input.CopyToAsync(stream);
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(input);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => CreateContentReadStreamAsync();
    }
    private sealed class ProbeStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal long BytesRead { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }
    }
}
