using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Host.Provider;

namespace CodexQuotaTaskbar.Host.Tests.Provider;

public sealed class CompanionActivityTests
{
    [Fact]
    public async Task Unchanged_polls_do_not_rebuild_ui_but_every_session_change_still_notifies()
    {
        await using var provider = new CodexRateLimitProvider(subscriptionDetailsEnabled: false);
        var setter = typeof(CodexRateLimitProvider).GetMethod("SetSessions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var notifications = 0;
        provider.SessionsChanged += (_, _) => notifications++;
        var now = DateTimeOffset.UtcNow;
        var session = new CodexSessionSnapshot("a", "任务", CodexSessionState.Running, now);
        void Send(CodexSessionSnapshot[] sessions, DateTimeOffset captured) => setter.Invoke(provider, [CodexSessionsSnapshot.Create(sessions, captured)]);
        Send([session], now);
        for (var i = 1; i <= 100; i++) Send([session with { }], now.AddSeconds(i));
        Assert.Equal(1, notifications);
        Assert.Equal(now.AddSeconds(100), provider.CurrentSessions.CapturedAt);
        Send([session with { Activity = CodexSessionActivity.Thinking }], now);
        Assert.Equal(2, notifications);
        Send([session with { Title = "改名" }], now);
        Assert.Equal(3, notifications);
        Send([session with { State = CodexSessionState.Waiting }], now);
        Assert.Equal(4, notifications);
        Send([], now);
        Assert.Equal(5, notifications);
    }

    [Fact]
    public void Explicit_metadata_drives_activity_and_cancellation_is_distinct()
    {
        var tracker = new RolloutActivityTracker();
        tracker.ApplyLine("""{"type":"event_msg","payload":{"type":"task_started"}}""");
        tracker.ApplyLine("""{"type":"event_msg","payload":{"type":"agent_reasoning"}}""");
        Assert.Equal(CodexSessionActivity.Thinking, tracker.Activity);
        tracker.ApplyLine("""{"type":"event_msg","payload":{"type":"agent_message","message":"生气 出错 搜索"}}""");
        Assert.Equal(CodexSessionActivity.Output, tracker.Activity);
        tracker.ApplyLine("""{"type":"event_msg","payload":{"type":"turn_aborted"}}""");
        Assert.Equal(CodexSessionActivity.Stopped, tracker.Activity);
        Assert.Equal(CodexSessionState.Failed, tracker.State);
    }

    [Fact]
    public void Async_input_request_waits_until_matching_result()
    {
        var tracker = new RolloutActivityTracker();
        tracker.ApplyLine("""{"type":"event_msg","payload":{"type":"task_started"}}""");
        tracker.ApplyLine("""{"type":"response_item","payload":{"type":"function_call","name":"request_user_input_async","call_id":"input"}}""");
        Assert.Equal(CodexSessionState.Waiting, tracker.State);
        tracker.ApplyLine("""{"type":"response_item","payload":{"type":"function_call_output","call_id":"wrong"}}""");
        Assert.Equal(CodexSessionState.Waiting, tracker.State);
        tracker.ApplyLine("""{"type":"response_item","payload":{"type":"function_call_output","call_id":"input"}}""");
        Assert.Equal(CodexSessionState.Running, tracker.State);
    }
}
