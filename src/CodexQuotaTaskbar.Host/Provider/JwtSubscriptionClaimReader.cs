using System.Globalization;
using System.Text.Json;

namespace CodexQuotaTaskbar.Host.Provider;

internal static class JwtSubscriptionClaimReader
{
    private const int MaximumJwtCharacters = 256 * 1024;
    private const int MaximumPayloadCharacters = 128 * 1024;
    private const string OpenAiAuthClaim = "https://api.openai.com/auth";

    internal static bool TryReadSubscriptionActiveUntil(string? token, out DateTimeOffset activeUntil)
    {
        activeUntil = default;
        return TryReadPayload(token, out var document)
            && TryReadAuthClaim(document!, "chatgpt_subscription_active_until", out var node)
            && TryParseTimestamp(node, out activeUntil);
    }

    internal static string? ReadAccountId(string? token)
    {
        if (!TryReadPayload(token, out var document))
        {
            return null;
        }

        using (document)
        {
            if (!document!.RootElement.TryGetProperty(OpenAiAuthClaim, out var auth)
                || auth.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var name in new[] { "chatgpt_account_id", "account_id" })
            {
                if (auth.TryGetProperty(name, out var node)
                    && node.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(node.GetString()))
                {
                    return node.GetString()!.Trim();
                }
            }
        }
        return null;
    }

    private static bool TryReadPayload(string? token, out JsonDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaximumJwtCharacters)
        {
            return false;
        }

        var segments = token.Split('.');
        if (segments.Length < 2 || segments[1].Length is 0 or > MaximumPayloadCharacters)
        {
            return false;
        }

        try
        {
            var payload = segments[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            var bytes = Convert.FromBase64String(payload);
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return true;
            }
            document.Dispose();
            document = null;
            return false;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            document?.Dispose();
            document = null;
            return false;
        }
    }

    private static bool TryReadAuthClaim(JsonDocument document, string name, out JsonElement node)
    {
        using (document)
        {
            if (document.RootElement.TryGetProperty(OpenAiAuthClaim, out var auth)
                && auth.ValueKind == JsonValueKind.Object
                && auth.TryGetProperty(name, out var found))
            {
                node = found.Clone();
                return true;
            }
        }
        node = default;
        return false;
    }

    private static bool TryParseTimestamp(JsonElement node, out DateTimeOffset value)
    {
        value = default;
        if (node.ValueKind == JsonValueKind.String)
        {
            var raw = node.GetString();
            if (DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out value))
            {
                return true;
            }
            if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
            {
                return false;
            }
            return TryParseUnixTimestamp(numeric, out value);
        }

        return node.ValueKind == JsonValueKind.Number
            && node.TryGetInt64(out var timestamp)
            && TryParseUnixTimestamp(timestamp, out value);
    }

    private static bool TryParseUnixTimestamp(long timestamp, out DateTimeOffset value)
    {
        try
        {
            value = timestamp >= 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
                : DateTimeOffset.FromUnixTimeSeconds(timestamp);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            value = default;
            return false;
        }
    }
}
