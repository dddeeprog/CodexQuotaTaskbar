using CodexQuotaTaskbar.Core.Sessions;

namespace CodexQuotaTaskbar.Host.Tests;

public sealed class AppTests
{
    [Theory]
    [InlineData(CodexSessionState.Review, true)]
    [InlineData(CodexSessionState.Running, false)]
    [InlineData(CodexSessionState.Waiting, false)]
    [InlineData(CodexSessionState.Failed, false)]
    public void Dismisses_only_completed_sessions_after_they_are_opened(CodexSessionState state, bool expected)
    {
        var session = new CodexSessionSnapshot("thread", "任务", state, DateTimeOffset.UtcNow);

        Assert.Equal(expected, App.ShouldDismissViewedSession(session));
    }
}
