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
    void DismissSession(string threadId);
}
