using System.IO;
using System.Text.Json;

namespace CodexQuotaTaskbar.Host.Provider;

internal sealed record CodexAuthCredential(
    string? IdToken,
    string? AccessToken,
    string? AccountId,
    DateTimeOffset UpdatedAt)
{
    public override string ToString() => "CodexAuthCredential { [redacted] }";
}

internal sealed class CodexAuthCredentialReader
{
    private const long MaximumAuthFileBytes = 1024 * 1024;
    private readonly string authFilePath;

    internal CodexAuthCredentialReader(string? authFilePath = null)
    {
        this.authFilePath = Path.GetFullPath(authFilePath ?? ResolveDefaultAuthFilePath());
    }

    internal async Task<CodexAuthCredential?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(authFilePath))
            {
                return null;
            }

            var info = new FileInfo(authFilePath);
            if (info.Length is <= 0 or > MaximumAuthFileBytes
                || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            await using var stream = new FileStream(
                authFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken);
                if (read == 0)
                {
                    break;
                }
                if (buffer.Length + read > MaximumAuthFileBytes)
                {
                    return null;
                }
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            }
            buffer.Position = 0;
            using var document = await JsonDocument.ParseAsync(
                buffer,
                new JsonDocumentOptions { MaxDepth = 24 },
                cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("tokens", out var tokens)
                || tokens.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var idToken = ReadNonEmptyString(tokens, "id_token");
            var accessToken = ReadNonEmptyString(tokens, "access_token");
            if (idToken is null && accessToken is null)
            {
                return null;
            }

            return new CodexAuthCredential(
                idToken,
                accessToken,
                ReadNonEmptyString(tokens, "account_id"),
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException)
        {
            return null;
        }
    }

    private static string ResolveDefaultAuthFilePath()
    {
        var configuredHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var codexHome = string.IsNullOrWhiteSpace(configuredHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : configuredHome.Trim();
        return Path.Combine(codexHome, "auth.json");
    }

    private static string? ReadNonEmptyString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
            ? Normalize(node.GetString())
            : null;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
