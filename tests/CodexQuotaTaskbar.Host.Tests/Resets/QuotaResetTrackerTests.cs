using System.IO;
using System.Text.Json;
using System.Reflection;
using CodexQuotaTaskbar.Core.Provider;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Host.Provider;
using CodexQuotaTaskbar.Host.Resets;

namespace CodexQuotaTaskbar.Host.Tests.Resets;

public sealed class QuotaResetTrackerTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-26T10:00:00Z");
    private static readonly string Scope = QuotaResetTracker.ScopeForSource("SYNTHETIC-BOUND-ACCOUNT")!;
    private const string Connection = "0123456789abcdef0123456789abcdef";
    private const string Account = """{"account":{"type":"chatgpt","email":"fixture@example.invalid","planType":"pro"}}""";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "CqtResetStatus-" + Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(directory, "reset-confirmations.json");
    private static ResetSchedule Notice(string id = "fixture", int hours = 3) =>
        new(id, ResetKind.Regular, Now.AddMinutes(-5), Now.AddHours(hours), CodexResetFeedClient.SiteUri);

    [Theory]
    [InlineData(20, 20.01)]
    [InlineData(20, 30)]
    [InlineData(99.5, 100)]
    public void Any_recovery_without_period_change_uses_detection_time(double before, double after)
    {
        var tracker = new QuotaResetTracker(new ResetConfirmationStore(StorePath));
        Assert.Null(tracker.Observe(Sample(Now, before), Scope, Now));
        var detected = Now.AddMinutes(3).AddSeconds(5);
        Assert.Equal(detected.AddHours(1), tracker.Observe(Sample(Now.AddMinutes(3), after), Scope, detected));
        var receipt = Assert.Single(new ResetConfirmationStore(StorePath).Load());
        Assert.Equal(detected, receipt.OccurredAt);
        Assert.Equal(detected.AddHours(1), receipt.ExpiresAt);
    }

    [Theory]
    [InlineData(false, -1, 2)]
    [InlineData(true, -1, 2)]
    [InlineData(false, 0, 0)]
    [InlineData(true, 0, 1)]
    [InlineData(false, 1, 0)]
    [InlineData(true, 1, 1)]
    public void Exact_expiry_and_priority_do_not_depend_on_a_new_poll(bool upcoming, int ticks, int expected)
    {
        var until = Now.AddHours(1);
        Assert.Equal((ResetVisualState)expected, QuotaResetTracker.Select(upcoming, until, until.AddTicks(ticks)));
    }

    [Theory]
    [InlineData("first")]
    [InlineData("same")]
    [InlineData("decrease")]
    [InlineData("missing-account")]
    [InlineData("switch-account")]
    [InlineData("stale")]
    [InlineData("unavailable")]
    [InlineData("cached")]
    [InlineData("future")]
    [InlineData("duplicate-time")]
    [InlineData("out-of-order")]
    [InlineData("long-gap")]
    [InlineData("duration")]
    [InlineData("limit")]
    [InlineData("unknown-kind")]
    [InlineData("duplicate-window")]
    [InlineData("empty")]
    [InlineData("interrupted")]
    public void Ambiguous_or_discontinuous_samples_do_not_create_recovery(string scenario)
    {
        var tracker = new QuotaResetTracker(new ResetConfirmationStore(StorePath));
        if (scenario != "first") tracker.Observe(Sample(Now, 20), Scope, Now);
        var time = Now.AddMinutes(3);
        var after = Sample(time, 40);
        string? scope = Scope;
        switch (scenario)
        {
            case "same": after = Sample(time, 20); break;
            case "decrease": after = Sample(time, 10); break;
            case "missing-account": scope = null; break;
            case "switch-account": scope = QuotaResetTracker.ScopeForSource("OTHER"); break;
            case "stale": after = QuotaSnapshot.Stale(after.Windows, time, "fixture"); break;
            case "unavailable": after = QuotaSnapshot.Unavailable("fixture"); break;
            case "cached": time = time.AddSeconds(31); break;
            case "future": time = time.AddTicks(-1); break;
            case "duplicate-time": after = Sample(Now, 40); break;
            case "out-of-order": after = Sample(Now.AddSeconds(-1), 40); break;
            case "long-gap": time = Now.AddMinutes(11); after = Sample(time, 40); break;
            case "duration": after = QuotaSnapshot.Available([new(QuotaWindowKind.Primary, 40, 60, Now.AddHours(3))], time); break;
            case "limit": after = QuotaSnapshot.Available([new(QuotaWindowKind.Primary, 40, 300, Now.AddHours(3), "codex_bengalfox")], time); break;
            case "unknown-kind": after = QuotaSnapshot.Available([new(QuotaWindowKind.Other, 40, 300, Now.AddHours(3))], time); break;
            case "duplicate-window": after = QuotaSnapshot.Available([after.Windows[0], after.Windows[0]], time); break;
            case "empty": after = QuotaSnapshot.Available([], time); break;
            case "interrupted": tracker.InvalidateSample(); break;
        }
        Assert.Null(tracker.Observe(after, scope, time));
        Assert.False(File.Exists(StorePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failure_and_missing_fields_break_continuity_and_recovery_first_sample_is_only_baseline(bool missing)
    {
        var tracker = new QuotaResetTracker();
        tracker.Observe(Sample(Now, 20), Scope, Now);
        tracker.Observe(missing ? QuotaSnapshot.Available([], Now.AddMinutes(1)) : QuotaSnapshot.Unavailable("fixture"), Scope, Now.AddMinutes(1));
        Assert.Null(tracker.Observe(Sample(Now.AddMinutes(2), 50), Scope, Now.AddMinutes(2)));
        Assert.Equal(Now.AddMinutes(63), tracker.Observe(Sample(Now.AddMinutes(3), 51), Scope, Now.AddMinutes(3)));
    }

    [Fact]
    public void Missing_or_changed_display_plan_is_not_identity()
    {
        var tracker = new QuotaResetTracker();
        tracker.Observe(Sample(Now, 20), Scope, Now);
        var after = QuotaSnapshot.Available(Sample(Now.AddMinutes(1), 21).Windows, Now.AddMinutes(1), "OTHER DISPLAY");
        Assert.Equal(Now.AddMinutes(61), tracker.Observe(after, Scope, Now.AddMinutes(1)));
        Assert.Null(QuotaResetTracker.ScopeForSource(null));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"account\":{\"type\":\"chatgpt\"}}")]
    [InlineData("{\"account\":{\"type\":\"chatgpt\",\"planType\":\"pro\",\"email\":\"a@example.invalid\"}}")]
    [InlineData("{\"account\":{\"type\":\"chatgpt\",\"planType\":\"unknown-plan\"}}")]
    [InlineData("{\"account\":{\"type\":\"chatgpt\",\"planType\":\"enterprise\",\"accountId\":\"LOCAL-B\"}}")]
    public void Missing_initialized_connection_never_establishes_scope(string account)
    {
        using var a = JsonDocument.Parse(account);
        using var limits = JsonDocument.Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":20,\"windowDurationMins\":300,\"resetsAt\":1793016000}}}");
        Assert.Null(CodexRateLimitProvider.RecoveryScopeFromRpc(a.RootElement, limits.RootElement, a.RootElement, null, null, 0, 0));
    }

    [Theory]
    [InlineData("missing-account")]
    [InlineData("null-email")]
    [InlineData("changed-email")]
    [InlineData("changed-mode")]
    [InlineData("malformed-email")]
    [InlineData("ended-connection")]
    [InlineData("new-connection")]
    [InlineData("generation")]
    [InlineData("wrong-id-type")]
    [InlineData("empty-id")]
    [InlineData("control-in-id")]
    public void Real_binding_helper_rejects_missing_or_interrupted_context(string scenario)
    {
        var before = Account;
        var after = Account;
        var limits = Limits(80);
        string? connectionAfter = Connection;
        long generation = 0;
        switch (scenario)
        {
            case "missing-account": before = "{}"; break;
            case "null-email": before = after = """{"account":{"type":"chatgpt","email":null}}"""; break;
            case "changed-email": after = Account.Replace("fixture@", "other@"); break;
            case "changed-mode": after = Account.Replace("chatgpt", "chatgptAuthTokens"); break;
            case "malformed-email": before = after = Account.Replace("fixture@example.invalid", "not-an-email"); break;
            case "ended-connection": connectionAfter = null; break;
            case "new-connection": connectionAfter = new string('a', 32); break;
            case "generation": generation = 1; break;
            case "wrong-id-type": limits = """{"accountId":123}"""; break;
            case "empty-id": limits = """{"accountId":""}"""; break;
            case "control-in-id": limits = """{"accountId":"fixture\nother"}"""; break;
        }
        Assert.Null(Bind(limits, before, after, connectionAfter: connectionAfter, generationAfter: generation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Provider_mapping_detects_raw_used_percent_drop_with_known_and_unknown_buckets(bool responseId)
    {
        var tracker = new QuotaResetTracker();
        var before = Limits(80, responseId, unknownUsed: 10);
        var after = Limits(79.99, responseId, unknownUsed: 0);
        var scope = Bind(before);
        Assert.True(QuotaResetTracker.IsDigest(scope));
        Assert.Equal(scope, Bind(after, after: Account.Replace("pro", "unknown-plan")));
        tracker.SetNotice(Notice());
        Assert.Null(tracker.Observe(AppServerRateLimitsParser.Parse(before, Now), scope, Now));
        Assert.Equal(Now.AddMinutes(61), tracker.Observe(AppServerRateLimitsParser.Parse(after, Now.AddMinutes(1)), Bind(after), Now.AddMinutes(1)));
        Assert.True(tracker.IsNoticeConsumed(Notice()));
        Assert.Equal(ResetVisualState.Confirmed, QuotaResetTracker.Select(true, tracker.ActiveUntil(Now.AddMinutes(1)), Now.AddMinutes(1)));
    }

    [Fact]
    public void Unknown_bucket_recovery_alone_never_creates_green()
    {
        var tracker = new QuotaResetTracker();
        tracker.Observe(AppServerRateLimitsParser.Parse(Limits(80, unknownUsed: 80), Now), Scope, Now);
        Assert.Null(tracker.Observe(AppServerRateLimitsParser.Parse(Limits(80, unknownUsed: 10), Now.AddMinutes(1)), Scope, Now.AddMinutes(1)));
    }

    [Fact]
    public void Shorter_next_reset_deadline_does_not_block_valid_percent_recovery()
    {
        var tracker = new QuotaResetTracker();
        tracker.Observe(Sample(Now, 20), Scope, Now);
        var sample = QuotaSnapshot.Available([new(QuotaWindowKind.Primary, 21, 300, Now.AddHours(1))], Now.AddMinutes(1));
        Assert.Equal(Now.AddMinutes(61), tracker.Observe(sample, Scope, Now.AddMinutes(1)));
    }

    [Fact]
    public void Response_id_scope_survives_transport_but_account_change_does_not()
    {
        var limits = Limits(80, responseId: true);
        Assert.Equal(Bind(limits), Bind(limits, connectionBefore: new string('a', 32), connectionAfter: new string('a', 32), generationBefore: 2, generationAfter: 2));
        Assert.NotEqual(Bind(limits), Bind(limits.Replace("fixture-account-A", "fixture-account-B")));
        var noEmail = """{"account":{"type":"chatgpt"}}""";
        Assert.NotNull(Bind(limits, noEmail, noEmail));
        Assert.Null(Bind(Limits(80), noEmail, noEmail));
        Assert.NotEqual(Bind(limits), Bind(Limits(80)));
    }

    [Fact]
    public void Fallback_generation_change_and_absent_id_rebuild_baseline_without_display_plan_identity()
    {
        var limits = Limits(80);
        Assert.Equal(Bind(limits), Bind(limits.Replace("\"accountId\":null,", "")));
        var changedPlan = Account.Replace("\"planType\":\"pro\"", "\"planType\":null");
        Assert.Equal(Bind(limits), Bind(limits, before: changedPlan, after: changedPlan));
        var tracker = new QuotaResetTracker();
        tracker.Observe(Sample(Now, 20), Bind(limits), Now);
        var nextScope = Bind(limits, generationBefore: 1, generationAfter: 1);
        Assert.NotEqual(Bind(limits), nextScope);
        Assert.Null(tracker.Observe(Sample(Now.AddMinutes(1), 40), nextScope, Now.AddMinutes(1)));
        Assert.Equal(Now.AddMinutes(62), tracker.Observe(Sample(Now.AddMinutes(2), 41), nextScope, Now.AddMinutes(2)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reinitialized_continuous_source_never_revives_or_renews_old_green_but_notice_stays_consumed(bool afterExpiry)
    {
        var store = new ResetConfirmationStore(StorePath);
        var tracker = new QuotaResetTracker(store);
        var scope = Bind(Limits(80));
        tracker.SetNotice(Notice());
        tracker.Observe(Sample(Now, 20), scope, Now);
        tracker.Observe(Sample(Now.AddMinutes(1), 30), scope, Now.AddMinutes(1));
        var saved = File.ReadAllBytes(StorePath);
        var next = new QuotaResetTracker(store);
        var time = Now.AddMinutes(afterExpiry ? 65 : 5);
        var newScope = Bind(Limits(70), connectionBefore: new string('a', 32), connectionAfter: new string('a', 32));
        Assert.NotEqual(scope, newScope);
        Assert.True(next.IsNoticeConsumed(Notice()));
        Assert.True(next.IsNoticeConsumed(Notice("late-old")));
        Assert.False(next.IsNoticeConsumed(Notice("genuinely-new") with { AnnouncedAt = Now.AddMinutes(2) }));
        Assert.Null(next.Observe(Sample(time, 40), newScope, time));
        Assert.Null(next.Observe(Sample(time.AddMinutes(1), 40), newScope, time.AddMinutes(1)));
        Assert.Equal(saved, File.ReadAllBytes(StorePath));
        Assert.Equal(time.AddMinutes(62), next.Observe(Sample(time.AddMinutes(2), 41), newScope, time.AddMinutes(2)));
        Assert.Equal(Now.AddMinutes(61), store.Load().Single(e => e.ScopeId == scope).ExpiresAt);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Actual_refresh_pipeline_brackets_quota_and_handles_failure_recovery_without_process_or_credentials(bool responseId, bool nullableUnknown)
    {
        var tracker = new QuotaResetTracker();
        tracker.SetNotice(Notice());
        await using var provider = new CodexRateLimitProvider(false, resetTracker: tracker);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var client = (CodexAppServerClient)typeof(CodexRateLimitProvider).GetField("client", flags)!.GetValue(provider)!;
        var dispatch = typeof(CodexAppServerClient).GetMethod("Dispatch", flags)!;
        var methods = new List<string>();
        var used = 80d;
        var fail = false;
        var writer = new RpcWriter(request =>
        {
            var method = request.GetProperty("method").GetString()!;
            methods.Add(method);
            if (method == "account/rateLimits/read" && fail) throw new IOException("Synthetic interrupted quota request");
            if (method == "account/read") Assert.False(request.GetProperty("params").GetProperty("refreshToken").GetBoolean());
            var result = method == "account/read" ? Account : Limits(used, responseId, nullableUnknown: nullableUnknown);
            using var response = JsonDocument.Parse($"{{\"id\":{request.GetProperty("id").GetInt64()},\"result\":{result}}}");
            dispatch.Invoke(client, [response.RootElement]);
        });
        typeof(CodexAppServerClient).GetField("input", flags)!.SetValue(client, writer);
        typeof(CodexAppServerClient).GetField("initializedConnectionId", flags)!.SetValue(client, Connection);
        typeof(CodexRateLimitProvider).GetField("started", flags)!.SetValue(provider, true);
        DateTimeOffset? published = null;
        var events = 0;
        provider.ResetConfirmationChanged += until => { published = until; events++; };
        await provider.RefreshAsync(CancellationToken.None);
        Assert.Null(published);
        Assert.Equal(new[] { "account/read", "account/rateLimits/read", "account/read" }, methods);
        used = 70;
        await provider.RefreshAsync(CancellationToken.None);
        Assert.NotNull(published);
        var original = published;
        Assert.InRange(original!.Value - DateTimeOffset.Now, TimeSpan.FromMinutes(59), TimeSpan.FromHours(1));
        Assert.Equal(30, provider.Current.Windows.First(w => w.LimitId == "codex").RemainingPercent);
        Assert.True(tracker.IsNoticeConsumed(Notice()));
        await provider.RefreshAsync(CancellationToken.None);
        Assert.Equal(1, events);
        fail = true;
        await provider.RefreshAsync(CancellationToken.None);
        Assert.Equal(QuotaAvailability.Stale, provider.Current.Availability);
        fail = false;
        used = 50;
        await provider.RefreshAsync(CancellationToken.None);
        Assert.Equal(original, published); // First success after failure is only baseline.
        Assert.Equal(1, events);
        Assert.Null(typeof(CodexAppServerClient).GetField("process", flags)!.GetValue(client));
    }

    [Theory]
    [InlineData("account/updated")]
    [InlineData("account/login/completed")]
    public async Task Authentication_notifications_invalidate_baseline_and_generation(string method)
    {
        var tracker = new QuotaResetTracker();
        tracker.Observe(Sample(Now, 20), Scope, Now);
        await using var provider = new CodexRateLimitProvider(false, resetTracker: tracker);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(CodexRateLimitProvider).GetMethod("OnNotification", flags)!.Invoke(provider, [null, method]);
        Assert.Equal(1L, typeof(CodexRateLimitProvider).GetField("accountGeneration", flags)!.GetValue(provider));
        Assert.Null(tracker.Observe(Sample(Now.AddMinutes(1), 40), Scope, Now.AddMinutes(1)));
    }

    [Fact]
    public async Task Adaptive_refresh_clears_explicit_empty_preserves_failure_cache_and_isolates_account_switch()
    {
        await using var provider = new CodexRateLimitProvider(false, resetTracker: new QuotaResetTracker());
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var client = (CodexAppServerClient)typeof(CodexRateLimitProvider).GetField("client", flags)!.GetValue(provider)!;
        var dispatch = typeof(CodexAppServerClient).GetMethod("Dispatch", flags)!;
        var account = Account;
        var result = """{"rateLimits":{"primary":null,"secondary":{"usedPercent":59,"windowDurationMins":10080,"resetsAt":1793016000}}}""";
        var fail = false;
        var writer = new RpcWriter(request =>
        {
            var isAccount = request.GetProperty("method").GetString() == "account/read";
            if (!isAccount && fail) throw new IOException("offline test failure");
            using var response = JsonDocument.Parse($"{{\"id\":{request.GetProperty("id").GetInt64()},\"result\":{(isAccount ? account : result)}}}");
            dispatch.Invoke(client, [response.RootElement]);
        });
        typeof(CodexAppServerClient).GetField("input", flags)!.SetValue(client, writer);
        typeof(CodexAppServerClient).GetField("initializedConnectionId", flags)!.SetValue(client, Connection);
        typeof(CodexRateLimitProvider).GetField("started", flags)!.SetValue(provider, true);
        await provider.RefreshAsync(CancellationToken.None); Assert.Single(provider.Current.Windows);
        fail = true; await provider.RefreshAsync(CancellationToken.None);
        Assert.Equal(QuotaAvailability.Stale, provider.Current.Availability); Assert.Single(provider.Current.Windows);
        // An account change observed before a failed quota read must not borrow A's cache.
        account = Account.Replace("fixture@", "other@"); await provider.RefreshAsync(CancellationToken.None);
        Assert.Equal(QuotaAvailability.Unavailable, provider.Current.Availability); Assert.Empty(provider.Current.Windows);
        fail = false; await provider.RefreshAsync(CancellationToken.None); Assert.Single(provider.Current.Windows);
        result = """{"rateLimits":{"primary":null,"secondary":null}}""";
        await provider.RefreshAsync(CancellationToken.None);
        Assert.Equal(QuotaAvailability.Available, provider.Current.Availability); Assert.Empty(provider.Current.Windows);
        Assert.Empty(provider.Current.UnresolvedWindows);
        result = """{"rateLimits":{"secondary":null}}""";
        await provider.RefreshAsync(CancellationToken.None); Assert.Single(provider.Current.UnresolvedWindows);
        Assert.Null(typeof(CodexAppServerClient).GetField("process", flags)!.GetValue(client));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Changed_response_scope_clears_old_green_even_if_new_quota_is_malformed(bool missingId)
    {
        await using var provider = new CodexRateLimitProvider(false, resetTracker: new QuotaResetTracker());
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var client = (CodexAppServerClient)typeof(CodexRateLimitProvider).GetField("client", flags)!.GetValue(provider)!;
        var dispatch = typeof(CodexAppServerClient).GetMethod("Dispatch", flags)!;
        var result = Limits(80, responseId: true);
        var writer = new RpcWriter(request =>
        {
            var data = request.GetProperty("method").GetString() == "account/read" ? Account : result;
            using var response = JsonDocument.Parse($"{{\"id\":{request.GetProperty("id").GetInt64()},\"result\":{data}}}");
            dispatch.Invoke(client, [response.RootElement]);
        });
        typeof(CodexAppServerClient).GetField("input", flags)!.SetValue(client, writer);
        typeof(CodexAppServerClient).GetField("initializedConnectionId", flags)!.SetValue(client, Connection);
        typeof(CodexRateLimitProvider).GetField("started", flags)!.SetValue(provider, true);
        DateTimeOffset? green = null; provider.ResetConfirmationChanged += value => green = value;
        await provider.RefreshAsync(CancellationToken.None);
        result = Limits(70, responseId: true); await provider.RefreshAsync(CancellationToken.None); Assert.NotNull(green);
        result = "{\"accountId\":" + (missingId ? "null" : "\"fixture-account-B\"") + ",\"rateLimits\":{\"primary\":42}}";
        await provider.RefreshAsync(CancellationToken.None);
        Assert.Null(green); Assert.Empty(provider.Current.Windows); Assert.Equal(QuotaAvailability.Unavailable, provider.Current.Availability);
        result = Limits(50, responseId: !missingId).Replace("fixture-account-A", "fixture-account-B");
        await provider.RefreshAsync(CancellationToken.None); Assert.Null(green); // New scope's first sample is baseline only.
    }

    private static string Limits(double used, bool responseId = false, double unknownUsed = 42, bool nullableUnknown = false) => JsonSerializer.Serialize(new
    {
        accountId = responseId ? "fixture-account-A" : null,
        rateLimitsByLimitId = new Dictionary<string, object>
        {
            ["codex"] = new { limitId = "codex", primary = new { usedPercent = used, windowDurationMins = 300, resetsAt = 1793016000L } },
            ["future-meter"] = new { limitId = "future-meter", primary = new { usedPercent = unknownUsed, windowDurationMins = nullableUnknown ? (int?)null : 60, resetsAt = nullableUnknown ? (long?)null : 1793016000L } },
        },
    });

    private static string? Bind(string limits, string before = Account, string after = Account,
        string? connectionBefore = Connection, string? connectionAfter = Connection, long generationBefore = 0, long generationAfter = 0)
    {
        using var a = JsonDocument.Parse(before);
        using var b = JsonDocument.Parse(after);
        using var quota = JsonDocument.Parse(limits);
        return CodexRateLimitProvider.RecoveryScopeFromRpc(a.RootElement, quota.RootElement, b.RootElement,
            connectionBefore, connectionAfter, generationBefore, generationAfter);
    }

    private sealed class RpcWriter(Action<JsonElement> reply) : StreamWriter(Stream.Null)
    {
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            using var request = JsonDocument.Parse(buffer.ToString());
            reply(request.RootElement);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Polls_metadata_replay_and_restart_do_not_extend_fixed_deadline()
    {
        var store = new ResetConfirmationStore(StorePath);
        var tracker = new QuotaResetTracker(store);
        tracker.Observe(Sample(Now, 20), Scope, Now);
        var time = Now.AddMinutes(3);
        tracker.Observe(Sample(time, 30), Scope, time);
        var saved = File.ReadAllBytes(StorePath);
        for (var i = 4; i < 63; i++)
            Assert.Equal(time.AddHours(1), tracker.Observe(Sample(Now.AddMinutes(i), 30), Scope, Now.AddMinutes(i)));
        Assert.Equal(saved, File.ReadAllBytes(StorePath));
        var restarted = new QuotaResetTracker(store);
        Assert.Null(restarted.ActiveUntil(Now.AddMinutes(20)));
        Assert.Equal(time.AddHours(1), restarted.Observe(Sample(Now.AddMinutes(20), 25), Scope, Now.AddMinutes(20)));
        // Old source samples cannot establish a lower baseline and then replay the rise.
        restarted.Observe(Sample(Now, 20), Scope, Now.AddMinutes(20));
        Assert.Equal(time.AddHours(1), restarted.Observe(Sample(time, 30), Scope, time));
        Assert.Null(restarted.ActiveUntil(time.AddHours(1)));
        Assert.Single(store.Load());
    }

    [Fact]
    public void Truly_new_recovery_after_spend_is_a_new_event_even_in_same_period()
    {
        var store = new ResetConfirmationStore(StorePath);
        var tracker = new QuotaResetTracker(store);
        tracker.Observe(Sample(Now, 20), Scope, Now);
        tracker.Observe(Sample(Now.AddMinutes(1), 30), Scope, Now.AddMinutes(1));
        tracker.Observe(Sample(Now.AddMinutes(2), 25), Scope, Now.AddMinutes(2));
        Assert.Equal(Now.AddMinutes(63), tracker.Observe(Sample(Now.AddMinutes(3), 30), Scope, Now.AddMinutes(3)));
        Assert.Equal(2, store.Load().Count);
        Assert.Equal(2, store.Load().Select(e => e.EventId).Distinct().Count());
    }

    [Fact]
    public void Multiwindow_recoveries_share_detection_time_and_latest_event_wins()
    {
        var store = new ResetConfirmationStore(StorePath);
        var tracker = new QuotaResetTracker(store);
        QuotaSnapshot Pair(int minute, double a, double b) => QuotaSnapshot.Available([
            new(QuotaWindowKind.Primary, a, 300, Now.AddHours(3)),
            new(QuotaWindowKind.Secondary, b, 10080, Now.AddDays(3), "codex_bengalfox")], Now.AddMinutes(minute));
        tracker.Observe(Pair(0, 20, 10), Scope, Now);
        Assert.Equal(Now.AddMinutes(61), tracker.Observe(Pair(1, 30, 11), Scope, Now.AddMinutes(1)));
        Assert.Equal(2, store.Load().Count);
        Assert.Equal(Now.AddMinutes(62), tracker.Observe(Pair(2, 30, 12), Scope, Now.AddMinutes(2)));
        Assert.Equal(3, store.Load().Count);
    }

    [Fact]
    public void Recovery_consumes_old_notice_atomically_and_304_or_restart_never_reopens_it()
    {
        var store = new ResetConfirmationStore(StorePath);
        var tracker = new QuotaResetTracker(store);
        tracker.SetNotice(Notice());
        tracker.Observe(Sample(Now, 20), Scope, Now);
        tracker.Observe(Sample(Now.AddMinutes(1), 21), Scope, Now.AddMinutes(1));
        Assert.True(tracker.IsNoticeConsumed(Notice()));
        Assert.Null(tracker.ActiveUntil(Now.AddHours(2)));
        var restarted = new QuotaResetTracker(store);
        restarted.Observe(Sample(Now.AddHours(2), 15), Scope, Now.AddHours(2));
        restarted.SetNotice(Notice());
        Assert.True(restarted.IsNoticeConsumed(Notice()));
        Assert.True(restarted.IsNoticeConsumed(Notice("late-old-notice")));
        Assert.False(restarted.IsNoticeConsumed(Notice("new-notice") with { AnnouncedAt = Now.AddMinutes(2) }));
        Assert.True(restarted.IsNoticeConsumed(Notice(hours: 4)));
        var a = new ResetScheduleSnapshot(Notice(), Now, false, null);
        var b = a with { LastUpdatedAt = Now.AddMinutes(15), SourceGeneratedAt = Now.AddMinutes(15) };
        Assert.Equal(QuotaResetTracker.NoticeId(a.Schedule), QuotaResetTracker.NoticeId(b.Schedule));
    }

    [Fact]
    public void Stored_state_is_only_digests_times_and_save_failure_does_not_publish_or_consume()
    {
        var store = new ResetConfirmationStore(StorePath);
        var tracker = new QuotaResetTracker(store);
        tracker.SetNotice(Notice());
        tracker.Observe(Sample(Now, 20), Scope, Now);
        tracker.Observe(Sample(Now.AddMinutes(1), 21), Scope, Now.AddMinutes(1));
        var json = File.ReadAllText(StorePath);
        Assert.DoesNotContain("SYNTHETIC", json);
        Assert.DoesNotContain("Remaining", json);
        Assert.DoesNotContain("fixture", json);
        Assert.DoesNotContain("http", json);
        foreach (var bad in new[] { "{", "null", "{\"Schema\":1,\"Events\":[]}", "{\"Schema\":2,\"Events\":[null],\"Notices\":[]}", new string(' ', 131073) })
        {
            File.WriteAllText(StorePath, bad);
            Assert.Empty(store.Load()); Assert.Empty(store.LoadConsumedNotices());
        }
        var blocked = new QuotaResetTracker(new ResetConfirmationStore(directory));
        blocked.SetNotice(Notice());
        blocked.Observe(Sample(Now, 20), Scope, Now);
        Assert.Null(blocked.Observe(Sample(Now.AddMinutes(1), 21), Scope, Now.AddMinutes(1)));
        Assert.False(blocked.IsNoticeConsumed(Notice()));
        Assert.Empty(Directory.GetFiles(directory, ".reset-confirmations-*.tmp"));
    }

    [Fact]
    public void Offline_keeps_existing_deadline_but_account_change_hides_it()
    {
        var tracker = new QuotaResetTracker();
        tracker.Observe(Sample(Now, 20), Scope, Now);
        tracker.Observe(Sample(Now.AddMinutes(1), 21), Scope, Now.AddMinutes(1));
        tracker.InvalidateSample();
        Assert.Equal(Now.AddMinutes(61), tracker.ActiveUntil(Now.AddMinutes(30)));
        tracker.InvalidateSample(accountChanged: true);
        Assert.Null(tracker.ActiveUntil(Now.AddMinutes(30)));
    }

    [Fact]
    public async Task Normal_eof_invalidates_generation_and_rebuilds_baseline_without_extending_old_receipt()
    {
        var tracker = new QuotaResetTracker();
        tracker.Observe(Sample(Now, 20), Scope, Now);
        tracker.Observe(Sample(Now.AddMinutes(1), 21), Scope, Now.AddMinutes(1));
        await using var provider = new CodexRateLimitProvider(false, resetTracker: tracker);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var client = (CodexAppServerClient)typeof(CodexRateLimitProvider).GetField("client", flags)!.GetValue(provider)!;
        var generation = typeof(CodexRateLimitProvider).GetField("accountGeneration", flags)!;
        Assert.Null(client.InitializedConnectionId);
        typeof(CodexAppServerClient).GetField("initializedConnectionId", flags)!.SetValue(client, Connection);
        Assert.Equal(Connection, client.InitializedConnectionId);
        using var stream = new MemoryStream(Array.Empty<byte>());
        using var reader = new StreamReader(stream);
        await client.ReadLoopAsync(reader, CancellationToken.None);
        Assert.Null(client.InitializedConnectionId);
        Assert.Equal(1L, generation.GetValue(provider));
        Assert.Null(tracker.ActiveUntil(Now.AddMinutes(2)));
        Assert.Equal(Now.AddMinutes(61), tracker.Observe(Sample(Now.AddMinutes(2), 40), Scope, Now.AddMinutes(2)));
        // No process was started and no credential method invoked.
        Assert.Null(typeof(CodexAppServerClient).GetField("process", flags)!.GetValue(client));
    }

    [Theory]
    [InlineData(false, 5)]
    [InlineData(true, 5)]
    [InlineData(true, 65)]
    public void Old_notice_arriving_after_recovery_is_consumed_even_after_restart_and_expiry(bool restart, int arrivalMinute)
    {
        var store = new ResetConfirmationStore(StorePath);
        var tracker = new QuotaResetTracker(store);
        tracker.Observe(Sample(Now.AddMinutes(-1), 20), Scope, Now.AddMinutes(-1));
        tracker.Observe(Sample(Now, 21), Scope, Now);
        if (restart) tracker = new QuotaResetTracker(store);
        var arrival = Now.AddMinutes(arrivalMinute);
        tracker.Observe(Sample(arrival, 20), Scope, arrival);
        tracker.SetNotice(Notice());
        Assert.True(tracker.IsNoticeConsumed(Notice()));
        Assert.True(tracker.IsNoticeConsumed(Notice(hours: 5)));
        Assert.False(tracker.IsNoticeConsumed(Notice("new") with { AnnouncedAt = Now.AddMinutes(1) }));
        Assert.Equal(arrivalMinute < 60 ? Now.AddHours(1) : (DateTimeOffset?)null, tracker.ActiveUntil(arrival));
    }

    [Fact]
    public void Notice_capacity_evicts_old_ids_but_publication_cutoff_still_suppresses_old_notices()
    {
        var store = new ResetConfirmationStore(StorePath);
        var tracker = new QuotaResetTracker(store);
        tracker.Observe(Sample(Now, 20), Scope, Now);
        for (var i = 1; i <= 258; i++)
        {
            var time = Now.AddSeconds(i);
            tracker.SetNotice(Notice("notice-" + i) with { AnnouncedAt = time });
            Assert.Equal(time.AddHours(1), tracker.Observe(Sample(time, 20 + i * .01), Scope, time));
        }
        Assert.Equal(256, store.LoadConsumedNotices().Count);
        Assert.Equal(32, store.Load().Count);
        Assert.True(tracker.IsNoticeConsumed(Notice("notice-1")));
    }

    [Fact]
    public void Transient_save_failure_retries_original_event_time_without_renewal()
    {
        Directory.CreateDirectory(StorePath); // Isolated path deliberately blocks file replacement.
        var store = new ResetConfirmationStore(StorePath);
        var tracker = new QuotaResetTracker(store);
        tracker.SetNotice(Notice());
        tracker.Observe(Sample(Now, 20), Scope, Now);
        Assert.Null(tracker.Observe(Sample(Now.AddMinutes(1), 21), Scope, Now.AddMinutes(1)));
        Assert.False(tracker.IsNoticeConsumed(Notice()));
        Directory.Delete(StorePath); // This exact, empty fixture directory only.
        Assert.Equal(Now.AddMinutes(61), tracker.Observe(Sample(Now.AddMinutes(4), 21), Scope, Now.AddMinutes(4)));
        Assert.Equal(Now.AddMinutes(1), Assert.Single(store.Load()).OccurredAt);
        Assert.True(tracker.IsNoticeConsumed(Notice()));
    }

    [Fact]
    public void Locked_receipt_temporarily_keeps_previous_deadline_then_retries_the_same_sample()
    {
        var store = new ResetConfirmationStore(StorePath);
        var tracker = new QuotaResetTracker(store);
        tracker.Observe(Sample(Now, 20), Scope, Now);
        var previous = Now.AddSeconds(100);
        var next = previous.AddSeconds(1);
        Assert.Equal(previous.AddHours(1), tracker.Observe(Sample(previous, 21), Scope, previous));
        using (var locked = new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(previous.AddHours(1), tracker.Observe(Sample(next, 21.01), Scope, next));
        // Exactly the one-second lag seen in the capacity test is an expected
        // save-failure state. Retrying must persist the original timestamp.
        Assert.Equal(next.AddHours(1), tracker.Observe(Sample(next, 21.01), Scope, next.AddSeconds(10)));
        Assert.Equal(next, store.Load().Max(item => item.OccurredAt));
    }

    private static QuotaSnapshot Sample(DateTimeOffset time, double remaining) =>
        QuotaSnapshot.Available([new(QuotaWindowKind.Primary, remaining, 300, Now.AddHours(3))], time);

    public void Dispose()
    {
        var root = Path.GetFullPath(directory);
        if (!root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(root).StartsWith("CqtResetStatus-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unsafe fixture cleanup.");
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
