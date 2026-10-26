using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexQuotaTaskbar.Host.Lifetime;

internal static class DotInlineWire
{
    internal const int MaximumCommandCharacters = 64 * 1024;
    internal const int MaximumStateCharacters = 4 * 1024 * 1024;
    internal const int MaximumDraftCharacters = 8000;
    private static readonly JsonSerializerOptions Options = new()
    {
        MaxDepth = 12,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    internal static T? Deserialize<T>(string value)
    {
        try { return JsonSerializer.Deserialize<T>(value, Options); }
        catch (JsonException) { return default; }
        catch (NotSupportedException) { return default; }
    }

    internal static bool ValidAction(DotInlineAction action) => action.DraftRevision >= 0 && action.Count >= 0
        && action.OriginalDraftRevision is null or >= 0 && (action.Kind switch
    {
        "draft" => action.Text is { Length: <= MaximumDraftCharacters } && action.MessageId is null && action.RequestId is null,
        "send" => action.Text is { Length: <= MaximumDraftCharacters } && action.MessageId is null
            && Guid.TryParseExact(action.RequestId, "N", out _),
        "read" => action.MessageId is { Length: > 0 and <= 256 } && action.Text is null && action.RequestId is null,
        "page" => action.Text is null && action.MessageId is null && action.RequestId is null,
        "reviewed_sent" or "reviewed_not_sent" => action.Text is { Length: <= MaximumDraftCharacters }
            && action.MessageId is null && (action.RequestId is null || Guid.TryParseExact(action.RequestId, "N", out _)),
        "earlier-read" => action.Text is null && action.MessageId is null && action.RequestId is null && action.Count > 0,
        "ack-notice" => action.Text is null && action.RequestId is null && action.Count == 0 && action.DraftRevision == 0
            && action.OriginalDraftRevision is null && (action.MessageId is null or { Length: > 0 and <= 256 })
            && Guid.TryParseExact(action.ReadToken, "N", out _) && Guid.TryParseExact(action.ReadSessionId, "N", out _)
            && action.ReadSequence > 0,
        _ => false,
    });

    internal static bool ValidState(DotInlineState state)
    {
        if (!Guid.TryParseExact(state.SessionId, "N", out _) || state.Sequence < 1 || state.ClientDraftRevision < 0
            || state.DraftText is not { Length: <= MaximumDraftCharacters } || state.Messages is not { Length: <= 64 }
            || state.UnreadCount < 0 || state.EarlierUnreadCount < 0 || state.NotificationSerial < 0
            || state.NotificationId is { Length: > 256 } || state.Status is not { Length: <= 512 }) return false;
        if (state.AttemptState is not { Length: <= 32 }
            || state.LastRequestId is not null && !Guid.TryParseExact(state.LastRequestId, "N", out _)) return false;
        if (state.ReadSequence < 0 || state.ReadToken is not null
            && (!Guid.TryParseExact(state.ReadToken, "N", out _) || state.ReadSequence < 1)) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        return state.Messages.All(message => message is not null && message.Id is { Length: > 0 and <= 256 }
            && ids.Add(message.Id) && message.Text is { Length: <= MaximumDraftCharacters });
    }
}

// StreamReader.ReadLineAsync has no length bound. This reader stops before a
// malformed child/parent line can allocate unbounded memory, preserving chunks.
internal sealed class DotInlineLineReader(TextReader input, int maximumCharacters)
{
    private readonly char[] buffer = new char[4096];
    private int offset, count;

    internal async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var result = new StringBuilder(Math.Min(4096, maximumCharacters));
        while (true)
        {
            if (offset == count)
            {
                count = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                offset = 0;
                if (count == 0) return result.Length == 0 ? null : result.ToString();
            }
            var end = Array.IndexOf(buffer, '\n', offset, count - offset);
            var length = (end < 0 ? count : end) - offset;
            if ((long)result.Length + length > maximumCharacters)
                throw new InvalidDataException("Dot pipe line exceeded its bound.");
            result.Append(buffer, offset, length);
            offset += length;
            if (end < 0) continue;
            offset++;
            if (result.Length > 0 && result[^1] == '\r') result.Length--;
            return result.ToString();
        }
    }
}
