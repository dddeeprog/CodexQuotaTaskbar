using System.IO;
using System.Text.Json;
using CodexQuotaTaskbar.Core.Provider;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Core.Sessions;

namespace CodexQuotaTaskbar.Host.Provider;

internal sealed class CodexRateLimitProvider : IQuotaProvider
{
    private readonly CodexAppServerClient client = new();
    private readonly CodexSubscriptionMetadataService subscriptionMetadataService;
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private readonly SemaphoreSlim sessionsRefreshLock = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly RolloutSessionMonitor sessionMonitor = new(DateTimeOffset.UtcNow);
    private Task? periodicRefreshTask;
    private Task? periodicSessionsRefreshTask;
    private long accountGeneration;
    private int subscriptionDetailsEnabled;
    private bool started;
    private bool sessionsStarted;

    internal static TimeSpan AutomaticRefreshInterval { get; } = TimeSpan.FromMinutes(3);
    internal static TimeSpan SessionsRefreshInterval { get; } = TimeSpan.FromSeconds(2);

    internal CodexRateLimitProvider(
        bool subscriptionDetailsEnabled = true,
        CodexSubscriptionMetadataService? subscriptionMetadataService = null)
    {
        this.subscriptionDetailsEnabled = subscriptionDetailsEnabled ? 1 : 0;
        this.subscriptionMetadataService = subscriptionMetadataService ?? new CodexSubscriptionMetadataService();
        client.Notification += OnNotification;
    }

    public event EventHandler<QuotaSnapshot>? SnapshotChanged;
    public event EventHandler<CodexSessionsSnapshot>? SessionsChanged;
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
            var accountResult = await client.RequestAsync("account/read", new { refreshToken = false }, cancellationToken);
            if (!TryValidateChatGptAccount(accountResult, out var accountMessage, out var subscriptionPlan))
            {
                Set(QuotaSnapshot.Unavailable(accountMessage));
                return;
            }

            var result = await client.RequestAsync("account/rateLimits/read", new { }, cancellationToken);
            if (generation != Interlocked.Read(ref accountGeneration))
            {
                return;
            }

            var baseSnapshot = AppServerRateLimitsParser.Parse(result.GetRawText(), DateTimeOffset.Now, subscriptionPlan);
            var previousExpiration = Current.SubscriptionExpiration;
            if (Volatile.Read(ref subscriptionDetailsEnabled) == 0)
            {
                Set(baseSnapshot.WithSubscriptionExpiration(SubscriptionExpirationSnapshot.Disabled()));
                return;
            }

            var pendingExpiration = previousExpiration.Availability == SubscriptionExpirationAvailability.Available
                ? previousExpiration.AsStale()
                : SubscriptionExpirationSnapshot.Unavailable(
                    DateTimeOffset.Now,
                    SubscriptionExpirationSource.CodexLogin,
                    "正在读取…");
            Set(baseSnapshot.WithSubscriptionExpiration(pendingExpiration));

            var expiration = await subscriptionMetadataService.ReadAsync(cancellationToken);
            if (generation != Interlocked.Read(ref accountGeneration))
            {
                return;
            }
            if (Volatile.Read(ref subscriptionDetailsEnabled) == 0)
            {
                Set(baseSnapshot.WithSubscriptionExpiration(SubscriptionExpirationSnapshot.Disabled()));
                return;
            }
            expiration = ResolveSubscriptionRefresh(expiration, previousExpiration);
            Set(baseSnapshot.WithSubscriptionExpiration(expiration));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException or AppServerProtocolException)
        {
            Set(Current.Windows.Count > 0 && Current.CapturedAt is { } captured
                ? QuotaSnapshot.Stale(
                    Current.Windows,
                    captured,
                    "连接中断，显示上次额度",
                    Current.SubscriptionPlan,
                    Current.SubscriptionExpiration,
                    Current.ExtraCredits)
                : QuotaSnapshot.Unavailable("暂时无法读取 Codex 额度"));
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private static bool TryValidateChatGptAccount(JsonElement result, out string message, out string? subscriptionPlan)
    {
        subscriptionPlan = null;
        if (!result.TryGetProperty("account", out var account) || account.ValueKind == JsonValueKind.Null)
        {
            message = "请先在 Codex 中登录 ChatGPT";
            return false;
        }

        var type = account.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : null;
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
        if (method == "account/updated")
        {
            Interlocked.Increment(ref accountGeneration);
            Set(QuotaSnapshot.Unavailable("账号已变化，正在刷新…"));
        }

        if (method is "account/updated" or "account/rateLimits/updated")
        {
            _ = RefreshAfterNotificationAsync(lifetime.Token);
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

    private void SetSessions(CodexSessionsSnapshot snapshot)
    {
        CurrentSessions = snapshot;
        SessionsChanged?.Invoke(this, snapshot);
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
        await client.DisposeAsync().ConfigureAwait(false);
        subscriptionMetadataService.Dispose();
        refreshLock.Dispose();
        sessionsRefreshLock.Dispose();
        lifetime.Dispose();
    }
}
