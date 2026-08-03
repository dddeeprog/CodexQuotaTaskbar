using System.Text.Json;
using CodexQuotaTaskbar.Core.Sessions;

namespace CodexQuotaTaskbar.Host.Provider;

internal sealed class RolloutActivityTracker
{
    internal static TimeSpan CompletedVisibilityDuration { get; } = TimeSpan.FromSeconds(30);
    private string? failedTurnId;
    private string? pendingInputCallId;

    internal CodexSessionState State { get; private set; } = CodexSessionState.Idle;
    internal DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.MinValue;
    internal string? TitleHint { get; private set; }

    internal void ApplyLine(string line)
    {
        if (!MayAffectState(line))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (!root.TryGetProperty("payload", out var payload)
                || !payload.TryGetProperty("type", out var payloadTypeNode)
                || payloadTypeNode.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var payloadType = payloadTypeNode.GetString();
            var timestamp = ReadTimestamp(root);
            if (root.TryGetProperty("type", out var recordType)
                && recordType.GetString() == "event_msg")
            {
                ApplyEvent(payloadType, payload, timestamp);
                return;
            }

            if (recordType.GetString() == "response_item")
            {
                ApplyResponseItem(payloadType, payload, timestamp);
            }
        }
        catch (JsonException)
        {
        }
    }

    internal CodexSessionState VisibleState(DateTimeOffset monitorStartedAt, DateTimeOffset now) => State switch
    {
        CodexSessionState.Running => State,
        CodexSessionState.Waiting when UpdatedAt >= now.AddHours(-24) => State,
        CodexSessionState.Failed when UpdatedAt >= monitorStartedAt && UpdatedAt >= now.AddHours(-1) => State,
        CodexSessionState.Review when UpdatedAt >= monitorStartedAt && UpdatedAt >= now - CompletedVisibilityDuration => State,
        _ => CodexSessionState.Idle,
    };

    private void ApplyEvent(string? payloadType, JsonElement payload, DateTimeOffset timestamp)
    {
        var turnId = ReadString(payload, "turn_id");
        switch (payloadType)
        {
            case "user_message":
                if (ReadString(payload, "message") is { } message && ExtractTitle(message) is { } title)
                {
                    TitleHint = title;
                }
                break;
            case "task_started":
                failedTurnId = null;
                pendingInputCallId = null;
                Set(CodexSessionState.Running, timestamp);
                break;
            case "exec_approval_request":
            case "apply_patch_approval_request":
                pendingInputCallId = ReadString(payload, "call_id") ?? ReadString(payload, "id");
                Set(CodexSessionState.Waiting, timestamp);
                break;
            case "exec_command_begin":
            case "patch_apply_begin":
                pendingInputCallId = null;
                Set(CodexSessionState.Running, timestamp);
                break;
            case "turn_aborted":
                failedTurnId = turnId;
                pendingInputCallId = null;
                Set(CodexSessionState.Failed, timestamp);
                break;
            case "task_complete":
                pendingInputCallId = null;
                Set(turnId is not null && turnId == failedTurnId ? CodexSessionState.Failed : CodexSessionState.Review, timestamp);
                break;
        }
    }

    private void ApplyResponseItem(string? payloadType, JsonElement payload, DateTimeOffset timestamp)
    {
        if (payloadType is "custom_tool_call" or "function_call")
        {
            var name = ReadString(payload, "name");
            if (name?.EndsWith("request_user_input", StringComparison.OrdinalIgnoreCase) == true)
            {
                pendingInputCallId = ReadString(payload, "call_id") ?? ReadString(payload, "id");
                Set(CodexSessionState.Waiting, timestamp);
            }
            return;
        }

        if (pendingInputCallId is not null
            && payloadType is "custom_tool_call_output" or "function_call_output"
            && string.Equals(ReadString(payload, "call_id"), pendingInputCallId, StringComparison.Ordinal))
        {
            pendingInputCallId = null;
            Set(CodexSessionState.Running, timestamp);
        }
    }

    private void Set(CodexSessionState state, DateTimeOffset timestamp)
    {
        State = state;
        UpdatedAt = timestamp;
    }

    private static bool MayAffectState(string line) =>
        line.Contains("\"user_message\"", StringComparison.Ordinal)
        || line.Contains("\"task_started\"", StringComparison.Ordinal)
        || line.Contains("\"task_complete\"", StringComparison.Ordinal)
        || line.Contains("\"turn_aborted\"", StringComparison.Ordinal)
        || line.Contains("approval_request", StringComparison.Ordinal)
        || line.Contains("exec_command_begin", StringComparison.Ordinal)
        || line.Contains("patch_apply_begin", StringComparison.Ordinal)
        || line.Contains("request_user_input", StringComparison.OrdinalIgnoreCase)
        || line.Contains("tool_call_output", StringComparison.Ordinal);

    private static DateTimeOffset ReadTimestamp(JsonElement root) =>
        root.TryGetProperty("timestamp", out var node)
        && node.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(node.GetString(), out var value)
            ? value
            : DateTimeOffset.UtcNow;

    private static string? ReadString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() : null;

    internal static string? ExtractTitle(string message)
    {
        foreach (var sourceLine in message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = sourceLine.TrimStart('#', '-', '*', ' ');
            if (line.Length == 0
                || line.StartsWith("Files mentioned", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("My request for Codex", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Response annotations", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("<image", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("<response-annotations", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("C:\\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var compact = string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return compact.Length <= 42 ? compact : compact[..41] + "…";
        }
        return null;
    }
}
