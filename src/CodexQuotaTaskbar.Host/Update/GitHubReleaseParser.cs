using System.Text.Json;

namespace CodexQuotaTaskbar.Host.Update;

internal sealed record GitHubRelease(
    Version Version,
    string Tag,
    Uri DownloadUri,
    string Sha256);

internal static class GitHubReleaseParser
{
    internal const string AssetName = "CodexQuotaTaskbar-win-x64.zip";
    internal const string ExecutableName = "CodexQuotaTaskbar.exe";

    internal static GitHubRelease? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            ReadBoolean(root, "draft") ||
            ReadBoolean(root, "prerelease") ||
            !root.TryGetProperty("tag_name", out var tagNode) ||
            tagNode.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var tag = tagNode.GetString();
        if (tag is null || tag.Length < 2 || tag[0] != 'v' ||
            !Version.TryParse(tag.AsSpan(1), out var version) || version.Build < 0)
        {
            return null;
        }

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (!HasString(asset, "name", AssetName) ||
                !asset.TryGetProperty("browser_download_url", out var urlNode) ||
                urlNode.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(urlNode.GetString(), UriKind.Absolute, out var downloadUri) ||
                downloadUri.Scheme != Uri.UriSchemeHttps ||
                !downloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                !downloadUri.AbsolutePath.StartsWith("/dddeeprog/CodexQuotaTaskbar/releases/download/", StringComparison.Ordinal) ||
                !asset.TryGetProperty("digest", out var digestNode) ||
                digestNode.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var digest = digestNode.GetString();
            if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sha256 = digest[7..];
            if (sha256.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character)))
            {
                continue;
            }

            return new GitHubRelease(version, tag, downloadUri, sha256.ToUpperInvariant());
        }

        return null;
    }

    private static bool ReadBoolean(JsonElement root, string name) =>
        root.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.True;

    private static bool HasString(JsonElement root, string name, string expected) =>
        root.TryGetProperty(name, out var node) &&
        node.ValueKind == JsonValueKind.String &&
        string.Equals(node.GetString(), expected, StringComparison.Ordinal);
}
