using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace CodexQuotaTaskbar.Host.Shortcuts;

public sealed record WebShortcutIcon(string? Name, string? CachedPngPath)
{
    public BitmapSource? Image { get; init; }
}

/// <summary>Fetches public site metadata only; bookmark paths and account context never leave the app.</summary>
public sealed class WebShortcutIconService : IDisposable
{
    internal const int MaximumPageBytes = 256 * 1024;
    internal const int MaximumIconBytes = 128 * 1024;
    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan FailureLifetime = TimeSpan.FromMinutes(5);
    private static readonly Regex Tags = new("""<(?:link|base)\b(?:[^>"']|"[^"]*"|'[^']*')*>""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    // Browsers retain '=' inside an unquoted value, including ordinary URL query parameters.
    private static readonly Regex Attributes = new("""(?<name>[^\s=/>]+)(?:\s*=\s*(?:"(?<double>[^"]*)"|'(?<single>[^']*)'|(?<bare>[^\s"'<>`]+)))?""", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Titles = new(@"<title\b[^>]*>(.*?)</title\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex IgnoredMarkup = new(@"<!--.*?-->|<(?:script|style)\b[^>]*>.*?</(?:script|style)\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex HtmlTags = new("<[^>]*>", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private readonly HttpClient client;
    private readonly string cacheDirectory;
    private readonly TimeProvider clock;
    private readonly Func<Uri, CancellationToken, Task<bool>> networkAllowed;
    private readonly SemaphoreSlim downloads = new(2, 2);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private readonly Dictionary<string, Task<WebShortcutIcon>> inFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CachedResult> memory = new(StringComparer.Ordinal);
    private bool disposed;

    public WebShortcutIconService() : this(CreateHandler(),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaTaskbar", "shortcut-icons"),
        TimeProvider.System, ValidateNetworkAsync) { }

    internal WebShortcutIconService(HttpMessageHandler handler, string cacheDirectory, TimeProvider? clock = null,
        Func<Uri, CancellationToken, Task<bool>>? networkAllowed = null)
    {
        client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        this.cacheDirectory = Path.GetFullPath(cacheDirectory);
        this.clock = clock ?? TimeProvider.System;
        this.networkAllowed = networkAllowed ?? ValidateNetworkAsync;
    }

    public async Task<WebShortcutIcon> FetchAsync(Uri target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var origin = Origin(target);
        if (origin is null) return new(null, null);
        var key = origin.AbsoluteUri;
        Task<WebShortcutIcon> task;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (memory.TryGetValue(key, out var cached) && cached.ExpiresAt > clock.GetUtcNow()) return cached.Icon;
            if (!inFlight.TryGetValue(key, out task!))
            {
                if (inFlight.Count >= 64) return new(null, null);
                // DNS, proxy discovery, decoding and file work all start away from the dispatcher.
                task = Task.Run(() => FetchCoreAsync(origin, key, lifetime.Token), CancellationToken.None);
                inFlight.Add(key, task);
                _ = task.ContinueWith(completed =>
                {
                    lock (sync)
                    {
                        inFlight.Remove(key);
                        if (!disposed && completed.IsCompletedSuccessfully)
                        {
                            if (memory.Count >= 64)
                                memory.Remove(memory.MinBy(item => item.Value.ExpiresAt).Key);
                            var result = completed.Result;
                            memory[key] = new(result, clock.GetUtcNow() + (result.Image is null ? FailureLifetime : CacheLifetime));
                        }
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        // Canceling one UI request does not cancel a shared origin lookup for another shortcut.
        try { return await task.WaitAsync(TimeSpan.FromSeconds(9), cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { return new(null, null); }
    }

    private async Task<WebShortcutIcon> FetchCoreAsync(Uri origin, string key, CancellationToken cancellationToken)
    {
        var path = Path.Combine(cacheDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".png");
        var cached = await ReadCacheAsync(path, cancellationToken).ConfigureAwait(false);
        if (cached.Image is not null && IsFreshCache(path)) return cached;
        string? name = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var entered = false;
        try
        {
            await downloads.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            var candidates = new List<Uri>();
            try
            {
                var page = await ReadResourceAsync(origin, "text/html, application/xhtml+xml;q=0.9", MaximumPageBytes, timeout.Token).ConfigureAwait(false);
                if (page.MediaType is "text/html" or "application/xhtml+xml")
                {
                    var metadata = Discover(page.Bytes, page.FinalUri);
                    name = metadata.Name;
                    candidates.AddRange(metadata.Icons);
                }
            }
            catch (Exception error) when (IsRecoverable(error, cancellationToken)) { }

            candidates.Add(new Uri(origin, "/favicon.ico"));
            foreach (var iconUri in candidates.DistinctBy(uri => uri.AbsoluteUri).Take(9))
            {
                if (IsKnownIconProxy(iconUri)) continue;
                try
                {
                    var resource = await ReadResourceAsync(iconUri, "image/png, image/jpeg, image/x-icon, image/vnd.microsoft.icon", MaximumIconBytes, timeout.Token).ConfigureAwait(false);
                    var png = IconImageCodec.ConvertToPng(resource.Bytes, resource.MediaType ?? string.Empty);
                    if (png is null) continue;
                    var image = IconImageCodec.ReadCachedPng(png);
                    if (image is null) continue;
                    string? saved = null;
                    try
                    {
                        Directory.CreateDirectory(cacheDirectory);
                        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try
                        {
                            await File.WriteAllBytesAsync(temporary, png, timeout.Token).ConfigureAwait(false);
                            timeout.Token.ThrowIfCancellationRequested();
                            File.Move(temporary, path, overwrite: true);
                            saved = path;
                        }
                        finally
                        {
                            try { if (File.Exists(temporary)) File.Delete(temporary); }
                            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                    return new(name, saved) { Image = image };
                }
                catch (Exception error) when (IsRecoverable(error, cancellationToken)) { }
            }
        }
        catch (Exception error) when (IsRecoverable(error, cancellationToken)) { }
        finally { if (entered) downloads.Release(); }
        return cached with { Name = name };
    }

    private static bool IsRecoverable(Exception error, CancellationToken lifetimeToken) =>
        error is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or SocketException
            or ArgumentException or NotSupportedException or InvalidOperationException or System.Security.SecurityException or RegexMatchTimeoutException
        || error is OperationCanceledException && !lifetimeToken.IsCancellationRequested;

    private bool IsFreshCache(string path)
    {
        try { return File.Exists(path) && clock.GetUtcNow() - File.GetLastWriteTimeUtc(path) < CacheLifetime; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private async Task<WebShortcutIcon> ReadCacheAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length is > 0 and <= MaximumIconBytes)
            {
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                if (bytes.Length <= MaximumIconBytes && IconImageCodec.ReadCachedPng(bytes) is { } image)
                    return new(null, path) { Image = image };
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return new(null, null);
    }

    private async Task<Resource> ReadResourceAsync(Uri uri, string accept, int maximumBytes, CancellationToken cancellationToken)
    {
        var current = uri;
        for (var hop = 0; hop <= 3; hop++)
        {
            if (!IsFetchUri(current) || IsKnownIconProxy(current) || !await networkAllowed(current, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("Icon metadata is not on a permitted public route.");
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Accept.ParseAdd(accept);
            request.Headers.UserAgent.ParseAdd($"CodexQuotaTaskbar/{ProductVersion.Text}");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                if (hop == 3 || location is null || !Uri.TryCreate(current, location, out var next)
                    || current.Scheme == "https" && next.Scheme != "https")
                    throw new InvalidDataException("Unsupported icon redirect.");
                current = next;
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > maximumBytes)
                throw new InvalidDataException("Icon response is not bounded content.");
            var media = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (maximumBytes == MaximumPageBytes && media is not ("text/html" or "application/xhtml+xml")
                || maximumBytes == MaximumIconBytes && media is not ("image/png" or "image/jpeg" or "image/x-icon" or "image/vnd.microsoft.icon"))
                throw new InvalidDataException("Unsupported site metadata type.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                if (bytes.Length + count > maximumBytes) throw new InvalidDataException("Icon response exceeds its limit.");
                bytes.Write(buffer, 0, count);
            }
            return new(bytes.ToArray(), media, current);
        }
        throw new InvalidDataException("Too many icon redirects.");
    }

    internal static (string? Name, IReadOnlyList<Uri> Icons) Discover(byte[] bytes, Uri page)
    {
        var html = IgnoredMarkup.Replace(Encoding.UTF8.GetString(bytes), string.Empty);
        var title = Titles.Match(html);
        var name = title.Success ? PlainName(title.Groups[1].Value) : null;
        var elements = Tags.Matches(html).Take(128).Select(match => match.Value).ToArray();
        var basis = page;
        foreach (var element in elements.Where(value => value.StartsWith("<base", StringComparison.OrdinalIgnoreCase)))
        {
            var attributes = ReadAttributes(element);
            if (attributes.TryGetValue("href", out var href) && Uri.TryCreate(page, href, out var candidate) && IsFetchUri(candidate)
                && (page.Scheme != "https" || candidate.Scheme == "https")) basis = candidate;
            break;
        }
        var icons = new List<Uri>();
        foreach (var element in elements.Where(value => value.StartsWith("<link", StringComparison.OrdinalIgnoreCase)))
        {
            var attributes = ReadAttributes(element);
            if (!attributes.TryGetValue("rel", out var rel) || !attributes.TryGetValue("href", out var href)) continue;
            var tokens = rel.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (!tokens.Any(token => token.Equals("icon", StringComparison.OrdinalIgnoreCase)
                || token.Equals("apple-touch-icon", StringComparison.OrdinalIgnoreCase)
                || token.Equals("apple-touch-icon-precomposed", StringComparison.OrdinalIgnoreCase))) continue;
            if (attributes.TryGetValue("type", out var type) && type.Contains("svg", StringComparison.OrdinalIgnoreCase)) continue;
            if (Uri.TryCreate(basis, href, out var icon) && IsFetchUri(icon) && (page.Scheme != "https" || icon.Scheme == "https")) icons.Add(icon);
        }
        return (name, icons.DistinctBy(uri => uri.AbsoluteUri).Take(8).ToArray());
    }

    private static Dictionary<string, string> ReadAttributes(string element)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match attribute in Attributes.Matches(element))
        {
            var value = attribute.Groups["double"].Success ? attribute.Groups["double"].Value
                : attribute.Groups["single"].Success ? attribute.Groups["single"].Value : attribute.Groups["bare"].Value;
            result.TryAdd(attribute.Groups["name"].Value, WebUtility.HtmlDecode(value));
        }
        return result;
    }

    private static string? PlainName(string value)
    {
        var text = WebUtility.HtmlDecode(HtmlTags.Replace(value, string.Empty));
        text = string.Join(' ', new string(text.Where(character => !char.IsControl(character) || char.IsWhiteSpace(character)).ToArray())
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0) return null;
        return text.Length <= 80 ? text : text[..79] + "…";
    }

    internal static Uri? Origin(Uri target) => IsFetchUri(target)
        ? new UriBuilder(target.Scheme, target.IdnHost, target.IsDefaultPort ? -1 : target.Port) { Path = "/", Query = string.Empty, Fragment = string.Empty }.Uri
        : null;

    internal static bool IsFetchUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme is "http" or "https"
        && uri.UserInfo.Length == 0 && uri.Host.Length > 0 && uri.Port is 80 or 443 && uri.AbsoluteUri.Length <= 2048
        && !uri.IdnHost.Contains('%') && !uri.OriginalString.Contains('\\') && !uri.OriginalString.Any(char.IsControl);

    private static bool IsKnownIconProxy(Uri icon) =>
        icon.Host is "icons.duckduckgo.com" or "favicon.yandex.net" or "favicon.im" or "faviconever.com"
        || (icon.Host is "www.google.com" or "google.com") && icon.AbsolutePath.StartsWith("/s2/favicons", StringComparison.OrdinalIgnoreCase);

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false, UseCookies = false, Credentials = null,
        Proxy = HttpClient.DefaultProxy, UseProxy = true,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = ConnectPublicAsync,
    };

    internal static async Task<bool> ValidateNetworkAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!IsFetchUri(uri)) return false;
        // Do not bypass a configured proxy. If its final route cannot be pinned safely, use a placeholder.
        var proxy = HttpClient.DefaultProxy;
        if (!proxy.IsBypassed(uri) && proxy.GetProxy(uri) is { } destination && destination.AbsoluteUri != uri.AbsoluteUri) return false;
        var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken).ConfigureAwait(false);
        return addresses.Length > 0 && addresses.All(IsPublicAddress);
    }

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var target = context.InitialRequestMessage.RequestUri;
        if (target is null || !string.Equals(context.DnsEndPoint.Host, target.IdnHost, StringComparison.OrdinalIgnoreCase)
            || context.DnsEndPoint.Port != target.Port)
            throw new HttpRequestException("An icon proxy route cannot be safely pinned.");
        // Resolve again at connection time, then connect to this exact checked address, never re-resolve in Socket.Connect.
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new HttpRequestException("Icon connection is not on a public route.");
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("No public icon route was reachable.");
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return b[0] is > 0 and < 224 && b[0] is not (10 or 127)
                && !(b[0] == 100 && b[1] is >= 64 and <= 127)
                && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31)
                && !(b[0] == 192 && (b[1] == 168 || b[1] == 0 && b[2] is 0 or 2 || b[1] == 88 && b[2] == 99))
                && !(b[0] == 168 && b[1] == 63 && b[2] == 129 && b[3] == 16)
                && !(b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100))
                && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId == 0 && (b[0] & 0xE0) == 0x20
            && !(b[0] == 0x20 && b[1] == 0x01 && (b[2] is 0 or 1 || b[2] == 0x0D && b[3] == 0xB8))
            && !(b[0] == 0x20 && b[1] == 0x02);
    }

    private sealed record Resource(byte[] Bytes, string? MediaType, Uri FinalUri);
    private sealed record CachedResult(WebShortcutIcon Icon, DateTimeOffset ExpiresAt);

    public void Dispose()
    {
        lock (sync) { if (disposed) return; disposed = true; }
        lifetime.Cancel();
        client.Dispose();
        // Gates and lifetime stay valid until existing shared lookups finish unwinding.
    }
}
