using CodexQuotaTaskbar.Core.Quota;

namespace CodexQuotaTaskbar.Host.Provider;

internal sealed class DemoQuotaProvider : IQuotaProvider
{
    public event EventHandler<QuotaSnapshot>? SnapshotChanged;
    public QuotaSnapshot Current { get; private set; } = QuotaSnapshot.Unavailable("演示数据准备中");

    public Task StartAsync(CancellationToken cancellationToken) => RefreshAsync(cancellationToken);

    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.Now;
        Current = QuotaSnapshot.Available([
            new(QuotaWindowKind.Primary, 72, 300, now.AddHours(3)),
            new(QuotaWindowKind.Secondary, 41, 10080, now.AddDays(4))], now, "Pro Lite");
        SnapshotChanged?.Invoke(this, Current);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
