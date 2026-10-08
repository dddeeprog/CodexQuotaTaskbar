using System.IO;
using System.Text.Json;

namespace CodexQuotaTaskbar.Host.Provider;

internal static class BrowserSubscriptionProtocol
{
    internal static bool IsChatGptOrigin(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) && uri.Host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase);

    internal static bool CanNavigate(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        new[] { "chatgpt.com", "openai.com", "auth0.com", "google.com", "microsoftonline.com", "live.com", "apple.com", "cloudflare.com" }
            .Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));

    internal static BrowserSubscriptionResult? Parse(string source, string json, string nonce, string accountId, DateTimeOffset now)
    {
        if (!IsChatGptOrigin(source) || json.Length > 4096) return null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var fields = root.EnumerateObject().ToArray();
            if (fields.Length != 5 || fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != 5 ||
                fields.Any(field => field.Name is not ("kind" or "nonce" or "status" or "accountId" or "expiresAt"))) return null;
            if (root.GetProperty("kind").GetString() != "cqtb.subscription" || root.GetProperty("nonce").GetString() != nonce ||
                root.GetProperty("accountId").GetString() != accountId) return null;
            var status = root.GetProperty("status").GetString();
            if (status is not ("ok" or "auth_required" or "verification_required" or "account_mismatch" or "account_unverified" or "not_provided" or "failed")) return null;
            if (status != "ok") return new(status!, null);
            if (!root.GetProperty("expiresAt").TryGetDateTimeOffset(out var expiresAt) || expiresAt <= now || expiresAt > now.AddYears(10)) return null;
            return new(status, expiresAt);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }

    internal static string ReadScript()
    {
        using var stream = typeof(BrowserSubscriptionProtocol).Assembly.GetManifestResourceStream("CodexQuotaTaskbar.Host.Provider.BrowserSubscriptionReader.js")
            ?? throw new InvalidOperationException("订阅读取组件缺失。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

internal sealed record BrowserSubscriptionResult(string Status, DateTimeOffset? ExpiresAt);
