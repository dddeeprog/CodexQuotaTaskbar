using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace CodexQuotaTaskbar.Host.Resets;

/// <summary>Reads only the public scheduled-reset status, without any account context.</summary>
public sealed class ResetScheduleClient : IDisposable
{
    internal const int MaximumResponseBytes = 128 * 1024;
    internal static readonly Uri StatusUri = new("https://codex-resets.com/api/v1/status");
    public static Uri SiteUri => CodexResetFeedClient.SiteUri;
    public static TimeSpan PollInterval { get; } = TimeSpan.FromSeconds(60);
    public static TimeSpan FreshnessLifetime { get; } = TimeSpan.FromMinutes(3);
    private readonly HttpClient client;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ResetScheduleSnapshot? lastSuccess;
    private ResetScheduleSnapshot current = ResetScheduleSnapshot.Loading;
    private string? entityTag;
    private DateTimeOffset nextRequestAt;
    private int disposed;

    public ResetScheduleClient() : this(CodexResetFeedClient.CreateHandler(), TimeProvider.System) { }

    internal ResetScheduleClient(HttpMessageHandler handler, TimeProvider? clock = null)
    {
        client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        this.clock = clock ?? TimeProvider.System;
    }

    public async Task<ResetScheduleSnapshot> FetchAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var requestStarted = false;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            var now = clock.GetUtcNow();
            // A waiting concurrent caller reuses the result, rather than queuing another HTTP request.
            if (now < nextRequestAt) return WithCurrentAge(current, now);
            nextRequestAt = now + PollInterval;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(HttpMethod.Get, StatusUri);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.UserAgent.ParseAdd($"CodexQuotaTaskbar/{ProductVersion.Text}");
            if (entityTag is not null) request.Headers.IfNoneMatch.ParseAdd(entityTag);
            // Conditional revalidation follows the public status endpoint; no cache-busting query,
            // no-store header, cookies or account fields are sent.
            requestStarted = true;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            now = clock.GetUtcNow();
            nextRequestAt = now + PollInterval;
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                var retry = response.Headers.RetryAfter?.Date;
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(5);
                if (retry is null)
                    retry = delay > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delay;
                if (retry is { } permittedAt && permittedAt > nextRequestAt) nextRequestAt = permittedAt;
                return Failure("重置预告服务繁忙，请稍后刷新");
            }
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (lastSuccess is null) throw new InvalidDataException("Missing conditional status baseline.");
                // The server has explicitly confirmed this baseline; a 304 has no new JSON timestamp.
                lastSuccess = lastSuccess with { LastUpdatedAt = now, IsStale = false, ErrorMessage = null };
                return current = lastSuccess;
            }
            response.EnsureSuccessStatusCode();
            if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException("Unexpected status response code.");
            var bytes = await ReadJsonAsync(response, timeout.Token).ConfigureAwait(false);
            var parsed = Parse(bytes, clock.GetUtcNow());
            var tag = response.Headers.ETag?.ToString();
            entityTag = tag is { Length: <= 128 } ? tag : null;
            lastSuccess = parsed;
            return current = parsed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation does not schedule a retry. A later, independent caller may start its
            // own read instead of being given a permanently Loading first-call snapshot.
            if (requestStarted) nextRequestAt = clock.GetUtcNow();
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failure("重置预告查询超时，稍后会自动重试");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException or FormatException)
        {
            return Failure("暂时无法获取重置预告");
        }
        finally { gate.Release(); }
    }

    private ResetScheduleSnapshot Failure(string message) => current = lastSuccess is null
        ? ResetScheduleSnapshot.Unavailable(message)
        : lastSuccess with { IsStale = true, ErrorMessage = message };

    private static ResetScheduleSnapshot WithCurrentAge(ResetScheduleSnapshot value, DateTimeOffset now) =>
        value.LastUpdatedAt is { } checkedAt && now - checkedAt >= FreshnessLifetime
            ? value with { IsStale = true, ErrorMessage = value.ErrorMessage ?? "预告数据已过期，等待更新" }
            : value;

    private static async Task<byte[]> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentType?.MediaType != "application/json" ||
            response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new InvalidDataException("Unexpected public status response.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaximumResponseBytes) throw new InvalidDataException("Public status exceeds its limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    internal static ResetScheduleSnapshot Parse(ReadOnlyMemory<byte> json, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
        var root = document.RootElement;
        RequireObject(root);
        var data = Property(root, "data"); RequireObject(data);
        var meta = Property(root, "meta"); RequireObject(meta);
        if (ReadString(meta, "api_version", 8) != "v1") throw new InvalidDataException("Unknown status version.");
        var generated = ReadTime(meta, "generated_at");
        if (generated > now + TimeSpan.FromMinutes(5)) throw new InvalidDataException("Future status generation timestamp.");
        var scheduled = Property(data, "scheduled_reset");
        ResetSchedule? schedule = null;
        if (scheduled.ValueKind != JsonValueKind.Null)
        {
            RequireObject(scheduled);
            var id = ReadString(scheduled, "id", 64);
            if (id.Length == 0 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
                throw new InvalidDataException("Invalid scheduled-reset identity.");
            if (ReadString(scheduled, "status", 24) != "scheduled") throw new InvalidDataException("Invalid scheduled status.");
            var kind = ReadString(scheduled, "reset_type", 16) switch
            {
                "regular" => ResetKind.Regular,
                "banked" => ResetKind.Banked,
                _ => throw new InvalidDataException("Unknown scheduled reset type."),
            };
            var announced = ReadTime(scheduled, "announced_at");
            if (announced > now + TimeSpan.FromMinutes(5)) throw new InvalidDataException("Future announcement timestamp.");
            var deadlineNode = Property(scheduled, "scheduled_for");
            DateTimeOffset? deadline = deadlineNode.ValueKind == JsonValueKind.Null ? null : ReadTime(scheduled, "scheduled_for");
            if (deadline is { } due && due < announced) throw new InvalidDataException("Deadline predates its announcement.");
            _ = ReadString(scheduled, "text", 20000); // Validate the schema without retaining post text.
            var source = Property(scheduled, "source"); RequireObject(source);
            var sourceUri = SiteUri;
            switch (ReadString(source, "type", 16))
            {
                case "x_post":
                    var link = ReadString(source, "url", 256);
                    if (ReadString(source, "author", 32) != "thsottiaux" || link.Contains('\\') ||
                        !Uri.TryCreate(link, UriKind.Absolute, out var uri) || !IsAllowedSource(uri) ||
                        uri.AbsolutePath != $"/thsottiaux/status/{id}" || !id.All(char.IsAsciiDigit))
                        throw new InvalidDataException("Invalid scheduled-reset source.");
                    sourceUri = uri;
                    break;
                case "observed": break; // Never navigate an unvalidated observation URL.
                default: throw new InvalidDataException("Unknown scheduled-reset source.");
            }
            schedule = new(id, kind, announced, deadline, sourceUri);
        }
        var stale = now - generated >= FreshnessLifetime;
        return new(schedule, now, stale, stale ? "来源预告数据较旧，等待更新" : null) { SourceGeneratedAt = generated };
    }

    public static bool IsAllowedSource(Uri uri)
    {
        if (!uri.IsAbsoluteUri) return false;
        if (string.Equals(uri.AbsoluteUri, SiteUri.AbsoluteUri, StringComparison.Ordinal)) return true;
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "x.com" || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        const string prefix = "/thsottiaux/status/";
        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var id = uri.AbsolutePath.AsSpan(prefix.Length);
        if (id.Length is < 1 or > 64) return false;
        foreach (var character in id) if (!char.IsAsciiDigit(character)) return false;
        return true;
    }

    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Missing status object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateObject())
            if (!names.Add(item.Name)) throw new InvalidDataException("Duplicate status field.");
    }

    private static JsonElement Property(JsonElement value, string name) => value.TryGetProperty(name, out var property)
        ? property : throw new InvalidDataException("Missing status field.");

    private static string ReadString(JsonElement value, string name, int maximum)
    {
        var property = Property(value, name);
        if (property.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid status field.");
        string text;
        try { text = property.GetString()!; }
        catch (InvalidOperationException error) { throw new InvalidDataException("Invalid status text encoding.", error); }
        if (text.Length > maximum) throw new InvalidDataException("Oversized status field.");
        return text;
    }

    private static DateTimeOffset ReadTime(JsonElement value, string name)
    {
        var text = ReadString(value, name, 64);
        if (!(text.EndsWith('Z') || text.Length >= 6 && text[^6] is '+' or '-' && text[^3] == ':') ||
            !Property(value, name).TryGetDateTimeOffset(out var timestamp))
            throw new InvalidDataException("A status timestamp must include its time zone.");
        return timestamp;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) client.Dispose();
        // Only async waits are used (no WaitHandle). Keep this managed gate alive while
        // canceled in-flight/waiting calls unwind, so their finally blocks remain safe.
    }
}
