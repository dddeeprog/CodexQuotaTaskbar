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
        Assert.Equal(1, snapshot.WaitingCount);
        Assert.Equal(1, snapshot.RunningCount);
        Assert.Equal(1, snapshot.CompletedCount);
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

    [Fact]
    public void Deduplicates_resumed_session_segments_using_the_newest_state()
    {
        var now = DateTimeOffset.UtcNow;

        var snapshot = CodexSessionsSnapshot.Create([
            new("resumed", "旧分段", CodexSessionState.Failed, now.AddMinutes(-1)),
            new("resumed", "最新分段", CodexSessionState.Running, now)], now);

        var session = Assert.Single(snapshot.Sessions);
        Assert.Equal("resumed", session.Id);
        Assert.Equal("最新分段", session.Title);
        Assert.Equal(CodexSessionState.Running, session.State);
        Assert.Equal(1, snapshot.ActiveCount);
        Assert.Equal(1, snapshot.RunningCount);
        Assert.Equal(0, snapshot.CompletedCount);
    }

    [Fact]
    public void Duplicate_session_ties_are_resolved_deterministically_by_attention_priority()
    {
        var now = DateTimeOffset.UtcNow;
        CodexSessionSnapshot[] sessions = [
            new("same", "运行", CodexSessionState.Running, now),
            new("same", "等待", CodexSessionState.Waiting, now)];

        var forward = CodexSessionsSnapshot.Create(sessions, now);
        var reverse = CodexSessionsSnapshot.Create(sessions.Reverse(), now);

        Assert.Equal(CodexSessionState.Waiting, Assert.Single(forward.Sessions).State);
        Assert.Equal(forward.Sessions.Single(), reverse.Sessions.Single());
    }
}
