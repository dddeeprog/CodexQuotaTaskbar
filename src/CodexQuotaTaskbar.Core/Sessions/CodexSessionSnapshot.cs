namespace CodexQuotaTaskbar.Core.Sessions;

public enum CodexSessionState
{
    Idle,
    Running,
    Waiting,
    Failed,
    Review,
}

public sealed record CodexSessionSnapshot(
    string Id,
    string Title,
    CodexSessionState State,
    DateTimeOffset UpdatedAt);

public sealed record CodexSessionsSnapshot(IReadOnlyList<CodexSessionSnapshot> Sessions, DateTimeOffset CapturedAt)
{
    public static CodexSessionsSnapshot Empty { get; } = new([], DateTimeOffset.MinValue);

    public CodexSessionSnapshot? Attention => Sessions.FirstOrDefault();
    public int ActiveCount => Sessions.Count(session => session.State is CodexSessionState.Running or CodexSessionState.Waiting);
    public int RunningCount => Sessions.Count(session => session.State == CodexSessionState.Running);
    public int WaitingCount => Sessions.Count(session => session.State == CodexSessionState.Waiting);
    public int CompletedCount => Sessions.Count(session => session.State == CodexSessionState.Review);

    public static CodexSessionsSnapshot Create(IEnumerable<CodexSessionSnapshot> sessions, DateTimeOffset capturedAt) =>
        new(sessions
            .GroupBy(session => session.Id, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(session => session.UpdatedAt)
                .ThenBy(session => Priority(session.State))
                .ThenBy(session => session.Title, StringComparer.Ordinal)
                .First())
            .Where(session => session.State != CodexSessionState.Idle)
            .OrderBy(session => Priority(session.State))
            .ThenByDescending(session => session.UpdatedAt)
            .ThenBy(session => session.Id, StringComparer.Ordinal)
            .ToArray(), capturedAt);

    public static int Priority(CodexSessionState state) => state switch
    {
        CodexSessionState.Waiting => 0,
        CodexSessionState.Failed => 1,
        CodexSessionState.Review => 2,
        CodexSessionState.Running => 3,
        _ => 4,
    };
}
