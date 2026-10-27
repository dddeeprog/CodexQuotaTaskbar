using System.IO;
using System.Net;
using System.Net.Http;

namespace CodexQuotaTaskbar.Host.Bongo;

// This is a product renderer with a fixed model, not a raw SDK or general-purpose Core mirror.
internal static class BongoOptionalDownload
{
    internal const string AssetName = "DianDao-BongoRuntime-win-x64.zip";
    internal static Uri Address => new($"https://github.com/dddeeprog/CodexQuotaTaskbar/releases/download/v{ProductVersion.Text}/{AssetName}");
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None,
    }) { Timeout = TimeSpan.FromSeconds(60) };

    internal static async Task<byte[]> ReadAfterConsentAsync(CancellationToken token)
    {
        if (!CoreTermsDocument.TryLoadCurrent(out var document) || document is null)
            throw new InvalidOperationException("Core 条款不可用，组件未下载。");
        var store = CoreConsentStore.CreateDefault();
        return await ReadAsync(Client, Address,
            async () => await store.LoadAsync(document, token).ConfigureAwait(false) == CoreConsentState.Accepted, token).ConfigureAwait(false);
    }

    internal static async Task<byte[]> ReadAsync(HttpClient client, Uri address, Func<Task<bool>> hasConsent, CancellationToken token)
    {
        if (address != Address) throw new InvalidDataException("只能下载本版本指定的可选组件。");
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            token.ThrowIfCancellationRequested();
            if (!await hasConsent().ConfigureAwait(false)) throw new InvalidOperationException("未同意 Core 条款，组件未下载。");
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.UserAgent.ParseAdd("CodexQuotaTaskbar/" + ProductVersion.Text);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                if (response.Headers.Location is not { } location) throw new InvalidDataException("组件下载重定向无效。");
                address = location.IsAbsoluteUri ? location : new Uri(address, location);
                if (address.Scheme != "https" || !address.IsDefaultPort || address.UserInfo.Length != 0
                    || address.Host is not ("github.com" or "release-assets.githubusercontent.com"))
                    throw new InvalidDataException("组件下载目标不是受支持的 GitHub 发布资源。");
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > BongoPayload.MaximumPayloadBytes or 0)
                throw new InvalidDataException("组件下载大小无效。");
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                if (output.Length + read > BongoPayload.MaximumPayloadBytes) throw new InvalidDataException("组件下载超过大小限制。");
                output.Write(buffer, 0, read);
            }
            if (!await hasConsent().ConfigureAwait(false)) throw new InvalidOperationException("Core 选择已变更，下载内容不会安装。");
            return output.ToArray(); // BongoPayload verifies pinned archive and every file before publishing the cache.
        }
        throw new InvalidDataException("组件下载重定向过多。");
    }
}
