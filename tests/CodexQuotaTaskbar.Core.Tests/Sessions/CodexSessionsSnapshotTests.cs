using CodexQuotaTaskbar.Core.Sessions;

namespace CodexQuotaTaskbar.Core.Tests.Sessions;

public sealed class CodexSessionsSnapshotTests
{
    [Fact]
    public void Orders_attention_like_the_native_pet()
    {
        var now = DateTimeOffset.UtcNow;

        var snapshot = CodexSessionsSnapshot.Create([
            new("running", "运行", CodexSessionState.Running, now),
            new("review", "待查看", CodexSessionState.Review, now),
            new("failed", "失败", CodexSessionState.Failed, now),
            new("waiting", "等待", CodexSessionState.Waiting, now)], now);

        Assert.Equal(["waiting", "failed", "review", "running"], snapshot.Sessions.Select(session => session.Id));
        Assert.Equal("waiting", snapshot.Attention?.Id);
        Assert.Equal(2, snapshot.ActiveCount);
    }

    [Fact]
    public void Removes_idle_sessions_and_orders_equal_states_by_recency()
    {
        var now = DateTimeOffset.UtcNow;

        var snapshot = CodexSessionsSnapshot.Create([
            new("old", "旧", CodexSessionState.Running, now.AddMinutes(-1)),
            new("idle", "空闲", CodexSessionState.Idle, now),
            new("new", "新", CodexSessionState.Running, now)], now);

        Assert.Equal(["new", "old"], snapshot.Sessions.Select(session => session.Id));
    }
}
