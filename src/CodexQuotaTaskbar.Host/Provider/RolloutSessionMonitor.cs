using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexQuotaTaskbar.Core.Sessions;

namespace CodexQuotaTaskbar.Host.Provider;

internal sealed class RolloutSessionMonitor
{
    private const long InitialScanBytes = 4 * 1024 * 1024;
    private const int MaximumTrackedSessions = 20;
    private static readonly Regex ThreadIdPattern = new(
        "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly Dictionary<string, TrackedSession> tracked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionVersion> dismissed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> indexedTitles = new(StringComparer.Ordinal);
    private readonly DateTimeOffset startedAt;
    private readonly string sessionIndexPath;
    private readonly string sessionsRoot;
    private long sessionIndexPosition;

    internal RolloutSessionMonitor(DateTimeOffset startedAt, string? codexRoot = null)
    {
        this.startedAt = startedAt;
        var root = ResolveCodexRoot(
            codexRoot ?? Environment.GetEnvironmentVariable("CODEX_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        sessionsRoot = Path.Combine(root, "sessions");
        sessionIndexPath = Path.Combine(root, "session_index.jsonl");
    }

    internal static string ResolveCodexRoot(string? configuredRoot, string userProfile) =>
        string.IsNullOrWhiteSpace(configuredRoot) ? Path.Combine(userProfile, ".codex") : configuredRoot;

    internal async Task<CodexSessionsSnapshot> RefreshFromDiskAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await RefreshTitleIndexAsync(cancellationToken).ConfigureAwait(false);
        var sessions = new List<CodexSessionSnapshot>();
        foreach (var metadata in EnumerateRecentRollouts())
        {
            if (!tracked.TryGetValue(metadata.Id, out var value) || !string.Equals(value.Path, metadata.Path, StringComparison.OrdinalIgnoreCase))
            {
                var sessionMetadata = await ReadSessionMetadataAsync(metadata.Path, cancellationToken).ConfigureAwait(false);
                value = new TrackedSession(metadata.Path, sessionMetadata.FallbackTitle, sessionMetadata.IsSubagent);
                tracked[metadata.Id] = value;
            }

            if (value.IsSubagent)
            {
                continue;
            }

            await ReadAppendedLinesAsync(value, cancellationToken).ConfigureAwait(false);
            var indexedTitle = indexedTitles.GetValueOrDefault(metadata.Id);
            value.Title = IsGenericTitle(indexedTitle, value.FallbackTitle)
                ? value.Activity.TitleHint ?? indexedTitle ?? value.FallbackTitle
                : indexedTitle!;
            var state = value.Activity.VisibleState(startedAt, now);
            if (state == CodexSessionState.Idle)
            {
                continue;
            }

            var version = new SessionVersion(state, value.Activity.UpdatedAt);
            if (dismissed.TryGetValue(metadata.Id, out var dismissedVersion) && dismissedVersion == version)
            {
                continue;
            }

            sessions.Add(new CodexSessionSnapshot(metadata.Id, value.Title, state, value.Activity.UpdatedAt));
        }
        return CodexSessionsSnapshot.Create(sessions, now);
    }

    internal void Dismiss(string threadId)
    {
        if (tracked.TryGetValue(threadId, out var value))
        {
            dismissed[threadId] = new SessionVersion(value.Activity.State, value.Activity.UpdatedAt);
        }
    }

    private IReadOnlyList<RolloutMetadata> EnumerateRecentRollouts()
    {
        if (!Directory.Exists(sessionsRoot))
        {
            return [];
        }

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                MatchCasing = MatchCasing.CaseInsensitive,
            };
            return Directory.EnumerateFiles(sessionsRoot, "rollout-*.jsonl", options)
                .Select(TryCreateMetadata)
                .Where(value => value is not null)
                .OrderByDescending(value => value!.LastWriteTimeUtc)
                .Take(MaximumTrackedSessions)
                .Select(value => value!)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static RolloutMetadata? TryCreateMetadata(string path)
    {
        try
        {
            var match = ThreadIdPattern.Match(Path.GetFileNameWithoutExtension(path));
            return match.Success ? new RolloutMetadata(match.Value, path, File.GetLastWriteTimeUtc(path)) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static async Task<SessionMetadata> ReadSessionMetadataAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 16_384, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, 16_384);
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return SessionMetadata.Default;
            }

            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "session_meta"
                || !root.TryGetProperty("payload", out var payload))
            {
                return SessionMetadata.Default;
            }

            var cwd = payload.TryGetProperty("cwd", out var cwdNode) && cwdNode.ValueKind == JsonValueKind.String
                ? cwdNode.GetString()
                : null;
            var title = string.IsNullOrWhiteSpace(cwd) ? null : Path.GetFileName(Path.TrimEndingDirectorySeparator(cwd));
            var threadSourceIsSubagent = payload.TryGetProperty("thread_source", out var threadSource)
                && threadSource.ValueKind == JsonValueKind.String
                && string.Equals(threadSource.GetString(), "subagent", StringComparison.OrdinalIgnoreCase);
            var sourceIsSubagent = payload.TryGetProperty("source", out var source)
                && source.ValueKind == JsonValueKind.Object
                && source.TryGetProperty("subagent", out _);
            return new SessionMetadata(
                string.IsNullOrWhiteSpace(title) ? "Codex 会话" : Shorten(title),
                threadSourceIsSubagent || sourceIsSubagent);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException or JsonException or ArgumentException)
        {
            return SessionMetadata.Default;
        }
    }

    private static string Shorten(string value) => value.Length <= 48 ? value : value[..47] + "…";

    private static bool IsGenericTitle(string? title, string fallbackTitle) =>
        string.IsNullOrWhiteSpace(title)
        || string.Equals(title, "All", StringComparison.OrdinalIgnoreCase)
        || string.Equals(title, "Codex 会话", StringComparison.Ordinal)
        || string.Equals(title, fallbackTitle, StringComparison.OrdinalIgnoreCase);

    private async Task RefreshTitleIndexAsync(CancellationToken cancellationToken)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(sessionIndexPath);
            if (!info.Exists || info.Length == sessionIndexPosition)
            {
                return;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (info.Length < sessionIndexPosition)
        {
            sessionIndexPosition = 0;
            indexedTitles.Clear();
        }

        try
        {
            await using var stream = new FileStream(sessionIndexPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 16_384, FileOptions.Asynchronous | FileOptions.SequentialScan);
            stream.Seek(sessionIndexPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), sessionIndexPosition == 0, 16_384, leaveOpen: true);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                ApplyTitleIndexLine(line);
            }
            sessionIndexPosition = stream.Position;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
        }
    }

    private void ApplyTitleIndexLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.TryGetProperty("id", out var idNode)
                && idNode.ValueKind == JsonValueKind.String
                && root.TryGetProperty("thread_name", out var titleNode)
                && titleNode.ValueKind == JsonValueKind.String
                && idNode.GetString() is { Length: > 0 } id
                && titleNode.GetString() is { } title
                && !string.IsNullOrWhiteSpace(title))
            {
                indexedTitles[id] = Shorten(title.Trim());
            }
        }
        catch (JsonException)
        {
        }
    }

    private static async Task ReadAppendedLinesAsync(TrackedSession session, CancellationToken cancellationToken)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(session.Path);
            if (!info.Exists || info.Length == session.Position)
            {
                return;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (info.Length < session.Position)
        {
            session.Reset();
        }

        try
        {
            await using var stream = new FileStream(session.Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 16_384, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var initialTailScan = session.Position == 0 && info.Length > InitialScanBytes;
            var start = initialTailScan ? info.Length - InitialScanBytes : session.Position;
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), start == 0, 16_384, leaveOpen: true);
            if (initialTailScan)
            {
                _ = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                session.Activity.ApplyLine(line);
            }
            session.Position = stream.Position;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
        }
    }

    private sealed class TrackedSession(string path, string fallbackTitle, bool isSubagent)
    {
        internal string Path { get; } = path;
        internal string FallbackTitle { get; } = fallbackTitle;
        internal bool IsSubagent { get; } = isSubagent;
        internal string Title { get; set; } = fallbackTitle;
        internal long Position { get; set; }
        internal RolloutActivityTracker Activity { get; private set; } = new();

        internal void Reset()
        {
            Position = 0;
            Activity = new RolloutActivityTracker();
        }
    }

    private sealed record RolloutMetadata(string Id, string Path, DateTime LastWriteTimeUtc);
    private sealed record SessionMetadata(string FallbackTitle, bool IsSubagent)
    {
        internal static SessionMetadata Default { get; } = new("Codex 会话", false);
    }
    private sealed record SessionVersion(CodexSessionState State, DateTimeOffset UpdatedAt);
}
