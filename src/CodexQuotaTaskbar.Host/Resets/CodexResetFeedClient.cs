using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CodexQuotaTaskbar.Host.Resets;

/// <summary>Reads the public, unauthenticated Codex Resets feed independently of quota providers.</summary>
public sealed class CodexResetFeedClient : IDisposable
{
    internal const int MaximumResponseBytes = 128 * 1024;
    internal const int MaximumTranslationResponseBytes = 512 * 1024;
    internal static readonly Uri FeedUri = new("https://codex-resets.com/api/v1/resets?limit=3&order=desc");
    internal static readonly Uri TranslationUri = new("https://codex-resets.com/api/resets?locale=zh-CN");
    public static Uri SiteUri { get; } = new("https://codex-resets.com/zh-CN");
    private readonly HttpClient client;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ResetFeedSnapshot? lastSuccess;
    private string? entityTag;
    private DateTimeOffset retryAfter;
    private DateTimeOffset translationRetryAfter;

    public CodexResetFeedClient() : this(CreateHandler(), TimeProvider.System)
    {
    }

    internal CodexResetFeedClient(HttpMessageHandler handler, TimeProvider? clock = null)
    {
        client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        this.clock = clock ?? TimeProvider.System;
    }

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromSeconds(8),
    };

    public async Task<ResetFeedSnapshot> FetchAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (clock.GetUtcNow() < retryAfter)
            {
                return Failure("重置公告服务繁忙，请稍后刷新");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(HttpMethod.Get, FeedUri);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.UserAgent.ParseAdd($"CodexQuotaTaskbar/{ProductVersion.Text}");
            if (entityTag is not null)
            {
                request.Headers.IfNoneMatch.ParseAdd(entityTag);
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified && lastSuccess is not null)
            {
                lastSuccess = await LocalizeAsync(lastSuccess with { LastUpdatedAt = clock.GetUtcNow() }, cancellationToken).ConfigureAwait(false);
                return lastSuccess;
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var delay = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date - clock.GetUtcNow()) ?? TimeSpan.FromMinutes(15);
                // Respect rate limiting even if the refresh button is pressed repeatedly.
                retryAfter = clock.GetUtcNow() + TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 60, 86400));
                return Failure("重置公告服务繁忙，请稍后刷新");
            }

            response.EnsureSuccessStatusCode();
            var bytes = await ReadJsonAsync(response, MaximumResponseBytes, timeout.Token).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            var announcements = Parse(bytes, now);
            lastSuccess = await LocalizeAsync(new ResetFeedSnapshot(announcements, now, false, null), cancellationToken).ConfigureAwait(false);
            var tag = response.Headers.ETag?.ToString();
            entityTag = tag is { Length: <= 128 } ? tag : null;
            return lastSuccess;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure("重置公告查询超时，稍后会自动重试");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or JsonException or FormatException)
        {
            return Failure("暂时无法获取重置公告");
        }
        finally
        {
            gate.Release();
        }
    }

    private ResetFeedSnapshot Failure(string message) => lastSuccess is null
        ? ResetFeedSnapshot.Unavailable(message)
        : lastSuccess with { IsStale = true, ErrorMessage = message };

    // The site's own Chinese endpoint is an optional enhancement, not the source of reset facts.
    private async Task<ResetFeedSnapshot> LocalizeAsync(ResetFeedSnapshot snapshot, CancellationToken cancellationToken)
    {
        var retained = snapshot with
        {
            Announcements = Array.AsReadOnly(snapshot.Announcements.Select(item =>
            {
                var previous = lastSuccess?.Announcements.FirstOrDefault(old => old.Id == item.Id &&
                    old.AnnouncedAt == item.AnnouncedAt && old.Kind == item.Kind && old.Text == item.Text &&
                    old.SourceUri == item.SourceUri && old.IsObserved == item.IsObserved);
                return item with { ChineseText = previous?.ChineseText ?? item.ChineseText };
            }).ToArray()),
        };
        if (retained.Announcements.Count == 0 || clock.GetUtcNow() < translationRetryAfter) return retained;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(HttpMethod.Get, TranslationUri);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.UserAgent.ParseAdd($"CodexQuotaTaskbar/{ProductVersion.Text}");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var delay = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date - clock.GetUtcNow()) ?? TimeSpan.FromMinutes(15);
                translationRetryAfter = clock.GetUtcNow() + TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 60, 86400));
                return retained;
            }
            response.EnsureSuccessStatusCode();
            var bytes = await ReadJsonAsync(response, MaximumTranslationResponseBytes, timeout.Token).ConfigureAwait(false);
            return retained with { Announcements = ResetAnnouncementLocalization.Apply(bytes, retained.Announcements) };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return retained;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or JsonException or FormatException)
        {
            return retained;
        }
    }

    private static async Task<byte[]> ReadJsonAsync(HttpResponseMessage response, int maximumBytes, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentType?.MediaType != "application/json" ||
            response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException("Unexpected public feed response.");
        }
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (bytes.Length + count > maximumBytes) throw new InvalidDataException("Public feed response exceeds its limit.");
            bytes.Write(buffer, 0, count);
        }
        return bytes.ToArray();
    }

    internal static IReadOnlyList<ResetAnnouncement> Parse(ReadOnlyMemory<byte> utf8Json, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions { MaxDepth = 12 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Public feed data is missing.");
        }

        var announcements = new List<ResetAnnouncement>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid reset announcement.");
            var id = ReadString(item, "id");
            var kind = ReadString(item, "reset_type") switch
            {
                "regular" => ResetKind.Regular,
                "banked" => ResetKind.Banked,
                _ => throw new InvalidDataException("Unknown reset type."),
            };
            if (id.Length is < 1 or > 64 || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_') ||
                !item.TryGetProperty("announced_at", out var timestamp) || timestamp.ValueKind != JsonValueKind.String ||
                !timestamp.TryGetDateTimeOffset(out var announcedAt) || !HasTimeZone(timestamp.GetString()!))
            {
                throw new InvalidDataException("Invalid reset identity or timestamp.");
            }
            // An announcement with a future timestamp is not an executed reset.
            if (announcedAt > now) continue;
            if (!item.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Reset source is missing.");
            }

            var sourceType = ReadString(source, "type");
            var isObserved = sourceType == "observed";
            var sourceUri = SiteUri;
            if (sourceType == "x_post")
            {
                if (ReadString(source, "author") != "thsottiaux" ||
                    !Uri.TryCreate(ReadString(source, "url"), UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps || uri.Host != "x.com" || uri.Port != 443 ||
                    uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                    uri.AbsolutePath != $"/thsottiaux/status/{id}" || !id.All(char.IsAsciiDigit))
                {
                    throw new InvalidDataException("Invalid reset source link.");
                }
                sourceUri = uri;
            }
            else if (!isObserved)
            {
                throw new InvalidDataException("Unknown reset source.");
            }

            announcements.Add(new ResetAnnouncement(id, kind, announcedAt, Preview(ReadString(item, "text")), sourceUri, isObserved));
        }

        return Array.AsReadOnly(announcements.OrderByDescending(item => item.AnnouncedAt)
            .DistinctBy(item => item.Id).Take(3).ToArray());
    }

    private static string ReadString(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("A public feed field is missing.");
        try
        {
            return value.GetString()!;
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException("A public feed field contains invalid Unicode.", exception);
        }
    }

    private static bool HasTimeZone(string timestamp) => timestamp.EndsWith('Z') ||
        (timestamp.Length >= 6 && timestamp[^6] is '+' or '-' && timestamp[^3] == ':');

    internal static string Preview(string text)
    {
        var preview = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format) continue;
            var value = Rune.IsWhiteSpace(rune) ? " " : rune.ToString();
            if (Rune.IsControl(rune) && value != " ") continue;
            if (value == " " && (preview.Length == 0 || preview[^1] == ' ')) continue;
            if (preview.Length + value.Length > 239)
            {
                preview.Append('…');
                break;
            }
            preview.Append(value);
        }
        return preview.ToString().Trim();
    }

    public void Dispose() => client.Dispose();
}
