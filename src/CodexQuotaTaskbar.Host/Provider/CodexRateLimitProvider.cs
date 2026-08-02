using System.IO;
using System.Text.Json;
using CodexQuotaTaskbar.Core.Provider;
using CodexQuotaTaskbar.Core.Quota;

namespace CodexQuotaTaskbar.Host.Provider;

internal sealed class CodexRateLimitProvider : IQuotaProvider
{
    private readonly CodexAppServerClient client = new();
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private long accountGeneration;
    private bool started;

    internal CodexRateLimitProvider()
    {
        client.Notification += OnNotification;
    }

    public event EventHandler<QuotaSnapshot>? SnapshotChanged;
    public QuotaSnapshot Current { get; private set; } = QuotaSnapshot.Unavailable("正在连接 Codex…");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await client.StartAsync(cancellationToken);
            started = true;
            await RefreshAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            Set(QuotaSnapshot.Unavailable("未找到可用的 Codex App Server"));
        }
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
            if (!TryValidateChatGptAccount(accountResult, out var accountMessage))
            {
                Set(QuotaSnapshot.Unavailable(accountMessage));
                return;
            }

            var result = await client.RequestAsync("account/rateLimits/read", new { }, cancellationToken);
            if (generation != Interlocked.Read(ref accountGeneration))
            {
                return;
            }

            Set(AppServerRateLimitsParser.Parse(result.GetRawText(), DateTimeOffset.Now));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException or AppServerProtocolException)
        {
            Set(Current.Windows.Count > 0 && Current.CapturedAt is { } captured
                ? QuotaSnapshot.Stale(Current.Windows, captured, "连接中断，显示上次额度")
                : QuotaSnapshot.Unavailable("暂时无法读取 Codex 额度"));
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private static bool TryValidateChatGptAccount(JsonElement result, out string message)
    {
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

    private void Set(QuotaSnapshot snapshot)
    {
        Current = snapshot;
        SnapshotChanged?.Invoke(this, snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        client.Notification -= OnNotification;
        await client.DisposeAsync().ConfigureAwait(false);
        refreshLock.Dispose();
        lifetime.Dispose();
    }
}
