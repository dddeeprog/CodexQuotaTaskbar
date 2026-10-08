using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Core.Sessions;

namespace CodexQuotaTaskbar.Host.Provider;

internal interface IQuotaProvider : IAsyncDisposable
{
    event EventHandler<QuotaSnapshot>? SnapshotChanged;
    event EventHandler<CodexSessionsSnapshot>? SessionsChanged;
    QuotaSnapshot Current { get; }
    CodexSessionsSnapshot CurrentSessions { get; }
    Task StartAsync(CancellationToken cancellationToken);
    Task RefreshAsync(CancellationToken cancellationToken);
    bool SetSubscriptionDetailsEnabled(bool enabled);
    void DismissSession(string threadId);
}
