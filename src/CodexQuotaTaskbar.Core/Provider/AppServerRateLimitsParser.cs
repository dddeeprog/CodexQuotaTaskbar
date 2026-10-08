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

            if (root.TryGetProperty("rateLimitsByLimitId", out var buckets) && buckets.ValueKind == JsonValueKind.Object)
            {
                foreach (var bucket in buckets.EnumerateObject())
                {
                    ParseBucket(bucket.Value, bucket.Name, windows);
                }
            }

            if (!windows.Any(window => string.Equals(window.LimitId, "codex", StringComparison.OrdinalIgnoreCase))
                && root.TryGetProperty("rateLimits", out var fallback)
                && fallback.ValueKind == JsonValueKind.Object)
            {
                ParseBucket(fallback, "codex", windows);
            }

            if (windows.Count == 0)
            {
                throw new AppServerProtocolException("额度响应不包含有效窗口。");
            }

            return QuotaSnapshot.Available(
                windows,
                capturedAt,
                subscriptionPlan,
                extraCredits: ParseCredits(root));
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

    private static void ParseBucket(JsonElement bucket, string fallbackId, ICollection<QuotaWindowSnapshot> destination)
    {
        var limitId = bucket.TryGetProperty("limitId", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString() ?? fallbackId
            : fallbackId;
        ParseWindow(bucket, "primary", QuotaWindowKind.Primary, limitId, destination);
        ParseWindow(bucket, "secondary", QuotaWindowKind.Secondary, limitId, destination);
    }

    private static void ParseWindow(JsonElement bucket, string propertyName, QuotaWindowKind kind, string limitId, ICollection<QuotaWindowSnapshot> destination)
    {
        if (!bucket.TryGetProperty(propertyName, out var window) || window.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("usedPercent", out var usedNode)
            || !usedNode.TryGetDouble(out var used)
            || !double.IsFinite(used) || used is < 0 or > 100
            || !window.TryGetProperty("windowDurationMins", out var durationNode)
            || !durationNode.TryGetInt32(out var duration) || duration <= 0
            || !window.TryGetProperty("resetsAt", out var resetNode)
            || !resetNode.TryGetInt64(out var reset))
        {
            throw new AppServerProtocolException($"额度窗口 {propertyName} 缺少有效字段。");
        }

        destination.Add(new QuotaWindowSnapshot(kind, 100d - used, duration, DateTimeOffset.FromUnixTimeSeconds(reset), limitId));
    }

    private static QuotaCreditsSnapshot? ParseCredits(JsonElement root)
    {
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
