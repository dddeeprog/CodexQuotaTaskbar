using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Core.Sessions;

namespace CodexQuotaTaskbar.Host.Provider;

internal sealed class DemoQuotaProvider : IQuotaProvider
{
    public event EventHandler<QuotaSnapshot>? SnapshotChanged;
    public event EventHandler<CodexSessionsSnapshot>? SessionsChanged;
    public QuotaSnapshot Current { get; private set; } = QuotaSnapshot.Unavailable("演示数据准备中");
    public CodexSessionsSnapshot CurrentSessions { get; private set; } = CodexSessionsSnapshot.Empty;

    public Task StartAsync(CancellationToken cancellationToken) => RefreshAsync(cancellationToken);

    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.Now;
        Current = QuotaSnapshot.Available([
            new(QuotaWindowKind.Primary, 72, 300, now.AddHours(3)),
            new(QuotaWindowKind.Secondary, 41, 10080, now.AddDays(4))], now, "Pro Lite");
        CurrentSessions = CodexSessionsSnapshot.Create([
            new("demo-1", "合并项目", CodexSessionState.Waiting, now),
            new("demo-2", "完善采样间扩建施工清单", CodexSessionState.Running, now.AddSeconds(-10)),
            new("demo-3", "Codex 额度显示器", CodexSessionState.Running, now.AddSeconds(-20)),
            new("demo-4", "检查自动更新发布流程", CodexSessionState.Review, now.AddSeconds(-30))], now);
        SnapshotChanged?.Invoke(this, Current);
        SessionsChanged?.Invoke(this, CurrentSessions);
        return Task.CompletedTask;
    }

    public void DismissSession(string threadId)
    {
        CurrentSessions = CodexSessionsSnapshot.Create(
            CurrentSessions.Sessions.Where(session => !string.Equals(session.Id, threadId, StringComparison.Ordinal)),
            DateTimeOffset.Now);
        SessionsChanged?.Invoke(this, CurrentSessions);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
