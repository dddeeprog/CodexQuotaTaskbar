using CodexQuotaTaskbar.Core.Quota;

namespace CodexQuotaTaskbar.Host.Provider;

internal interface IQuotaProvider : IAsyncDisposable
{
    event EventHandler<QuotaSnapshot>? SnapshotChanged;
    QuotaSnapshot Current { get; }
    Task StartAsync(CancellationToken cancellationToken);
    Task RefreshAsync(CancellationToken cancellationToken);
}
