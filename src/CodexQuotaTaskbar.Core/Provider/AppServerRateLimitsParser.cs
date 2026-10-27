using System.Globalization;
using System.Text.Json;
using CodexQuotaTaskbar.Core.Quota;

namespace CodexQuotaTaskbar.Core.Provider;

public sealed class AppServerProtocolException(string message, Exception? inner = null) : Exception(message, inner);

public static class AppServerRateLimitsParser
{
    public static QuotaSnapshot Parse(string json, DateTimeOffset capturedAt, string? subscriptionPlan = null)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            var windows = new List<QuotaWindowSnapshot>();
            var unresolved = new List<(string LimitId, QuotaWindowKind Kind)>();
            var codexSeen = false;

            if (root.TryGetProperty("rateLimitsByLimitId", out var buckets) && buckets.ValueKind == JsonValueKind.Object)
            {
                foreach (var bucket in buckets.EnumerateObject())
                {
                    codexSeen |= ParseBucket(bucket.Value, bucket.Name, windows, unresolved, keyed: true);
                }
            }

            if (!codexSeen
                && root.TryGetProperty("rateLimits", out var fallback)
                && fallback.ValueKind == JsonValueKind.Object)
            {
                codexSeen = ParseBucket(fallback, "codex", windows, unresolved);
            }

            if (!codexSeen && windows.Count == 0)
            {
                throw new AppServerProtocolException("额度响应不包含有效窗口。");
            }
            if (!codexSeen)
            {
                unresolved.Add(("codex", QuotaWindowKind.Primary));
                unresolved.Add(("codex", QuotaWindowKind.Secondary));
            }

            return QuotaSnapshot.Available(
                windows,
                capturedAt,
                subscriptionPlan,
                extraCredits: ParseCredits(root)) with { UnresolvedWindows = unresolved };
        }
        catch (AppServerProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            throw new AppServerProtocolException("额度响应格式不兼容。", exception);
        }
    }

    private static bool ParseBucket(JsonElement bucket, string fallbackId, ICollection<QuotaWindowSnapshot> destination,
        ICollection<(string LimitId, QuotaWindowKind Kind)> unresolved, bool keyed = false)
    {
        if (bucket.ValueKind != JsonValueKind.Object)
        {
            if (IsKnownLimit(fallbackId)) throw new AppServerProtocolException("已知额度桶缺少有效字段。");
            return false;
        }
        var limitId = !keyed && bucket.TryGetProperty("limitId", out var id) && id.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(id.GetString()) ? id.GetString()!
            : fallbackId;
        if (limitId.Length > 128 || string.IsNullOrWhiteSpace(limitId) || limitId.Any(char.IsControl))
            return false;
        var limitName = bucket.TryGetProperty("limitName", out var name) && name.ValueKind == JsonValueKind.String
            && name.GetString() is { Length: > 0 and <= 128 } display && !display.Any(char.IsControl) ? display : null;
        var required = IsKnownLimit(limitId) || IsKnownLimit(fallbackId);
        ParseWindow(bucket, "primary", QuotaWindowKind.Primary, limitId, destination, required, unresolved, limitName);
        ParseWindow(bucket, "secondary", QuotaWindowKind.Secondary, limitId, destination, required, unresolved, limitName);
        return string.Equals(limitId, "codex", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownLimit(string id) => string.Equals(id, "codex", StringComparison.OrdinalIgnoreCase)
        || string.Equals(id, "codex_bengalfox", StringComparison.OrdinalIgnoreCase);

    private static void ParseWindow(JsonElement bucket, string propertyName, QuotaWindowKind kind, string limitId,
        ICollection<QuotaWindowSnapshot> destination, bool required,
        ICollection<(string LimitId, QuotaWindowKind Kind)> unresolved, string? limitName)
    {
        if (!bucket.TryGetProperty(propertyName, out var window))
        {
            unresolved.Add((limitId, kind));
            return;
        }
        if (window.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("usedPercent", out var usedNode)
            || usedNode.ValueKind != JsonValueKind.Number
            || !usedNode.TryGetDouble(out var used)
            || !double.IsFinite(used) || used is < 0 or > 100)
        {
            // Future/unknown meters may use nullable windows that we cannot represent.
            // Ignore only those windows; known Codex/Spark failures still break continuity.
            if (required) throw new AppServerProtocolException($"额度窗口 {propertyName} 缺少有效字段。");
            unresolved.Add((limitId, kind));
            return;
        }

        int? duration = null;
        DateTimeOffset? resets = null;
        var valid = true;
        if (window.TryGetProperty("windowDurationMins", out var durationNode) && durationNode.ValueKind != JsonValueKind.Null)
        {
            if (durationNode.ValueKind == JsonValueKind.Number && durationNode.TryGetInt32(out var minutes) && minutes > 0)
                duration = minutes;
            else valid = false;
        }
        if (window.TryGetProperty("resetsAt", out var resetNode) && resetNode.ValueKind != JsonValueKind.Null)
        {
            if (resetNode.ValueKind == JsonValueKind.Number && resetNode.TryGetInt64(out var seconds)
                && seconds >= DateTimeOffset.MinValue.ToUnixTimeSeconds() && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
                resets = DateTimeOffset.FromUnixTimeSeconds(seconds);
            else valid = false;
        }
        if (!valid)
        {
            if (required) throw new AppServerProtocolException($"额度窗口 {propertyName} 字段格式无效。");
            unresolved.Add((limitId, kind)); return;
        }
        destination.Add(new QuotaWindowSnapshot(kind, 100d - used, duration, resets, limitId, limitName));
    }

    private static QuotaCreditsSnapshot? ParseCredits(JsonElement root)
    {
        if (root.TryGetProperty("rateLimitsByLimitId", out var mapped) && mapped.ValueKind == JsonValueKind.Object
            && mapped.TryGetProperty("codex", out var current))
            return current.ValueKind == JsonValueKind.Object && TryParseCredits(current, out var currentCredits) ? currentCredits : null;
        if (root.TryGetProperty("rateLimits", out var rateLimits)
            && rateLimits.ValueKind == JsonValueKind.Object
            && TryParseCredits(rateLimits, out var credits))
        {
            return credits;
        }

        if (!root.TryGetProperty("rateLimitsByLimitId", out var buckets) || buckets.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var bucket in buckets.EnumerateObject())
        {
            if (bucket.Value.ValueKind != JsonValueKind.Object) continue;
            var limitId = bucket.Value.TryGetProperty("limitId", out var idNode) && idNode.ValueKind == JsonValueKind.String
                ? idNode.GetString()
                : bucket.Name;
            if (string.Equals(limitId, "codex", StringComparison.OrdinalIgnoreCase)
                && TryParseCredits(bucket.Value, out credits))
            {
                return credits;
            }
        }

        return null;
    }

    private static bool TryParseCredits(JsonElement bucket, out QuotaCreditsSnapshot? snapshot)
    {
        snapshot = null;
        if (!bucket.TryGetProperty("credits", out var credits) || credits.ValueKind != JsonValueKind.Object
            || !TryGetBoolean(credits, "hasCredits", out var hasCredits)
            || !TryGetBoolean(credits, "unlimited", out var unlimited))
        {
            return false;
        }

        decimal? balance = null;
        if (credits.TryGetProperty("balance", out var balanceNode) && balanceNode.ValueKind != JsonValueKind.Null)
        {
            var rawBalance = balanceNode.ValueKind switch
            {
                JsonValueKind.String => balanceNode.GetString(),
                JsonValueKind.Number => balanceNode.GetRawText(),
                _ => null,
            };
            if (decimal.TryParse(rawBalance, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedBalance)
                && parsedBalance >= 0)
            {
                balance = parsedBalance;
            }
        }

        snapshot = new QuotaCreditsSnapshot(hasCredits, unlimited, balance);
        return true;
    }

    private static bool TryGetBoolean(JsonElement parent, string propertyName, out bool value)
    {
        if (parent.TryGetProperty(propertyName, out var node)
            && node.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = node.GetBoolean();
            return true;
        }

        value = false;
        return false;
    }
}
