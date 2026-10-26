namespace CodexQuotaTaskbar.Host.Shortcuts;

public sealed record WebShortcut(string Id, string Name, Uri TargetUri)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ManualIconKey { get; init; }

    public static bool TryNormalizeUrl(string? input, out Uri? uri, out string? error)
    {
        uri = null;
        error = "请输入有效的 http 或 https 网站地址。";
        if (string.IsNullOrWhiteSpace(input) || input.Any(char.IsControl)) return false;
        var text = input.Trim();
        if (text.Length > 2048 || text.Any(char.IsWhiteSpace) || text.Contains('\\')) return false;
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // A bare host gets HTTPS. A declared protocol must not become a different kind of URL.
            if (text.Contains("://", StringComparison.Ordinal) || text.StartsWith("//", StringComparison.Ordinal)) return false;
            var colon = text.IndexOf(':');
            var boundary = text.IndexOfAny(['/', '?', '#']);
            if (colon >= 0 && (boundary < 0 || colon < boundary) && !text.StartsWith('['))
            {
                var host = text[..colon];
                var portEnd = boundary < 0 ? text.Length : boundary;
                var port = text[(colon + 1)..portEnd];
                if ((!host.Contains('.') && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                    || !int.TryParse(port, out var number) || number is < 1 or > 65535) return false;
            }
            text = "https://" + text;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https") || parsed.Host.Length == 0
            || parsed.UserInfo.Length > 0 || parsed.Port is < 1 or > 65535) return false;
        try
        {
            if (Uri.CheckHostName(parsed.IdnHost) == UriHostNameType.Unknown || parsed.IdnHost.Length > 253) return false;
            var canonical = new UriBuilder(parsed)
            {
                Host = parsed.IdnHost.ToLowerInvariant(),
                Scheme = parsed.Scheme.ToLowerInvariant(),
                Port = parsed.IsDefaultPort ? -1 : parsed.Port,
            }.Uri;
            if (canonical.AbsoluteUri.Length > 2048) return false;
            uri = canonical;
            error = null;
            return true;
        }
        catch (UriFormatException) { return false; }
    }

    public static string DisplayNameFor(Uri target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var host = target.IdnHost;
        return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
    }
}

public sealed record WebShortcutsSnapshot(
    IReadOnlyList<WebShortcut> Items,
    string? ErrorMessage = null,
    bool IsLoading = false);
