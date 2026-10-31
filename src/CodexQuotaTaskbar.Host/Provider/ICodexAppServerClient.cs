using System.Text.Json;

namespace CodexQuotaTaskbar.Host.Provider;

internal interface ICodexAppServerClient : IAsyncDisposable
{
    string? InitializedConnectionId { get; }
    event EventHandler<string>? Notification;
    event Action? ConnectionLost;
    Task StartAsync(CancellationToken cancellationToken);
    Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken,
        Func<bool>? authorizeBeforeSend = null);
}
