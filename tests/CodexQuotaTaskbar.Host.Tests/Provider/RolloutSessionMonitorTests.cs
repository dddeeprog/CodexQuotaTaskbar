using System.Text.Json;
using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Host.Provider;

namespace CodexQuotaTaskbar.Host.Tests.Provider;

public sealed class RolloutSessionMonitorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RolloutSessionMonitorTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Empty_codex_home_falls_back_to_the_user_profile()
    {
        Assert.Equal(Path.Combine("C:\\Users\\Test", ".codex"), RolloutSessionMonitor.ResolveCodexRoot("", "C:\\Users\\Test"));
    }

    [Fact]
    public async Task Reads_the_recent_tail_tracks_changes_and_dismisses_only_the_current_state()
    {
        var sessionsRoot = Path.Combine(root, "sessions", "2026", "08", "03");
        Directory.CreateDirectory(sessionsRoot);
        var threadId = "019fb91e-5990-7c53-a448-c5287b8678af";
        var rollout = Path.Combine(sessionsRoot, $"rollout-{threadId}.jsonl");
        await File.WriteAllTextAsync(
            Path.Combine(root, "session_index.jsonl"),
            JsonSerializer.Serialize(new { id = threadId, thread_name = "All", updated_at = "2026-08-03T05:01:00Z" }) + "\n");
        await File.WriteAllTextAsync(rollout, new string('x', 5 * 1024 * 1024) + "\n"
            + UserMessage("2026-08-03T05:00:59Z", "完善采样间扩建施工清单") + "\n"
            + Event("2026-08-03T05:01:00Z", "task_started", "turn-1") + "\n");
        var monitor = new RolloutSessionMonitor(DateTimeOffset.Parse("2026-08-03T05:00:00Z"), root);

        var running = await monitor.RefreshFromDiskAsync(DateTimeOffset.Parse("2026-08-03T05:01:01Z"), CancellationToken.None);
        var session = Assert.Single(running.Sessions);
        Assert.Equal(CodexSessionState.Running, session.State);
        Assert.Equal("完善采样间扩建施工清单", session.Title);

        await File.AppendAllTextAsync(
            Path.Combine(root, "session_index.jsonl"),
            JsonSerializer.Serialize(new { id = threadId, thread_name = "施工清单最终核对", updated_at = "2026-08-03T05:01:30Z" }) + "\n");
        var renamed = await monitor.RefreshFromDiskAsync(DateTimeOffset.Parse("2026-08-03T05:01:31Z"), CancellationToken.None);
        Assert.Equal("施工清单最终核对", Assert.Single(renamed.Sessions).Title);

        monitor.Dismiss(threadId);
        Assert.Empty((await monitor.RefreshFromDiskAsync(DateTimeOffset.Parse("2026-08-03T05:01:02Z"), CancellationToken.None)).Sessions);

        await File.AppendAllTextAsync(rollout, Event("2026-08-03T05:02:00Z", "task_complete", "turn-1") + "\n");
        var completed = await monitor.RefreshFromDiskAsync(DateTimeOffset.Parse("2026-08-03T05:02:01Z"), CancellationToken.None);
        Assert.Equal(CodexSessionState.Review, Assert.Single(completed.Sessions).State);
    }

    [Fact]
    public async Task Excludes_internal_subagent_rollouts_from_user_sessions()
    {
        var sessionsRoot = Path.Combine(root, "sessions", "2026", "08", "03");
        Directory.CreateDirectory(sessionsRoot);
        var threadId = "019fba9d-8d54-7643-ba9c-ddc1137e7ebf";
        var rollout = Path.Combine(sessionsRoot, $"rollout-{threadId}.jsonl");
        await File.WriteAllTextAsync(rollout,
            SessionMeta("C:\\Work\\Internal", isSubagent: true) + "\n"
            + UserMessage("2026-08-03T05:00:59Z", "修复") + "\n"
            + Event("2026-08-03T05:01:00Z", "task_started", "turn-1") + "\n");
        var monitor = new RolloutSessionMonitor(DateTimeOffset.Parse("2026-08-03T05:00:00Z"), root);

        var snapshot = await monitor.RefreshFromDiskAsync(DateTimeOffset.Parse("2026-08-03T05:01:01Z"), CancellationToken.None);

        Assert.Empty(snapshot.Sessions);
    }

    private static string Event(string timestamp, string type, string turnId) =>
        JsonSerializer.Serialize(new { timestamp, type = "event_msg", payload = new { type, turn_id = turnId } });

    private static string UserMessage(string timestamp, string message) =>
        JsonSerializer.Serialize(new { timestamp, type = "event_msg", payload = new { type = "user_message", message } });

    private static string SessionMeta(string cwd, bool isSubagent) => JsonSerializer.Serialize(new
    {
        type = "session_meta",
        payload = new
        {
            cwd,
            thread_source = isSubagent ? "subagent" : "cli",
            source = isSubagent ? new { subagent = new { } } : null,
        },
    });

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}
