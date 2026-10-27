using System.IO;
using System.Text.Json;
using CodexQuotaTaskbar.Core.Provider;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Host.Resets;

namespace CodexQuotaTaskbar.Host.Provider;

internal sealed class CodexRateLimitProvider : IQuotaProvider
{
    private readonly CodexAppServerClient client = new();
    private readonly CodexSubscriptionMetadataService subscriptionMetadataService;
    private readonly QuotaResetTracker resetTracker;
    private readonly object resetGate = new();
    private DateTimeOffset? publishedResetUntil;
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private readonly SemaphoreSlim sessionsRefreshLock = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly RolloutSessionMonitor sessionMonitor = new(DateTimeOffset.UtcNow);
    private Task? periodicRefreshTask;
    private Task? periodicSessionsRefreshTask;
    private long accountGeneration;
    private string? quotaScope;
    private (string Type, string? Email)? quotaAccountContext;
    private int subscriptionDetailsEnabled;
    private bool started;
    private bool sessionsStarted;

    internal static TimeSpan AutomaticRefreshInterval { get; } = TimeSpan.FromMinutes(3);
    internal static TimeSpan SessionsRefreshInterval { get; } = TimeSpan.FromSeconds(2);

    internal CodexRateLimitProvider(
        bool subscriptionDetailsEnabled = true,
        CodexSubscriptionMetadataService? subscriptionMetadataService = null,
        QuotaResetTracker? resetTracker = null)
    {
        this.subscriptionDetailsEnabled = subscriptionDetailsEnabled ? 1 : 0;
        this.subscriptionMetadataService = subscriptionMetadataService ?? new CodexSubscriptionMetadataService();
        this.resetTracker = resetTracker ?? new QuotaResetTracker();
        client.Notification += OnNotification;
        client.ConnectionLost += OnConnectionLost;
    }

    public event EventHandler<QuotaSnapshot>? SnapshotChanged;
    public event EventHandler<CodexSessionsSnapshot>? SessionsChanged;
    internal event Action<DateTimeOffset?>? ResetConfirmationChanged;
    public QuotaSnapshot Current { get; private set; } = QuotaSnapshot.Unavailable("正在连接 Codex…");
    public CodexSessionsSnapshot CurrentSessions { get; private set; } = CodexSessionsSnapshot.Empty;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await client.StartAsync(cancellationToken);
            started = true;
            await RefreshAsync(cancellationToken);
            periodicRefreshTask = RefreshPeriodicallyAsync(lifetime.Token);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            Set(QuotaSnapshot.Unavailable("未找到可用的 Codex App Server"));
        }

        sessionsStarted = true;
        await RefreshSessionsAsync(cancellationToken);
        periodicSessionsRefreshTask = RefreshSessionsPeriodicallyAsync(lifetime.Token);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!started)
        {
            return;
        }

        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            var generation = Interlocked.Read(ref accountGeneration);
            var connection = client.InitializedConnectionId;
            var accountResult = await client.RequestAsync("account/read", new { refreshToken = false }, cancellationToken);
            if (!TryValidateChatGptAccount(accountResult, out var accountMessage, out var subscriptionPlan))
            {
                lock (resetGate)
                {
                    if (generation == Interlocked.Read(ref accountGeneration))
                    {
                        resetTracker.InvalidateSample(accountChanged: true);
                        PublishResetConfirmation(null);
                    }
                }
                Set(QuotaSnapshot.Unavailable(accountMessage));
                return;
            }

            var nextContext = AccountContext(accountResult);
            if (nextContext != quotaAccountContext)
            {
                quotaAccountContext = nextContext;
                quotaScope = null;
                resetTracker.InvalidateSample(accountChanged: true);
                PublishResetConfirmation(null);
                Set(QuotaSnapshot.Unavailable("账号已变化，正在刷新…"));
            }
            var result = await client.RequestAsync("account/rateLimits/read", new { }, cancellationToken);
            var capturedAt = DateTimeOffset.Now;
            // Bracket the quota read with supported non-secret account context. No local
            // credential-file join; one extra read per quota refresh, not per session poll.
            var accountAfter = await client.RequestAsync("account/read", new { refreshToken = false }, cancellationToken);
            if (generation != Interlocked.Read(ref accountGeneration))
            {
                return;
            }

            var scope = RecoveryScopeFromRpc(accountResult, result, accountAfter,
                connection, client.InitializedConnectionId, generation, Interlocked.Read(ref accountGeneration));
            if (!IsStableAccountContext(accountResult, accountAfter))
            {
                quotaScope = null;
                resetTracker.InvalidateSample(accountChanged: true);
                PublishResetConfirmation(null);
                Set(QuotaSnapshot.Unavailable("账号上下文发生变化，等待重新读取…"));
                return;
            }
            if (scope != quotaScope)
            {
                lock (resetGate)
                {
                    if (generation != Interlocked.Read(ref accountGeneration)) return;
                    quotaScope = scope;
                    resetTracker.InvalidateSample(accountChanged: true);
                    PublishResetConfirmation(null);
                    Set(QuotaSnapshot.Unavailable("额度来源已变化，正在刷新…"));
                }
            }
            var baseSnapshot = AppServerRateLimitsParser.Parse(result.GetRawText(), capturedAt, subscriptionPlan);
            lock (resetGate)
            {
                if (generation != Interlocked.Read(ref accountGeneration)) return;
                PublishResetConfirmation(resetTracker.Observe(baseSnapshot, scope, DateTimeOffset.Now));
            }
            var previousExpiration = Current.SubscriptionExpiration;
            if (Volatile.Read(ref subscriptionDetailsEnabled) == 0)
            {
                SetForGeneration(baseSnapshot.WithSubscriptionExpiration(SubscriptionExpirationSnapshot.Disabled()), generation);
                return;
            }

            var pendingExpiration = previousExpiration.Availability == SubscriptionExpirationAvailability.Available
                ? previousExpiration.AsStale()
                : SubscriptionExpirationSnapshot.Unavailable(
                    DateTimeOffset.Now,
                    SubscriptionExpirationSource.CodexLogin,
                    "正在读取…");
            SetForGeneration(baseSnapshot.WithSubscriptionExpiration(pendingExpiration), generation);

            var expiration = await subscriptionMetadataService.ReadAsync(cancellationToken);
            if (generation != Interlocked.Read(ref accountGeneration))
            {
                return;
            }
            if (Volatile.Read(ref subscriptionDetailsEnabled) == 0)
            {
                SetForGeneration(baseSnapshot.WithSubscriptionExpiration(SubscriptionExpirationSnapshot.Disabled()), generation);
                return;
            }
            expiration = ResolveSubscriptionRefresh(expiration, previousExpiration);
            SetForGeneration(baseSnapshot.WithSubscriptionExpiration(expiration), generation);
        }
        catch (OperationCanceledException)
        {
            resetTracker.InvalidateSample();
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException or AppServerProtocolException or JsonException)
        {
            resetTracker.InvalidateSample(); // An interruption breaks evidence continuity, not an already fixed expiry.
            Set(quotaScope is not null && Current.Windows.Count > 0 && Current.CapturedAt is { } captured
                ? QuotaSnapshot.Stale(
                    Current.Windows,
                    captured,
                    "连接中断，显示上次额度",
                    Current.SubscriptionPlan,
                    Current.SubscriptionExpiration,
                    Current.ExtraCredits) with { UnresolvedWindows = Current.UnresolvedWindows }
                : QuotaSnapshot.Unavailable("暂时无法读取 Codex 额度"));
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private void SetForGeneration(QuotaSnapshot snapshot, long generation)
    {
        lock (resetGate)
            if (generation == Interlocked.Read(ref accountGeneration)) Set(snapshot);
    }

    internal static string? RecoveryScopeFromRpc(JsonElement accountBefore, JsonElement rateLimitResult,
        JsonElement accountAfter, string? connectionBefore, string? connectionAfter,
        long generationBefore, long generationAfter)
    {
        if (connectionBefore is not { Length: 32 } || !connectionBefore.All(char.IsAsciiHexDigit)
            || connectionBefore != connectionAfter || generationBefore != generationAfter
            || AccountContext(accountBefore) is not { } before
            || AccountContext(accountAfter) is not { } after || before != after
            || rateLimitResult.ValueKind != JsonValueKind.Object) return null;

        // Newer supported protocol: this ID belongs to the quota response itself.
        // Missing/null is an older-server capability, not an account read failure.
        if (rateLimitResult.TryGetProperty("accountId", out var id) && id.ValueKind != JsonValueKind.Null)
            return id.ValueKind == JsonValueKind.String && SafeContextValue(id.GetString(), 256)
                ? QuotaResetTracker.ScopeForSource("response-account\n" + id.GetString()) : null;

        // Conservative continuous-source fallback, NOT globally verified account binding.
        // Same email alone cannot distinguish workspaces or unannounced ABA switches.
        // A new transport/auth generation has no old baseline and cannot revive green.
        return before.Email is { } email
            ? QuotaResetTracker.ScopeForSource($"connection\n{connectionBefore}\n{generationBefore}\n{before.Type}\n{email}")
            : null;
    }

    internal static bool IsStableAccountContext(JsonElement before, JsonElement after) =>
        AccountContext(before) is { } context && AccountContext(after) == context;

    private static (string Type, string? Email)? AccountContext(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("account", out var account)
            || account.ValueKind != JsonValueKind.Object || !account.TryGetProperty("type", out var typeNode)
            || typeNode.ValueKind != JsonValueKind.String || typeNode.GetString() is not ("chatgpt" or "chatgptAuthTokens"))
            return null;
        string? email = null;
        if (account.TryGetProperty("email", out var emailNode) && emailNode.ValueKind != JsonValueKind.Null)
        {
            if (emailNode.ValueKind != JsonValueKind.String || !SafeContextValue(emailNode.GetString(), 320)) return null;
            email = emailNode.GetString()!;
            if (email.IndexOf('@') <= 0 || email.EndsWith('@')) return null;
        }
        return (typeNode.GetString()!, email);
    }

    private static bool SafeContextValue(string? value, int maximumLength) =>
        value is { Length: > 0 } && value.Length <= maximumLength && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    private static bool TryValidateChatGptAccount(JsonElement result, out string message, out string? subscriptionPlan)
    {
        subscriptionPlan = null;
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("account", out var account)
            || account.ValueKind != JsonValueKind.Object)
        {
            message = "请先在 Codex 中登录 ChatGPT";
            return false;
        }

        var type = account.TryGetProperty("type", out var typeNode) && typeNode.ValueKind == JsonValueKind.String ? typeNode.GetString() : null;
        if (type is not ("chatgpt" or "chatgptAuthTokens"))
        {
            message = "当前登录方式不提供 ChatGPT 额度";
            return false;
        }

        var rawPlan = account.TryGetProperty("planType", out var planNode) && planNode.ValueKind == JsonValueKind.String
            ? planNode.GetString()
            : null;
        subscriptionPlan = SubscriptionPlanFormatter.Format(rawPlan);
        message = string.Empty;
        return true;
    }

    private void OnNotification(object? sender, string method)
    {
        if (method is "account/updated" or "account/login/completed")
        {
            quotaScope = null;
            lock (resetGate)
            {
                Interlocked.Increment(ref accountGeneration);
                resetTracker.InvalidateSample(accountChanged: true);
                PublishResetConfirmation(null);
            }
            Set(QuotaSnapshot.Unavailable("账号已变化，正在刷新…"));
        }

        if (method is "account/updated" or "account/login/completed" or "account/rateLimits/updated")
        {
            _ = RefreshAfterNotificationAsync(lifetime.Token);
        }
    }

    private void OnConnectionLost()
    {
        quotaScope = null;
        lock (resetGate)
        {
            Interlocked.Increment(ref accountGeneration);
            resetTracker.InvalidateSample(accountChanged: true);
            PublishResetConfirmation(null);
        }
    }

    private async Task RefreshAfterNotificationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(300, cancellationToken);
            await RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public void DismissSession(string threadId)
    {
        sessionMonitor.Dismiss(threadId);
        var remaining = CurrentSessions.Sessions.Where(session => session.Id != threadId);
        SetSessions(CodexSessionsSnapshot.Create(remaining, DateTimeOffset.Now));
    }

    public bool SetSubscriptionDetailsEnabled(bool enabled)
    {
        var next = enabled ? 1 : 0;
        return Interlocked.Exchange(ref subscriptionDetailsEnabled, next) != next;
    }

    internal async Task ClearBrowserSubscriptionMetadataAsync(CancellationToken cancellationToken)
    {
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            var next = RemoveBrowserSubscriptionMetadata(Current, Volatile.Read(ref subscriptionDetailsEnabled) != 0);
            if (!ReferenceEquals(next, Current))
            {
                Set(next);
            }
        }
        finally
        {
            refreshLock.Release();
        }
    }

    internal static QuotaSnapshot RemoveBrowserSubscriptionMetadata(QuotaSnapshot snapshot, bool enabled) =>
        snapshot.SubscriptionExpiration.Source != SubscriptionExpirationSource.ChatGptWeb
            ? snapshot
            : snapshot.WithSubscriptionExpiration(enabled
                ? SubscriptionExpirationSnapshot.Unavailable(DateTimeOffset.Now, SubscriptionExpirationSource.None, "网页登录数据已清除")
                : SubscriptionExpirationSnapshot.Disabled());

    internal static SubscriptionExpirationSnapshot ResolveSubscriptionRefresh(
        SubscriptionExpirationSnapshot updated,
        SubscriptionExpirationSnapshot previous) =>
        updated.Availability == SubscriptionExpirationAvailability.Unavailable
        && previous.Availability == SubscriptionExpirationAvailability.Available
        && previous.Source != SubscriptionExpirationSource.ChatGptWeb
            ? previous.AsStale()
            : updated;

    private async Task RefreshPeriodicallyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(AutomaticRefreshInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await RefreshAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshSessionsAsync(CancellationToken cancellationToken)
    {
        if (!sessionsStarted || !await sessionsRefreshLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            var snapshot = await sessionMonitor.RefreshFromDiskAsync(DateTimeOffset.Now, cancellationToken);
            SetSessions(snapshot);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException or JsonException)
        {
        }
        finally
        {
            sessionsRefreshLock.Release();
        }
    }

    private async Task RefreshSessionsPeriodicallyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(SessionsRefreshInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await RefreshSessionsAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void Set(QuotaSnapshot snapshot)
    {
        Current = snapshot;
        SnapshotChanged?.Invoke(this, snapshot);
    }

    private void PublishResetConfirmation(DateTimeOffset? until)
    {
        if (publishedResetUntil == until) return;
        publishedResetUntil = until;
        ResetConfirmationChanged?.Invoke(until);
    }

    private void SetSessions(CodexSessionsSnapshot snapshot)
    {
        var changed = !CurrentSessions.Sessions.SequenceEqual(snapshot.Sessions);
        CurrentSessions = snapshot;
        if (changed) SessionsChanged?.Invoke(this, snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (periodicRefreshTask is not null)
        {
            await periodicRefreshTask.ConfigureAwait(false);
        }
        if (periodicSessionsRefreshTask is not null)
        {
            await periodicSessionsRefreshTask.ConfigureAwait(false);
        }
        client.Notification -= OnNotification;
        client.ConnectionLost -= OnConnectionLost;
        await client.DisposeAsync().ConfigureAwait(false);
        subscriptionMetadataService.Dispose();
        refreshLock.Dispose();
        sessionsRefreshLock.Dispose();
        lifetime.Dispose();
    }
}
