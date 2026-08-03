using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Host.Provider;
using System.Text.Json;

namespace CodexQuotaTaskbar.Host.Tests.Provider;

public sealed class RolloutActivityTrackerTests
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.Parse("2026-08-03T05:00:00Z");

    [Fact]
    public void Tracks_running_waiting_and_review_states()
    {
        var tracker = new RolloutActivityTracker();
        tracker.ApplyLine(Event("2026-08-03T05:01:00Z", "task_started", "turn-1"));

        Assert.Equal(CodexSessionState.Running, tracker.VisibleState(StartedAt, StartedAt.AddMinutes(1)));

        tracker.ApplyLine(Response("2026-08-03T05:02:00Z", "custom_tool_call", "call-1", "request_user_input"));
        Assert.Equal(CodexSessionState.Waiting, tracker.VisibleState(StartedAt, StartedAt.AddMinutes(2)));

        tracker.ApplyLine(Response("2026-08-03T05:03:00Z", "custom_tool_call_output", "call-1"));
        Assert.Equal(CodexSessionState.Running, tracker.VisibleState(StartedAt, StartedAt.AddMinutes(3)));

        tracker.ApplyLine(Event("2026-08-03T05:04:00Z", "task_complete", "turn-1"));
        Assert.Equal(CodexSessionState.Review, tracker.VisibleState(StartedAt, StartedAt.AddMinutes(4)));
    }

    [Fact]
    public void Keeps_an_aborted_turn_failed_when_task_complete_follows()
    {
        var tracker = new RolloutActivityTracker();
        tracker.ApplyLine(Event("2026-08-03T05:01:00Z", "task_started", "turn-1"));
        tracker.ApplyLine(Event("2026-08-03T05:02:00Z", "turn_aborted", "turn-1"));
        tracker.ApplyLine(Event("2026-08-03T05:02:01Z", "task_complete", "turn-1"));

        Assert.Equal(CodexSessionState.Failed, tracker.VisibleState(StartedAt, StartedAt.AddMinutes(3)));
    }

    [Theory]
    [InlineData("exec_approval_request", "exec_command_begin")]
    [InlineData("apply_patch_approval_request", "patch_apply_begin")]
    public void Tracks_persisted_approval_requests_until_execution_resumes(string requestType, string resumeType)
    {
        var tracker = new RolloutActivityTracker();
        tracker.ApplyLine(Event("2026-08-03T05:01:00Z", "task_started", "turn-1"));
        tracker.ApplyLine(Event("2026-08-03T05:02:00Z", requestType, "turn-1", "call-1"));

        Assert.Equal(CodexSessionState.Waiting, tracker.VisibleState(StartedAt, StartedAt.AddMinutes(2)));

        tracker.ApplyLine(Event("2026-08-03T05:03:00Z", resumeType, "turn-1", "call-1"));
        Assert.Equal(CodexSessionState.Running, tracker.VisibleState(StartedAt, StartedAt.AddMinutes(3)));
    }

    [Fact]
    public void Does_not_surface_completions_that_predate_monitor_start()
    {
        var tracker = new RolloutActivityTracker();
        tracker.ApplyLine(Event("2026-08-03T04:59:00Z", "task_complete", "turn-1"));

        Assert.Equal(CodexSessionState.Idle, tracker.VisibleState(StartedAt, StartedAt));
    }

    [Fact]
    public void Hides_completed_tasks_after_the_short_review_window()
    {
        var tracker = new RolloutActivityTracker();
        tracker.ApplyLine(Event("2026-08-03T05:01:00Z", "task_complete", "turn-1"));

        Assert.Equal(CodexSessionState.Review, tracker.VisibleState(StartedAt, StartedAt.AddMinutes(1).AddSeconds(30)));
        Assert.Equal(CodexSessionState.Idle, tracker.VisibleState(StartedAt, StartedAt.AddMinutes(1).AddSeconds(31)));
        Assert.Equal(TimeSpan.FromSeconds(30), RolloutActivityTracker.CompletedVisibilityDuration);
    }

    [Fact]
    public void Extracts_a_short_task_title_from_the_latest_user_message()
    {
        var tracker = new RolloutActivityTracker();
        tracker.ApplyLine(JsonSerializer.Serialize(new
        {
            timestamp = "2026-08-03T05:01:00Z",
            type = "event_msg",
            payload = new
            {
                type = "user_message",
                message = "# Files mentioned by the user:\nC:\\Temp\\shot.png\n\n## My request for Codex:\n岛上保持额度，多会话展开最多显示三个任务",
            },
        }));

        Assert.Equal("岛上保持额度，多会话展开最多显示三个任务", tracker.TitleHint);
    }

    private static string Event(string timestamp, string type, string turnId, string? callId = null) =>
        JsonSerializer.Serialize(new { timestamp, type = "event_msg", payload = new { type, turn_id = turnId, call_id = callId } });

    private static string Response(string timestamp, string type, string callId, string? name = null) => JsonSerializer.Serialize(new
    {
        timestamp,
        type = "response_item",
        payload = new { type, call_id = callId, name },
    });
}
