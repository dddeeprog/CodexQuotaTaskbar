using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Host.Resets;

namespace CodexQuotaTaskbar.Host.Tests.Resets;

public sealed class ResetScheduleClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Query_phase_spans_body_read_and_concurrent_cache_hits_do_not_restart_it()
    {
        var body = new GatedContent(Status("null"));
        body.SetJsonHeader();
        using var service = new ResetScheduleClient(new FakeHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = body })), new Clock());
        var phases = new List<ResetQueryState>();
        service.QueryStateChanged += phases.Add;
        var first = service.FetchAsync();
        await body.Entered.Task;
        Assert.Equal(ResetQueryPhase.Checking, service.QueryState.Phase);
        var waiter = service.FetchAsync();
        Assert.Single(phases);
        body.Release.SetResult();
        Assert.Null((await first).Schedule);
        await waiter; await service.FetchAsync();
        Assert.Equal(new[] { ResetQueryPhase.Checking, ResetQueryPhase.Idle }, phases.Select(p => p.Phase));
        Assert.Equal(new long[] { 1, 2 }, phases.Select(p => p.Revision));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_or_disposal_ends_checking_without_red_or_publishing_late_data(bool dispose)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var service = new ResetScheduleClient(new FakeHandler(async (_, _) =>
        {
            entered.SetResult(); await release.Task; // Deliberately ignores cancellation: late result must be rejected.
            return Json(Status(Schedule()));
        }), new Clock());
        var phases = new List<ResetQueryPhase>(); service.QueryStateChanged += p => phases.Add(p.Phase);
        var task = service.FetchAsync(cancellation.Token); await entered.Task;
        if (dispose) service.Dispose(); else cancellation.Cancel();
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(ResetQueryPhase.Idle, service.QueryState.Phase);
        Assert.DoesNotContain(ResetQueryPhase.Failed, phases);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("http")]
    [InlineData("parse")]
    [InlineData("timeout")]
    public async Task Real_failures_turn_red_and_successful_empty_result_clears_it(string failure)
    {
        var clock = new Clock(); var count = 0;
        using var service = new ResetScheduleClient(new FakeHandler((_, _) =>
        {
            if (++count > 1) return Task.FromResult(Json(Status("null", clock.Now)));
            return failure switch
            {
                "network" => throw new HttpRequestException("synthetic"),
                "timeout" => throw new OperationCanceledException("synthetic timeout"),
                "http" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)),
                _ => Task.FromResult(Json("not-json")),
            };
        }), clock);
        Assert.NotNull((await service.FetchAsync()).ErrorMessage);
        Assert.Equal(ResetQueryPhase.Failed, service.QueryState.Phase);
        clock.Now += ResetScheduleClient.PollInterval;
        Assert.Null((await service.FetchAsync()).ErrorMessage);
        Assert.Equal(ResetQueryPhase.Idle, service.QueryState.Phase);
    }

    [Fact]
    public async Task Revalidation_clears_failure_and_old_source_is_not_transport_failure()
    {
        var clock = new Clock(); var count = 0;
        using var service = new ResetScheduleClient(new FakeHandler((_, _) => Task.FromResult(++count switch
        {
            1 => Json(Status(Schedule(), Now.AddMinutes(-4))),
            2 => new HttpResponseMessage(HttpStatusCode.BadGateway),
            _ => new HttpResponseMessage(HttpStatusCode.NotModified),
        })), clock);
        var original = await service.FetchAsync();
        Assert.True(original.IsStale); Assert.Equal(ResetQueryPhase.Idle, service.QueryState.Phase);
        clock.Now += ResetScheduleClient.PollInterval;
        await service.FetchAsync(); Assert.Equal(ResetQueryPhase.Failed, service.QueryState.Phase);
        clock.Now += ResetScheduleClient.PollInterval;
        Assert.Equal(original.Schedule, (await service.FetchAsync()).Schedule);
        Assert.Equal(ResetQueryPhase.Idle, service.QueryState.Phase);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("304")]
    [InlineData("cancel")]
    [InlineData("dispose")]
    public async Task Unresolved_failure_survives_retry_and_cancellation_until_validated_success(string outcome)
    {
        var clock = new Clock(); var count = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new ResetScheduleClient(new FakeHandler(async (_, token) =>
        {
            if (++count == 1) return Json(Status(Schedule(), clock.Now));
            if (count == 2) return new HttpResponseMessage(HttpStatusCode.BadGateway);
            entered.SetResult(); await release.Task.WaitAsync(token);
            return outcome == "304" ? new HttpResponseMessage(HttpStatusCode.NotModified) : Json(Status("null", clock.Now));
        }), clock);
        await service.FetchAsync(); clock.Now += ResetScheduleClient.PollInterval;
        await service.FetchAsync(); clock.Now += ResetScheduleClient.PollInterval;
        Assert.True(service.QueryState.HasUnresolvedFailure);
        using var cancel = new CancellationTokenSource();
        var retry = service.FetchAsync(cancel.Token); await entered.Task;
        Assert.Equal(ResetQueryPhase.Checking, service.QueryState.Phase);
        Assert.True(service.QueryState.HasUnresolvedFailure);
        Assert.Equal(ResetVisualState.Failed, service.QueryState.Select(true, null, clock.Now));
        Assert.Equal(ResetVisualState.Confirmed, service.QueryState.Select(true, clock.Now.AddHours(1), clock.Now));
        if (outcome is "cancel" or "dispose")
        {
            if (outcome == "cancel") cancel.Cancel(); else service.Dispose();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry);
            Assert.Equal(ResetQueryPhase.Idle, service.QueryState.Phase);
            Assert.True(service.QueryState.HasUnresolvedFailure);
            Assert.Equal(ResetVisualState.Failed, service.QueryState.Select(false, null, clock.Now));
        }
        else
        {
            release.SetResult(); await retry;
            Assert.False(service.QueryState.HasUnresolvedFailure);
            Assert.Equal(ResetQueryPhase.Idle, service.QueryState.Phase);
            Assert.Equal(ResetVisualState.None, service.QueryState.Select(false, null, clock.Now));
            Assert.Equal(ResetVisualState.Upcoming, service.QueryState.Select(true, null, clock.Now));
        }
    }

    [Fact]
    public void Checking_is_not_a_visual_state_green_keeps_priority_and_success_never_invents_recovery()
    {
        var checking = new ResetQueryState(1, ResetQueryPhase.Checking, Now);
        Assert.Equal(ResetVisualState.Upcoming, checking.Select(true, null, Now.AddMilliseconds(299)));
        Assert.Equal(ResetVisualState.Upcoming, checking.Select(true, null, Now.AddMilliseconds(300)));
        Assert.Equal(ResetVisualState.None, checking.Select(false, null, Now.AddHours(1)));
        Assert.Equal(ResetVisualState.None, new ResetQueryState(2, ResetQueryPhase.Idle).Select(false, null, Now.AddMilliseconds(301)));
        var red = new ResetQueryState(3, ResetQueryPhase.Failed);
        Assert.Equal(ResetVisualState.Failed, red.Select(true, null, Now));
        foreach (var phase in Enum.GetValues<ResetQueryPhase>())
        {
            var state = new ResetQueryState(4, phase, Now.AddSeconds(-1));
            Assert.Equal(ResetVisualState.Confirmed, state.Select(true, Now.AddHours(1), Now));
            Assert.NotEqual(ResetVisualState.Confirmed, state.Select(false, Now, Now));
        }
    }

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
    public async Task Polling_keeps_latest_responses_while_display_latches_first_time_and_later_notice_rearms()
    {
        var clock = new Clock(); var requests = 0;
        using var service = new ResetScheduleClient(new FakeHandler((request, _) =>
        {
            var number = ++requests;
            if (number == 4)
            {
                Assert.Equal("\"latest-status\"", Assert.Single(request.Headers.IfNoneMatch).ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            }
            var body = number switch
            {
                1 => Status(Schedule(deadline: Now.AddHours(3)), clock.Now),
                2 => Status("null", clock.Now), // Site temporarily reports no scheduled reset.
                3 => Status(Schedule(deadline: Now.AddHours(7)), clock.Now), // Same publication changes time.
                5 => Status(Schedule(deadline: Now.AddHours(9), announced: clock.Now, id: "456"), clock.Now),
                _ => throw new InvalidOperationException("Unexpected mock request."),
            };
            var response = Json(body); response.Headers.ETag = new EntityTagHeaderValue("\"latest-status\"");
            return Task.FromResult(response);
        }), clock);
        var tracker = new QuotaResetTracker(); var scope = QuotaResetTracker.ScopeForSource("mock-running-context")!;
        QuotaSnapshot Quota(double remaining) => QuotaSnapshot.Available(
            [new(QuotaWindowKind.Primary, remaining, 300, Now.AddHours(12))], clock.Now);
        tracker.Observe(Quota(20), scope, clock.Now);
        var first = await service.FetchAsync(); tracker.SetSchedule(first, clock.Now);
        Assert.Equal(Now.AddHours(3), tracker.ProjectSchedule(first, clock.Now).Schedule!.ScheduledFor);

        clock.Now += ResetScheduleClient.PollInterval;
        var empty = await service.FetchAsync(); tracker.SetSchedule(empty, clock.Now);
        Assert.Null(empty.Schedule); Assert.Equal(clock.Now, empty.SourceGeneratedAt);
        Assert.Equal(empty, await service.FetchAsync()); // The client cache retains the LATEST response.
        Assert.Equal(first.Schedule, tracker.ProjectSchedule(empty, clock.Now).Schedule);

        clock.Now += ResetScheduleClient.PollInterval;
        var changed = await service.FetchAsync(); tracker.SetSchedule(changed, clock.Now);
        Assert.Equal(Now.AddHours(7), changed.Schedule!.ScheduledFor);
        Assert.Equal(clock.Now, changed.LastUpdatedAt); Assert.Equal(clock.Now, changed.SourceGeneratedAt);
        var display = tracker.ProjectSchedule(changed, clock.Now);
        Assert.Equal(first.Schedule, display.Schedule);
        Assert.Equal(first.LastUpdatedAt, display.LastUpdatedAt);
        Assert.Equal(first.SourceGeneratedAt, display.SourceGeneratedAt);
        Assert.Null(tracker.ActiveUntil(clock.Now)); // Neither site response is quota recovery evidence.

        clock.Now += ResetScheduleClient.PollInterval;
        var revalidated = await service.FetchAsync(); tracker.SetSchedule(revalidated, clock.Now);
        Assert.Equal(changed.Schedule, revalidated.Schedule);
        Assert.Equal(clock.Now, revalidated.LastUpdatedAt);
        Assert.Equal(changed.SourceGeneratedAt, revalidated.SourceGeneratedAt);
        Assert.Equal(first.Schedule, tracker.ProjectSchedule(revalidated, clock.Now).Schedule);

        clock.Now += TimeSpan.FromSeconds(1);
        var recoveredAt = clock.Now;
        Assert.Equal(recoveredAt.AddHours(1), tracker.Observe(Quota(21), scope, recoveredAt));
        Assert.Null(tracker.ProjectSchedule(revalidated, clock.Now).Schedule);

        clock.Now = Now.AddMinutes(4);
        var later = await service.FetchAsync(); tracker.SetSchedule(later, clock.Now);
        Assert.Equal(5, requests); Assert.Equal(later, await service.FetchAsync());
        Assert.Equal(5, requests); // Latching/consumption did not stop any due request.
        Assert.True(later.Schedule!.AnnouncedAt > recoveredAt);
        Assert.Equal(later.Schedule, tracker.ProjectSchedule(later, clock.Now).Schedule);
        Assert.Equal(recoveredAt.AddHours(1), tracker.ActiveUntil(clock.Now));
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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(ResetQueryPhase.Idle, service.QueryState.Phase);
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
    private static string Schedule(string? url = null, DateTimeOffset? deadline = default, object? source = null, bool noDeadline = false,
        DateTimeOffset? announced = null, string id = "123") => JsonSerializer.Serialize(new
    {
        id, status = "scheduled", reset_type = "regular", announced_at = (announced ?? Now.AddHours(-2)).ToString("O"),
        scheduled_for = noDeadline ? null : (deadline ?? Now.AddHours(1)).ToString("O"), text = "Public scheduled announcement.",
        source = source ?? new { type = "x_post", author = "thsottiaux", url = url ?? $"https://x.com/thsottiaux/status/{id}" },
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
    private sealed class GatedContent(string json) : HttpContent
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Entered.TrySetResult(); await Release.Task;
            await stream.WriteAsync(Encoding.UTF8.GetBytes(json));
        }
        // ReadJsonAsync checks headers before requesting the body.
        internal void SetJsonHeader() => Headers.ContentType = new MediaTypeHeaderValue("application/json");
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
