using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CodexQuotaTaskbar.Core.Quota;

namespace CodexQuotaTaskbar.Host.Provider;

internal sealed class CodexSubscriptionMetadataService : IDisposable
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private static readonly Uri AccountsCheckUri = new("https://chatgpt.com/backend-api/accounts/check/v4-2023-04-27");
    private static readonly Uri SubscriptionsUri = new("https://chatgpt.com/backend-api/subscriptions");
    private readonly CodexAuthCredentialReader credentialReader;
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly Func<DateTimeOffset> clock;
    private readonly TimeSpan requestTimeout;
    private readonly BrowserSubscriptionStore? browserStore;

    internal CodexSubscriptionMetadataService(
        CodexAuthCredentialReader? credentialReader = null,
        HttpClient? client = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? requestTimeout = null,
        BrowserSubscriptionStore? browserStore = null)
    {
        this.credentialReader = credentialReader ?? new CodexAuthCredentialReader();
        ownsClient = client is null;
        this.client = client ?? new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        });
        this.client.Timeout = TimeSpan.FromSeconds(12);
        this.clock = clock ?? (() => DateTimeOffset.Now);
        this.requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(12);
        this.browserStore = browserStore;
    }

    internal async Task<string?> GetCurrentAccountIdAsync(CancellationToken cancellationToken)
    {
        var credentials = await credentialReader.ReadAsync(cancellationToken);
        return credentials is null ? null : ReadAccountId(credentials);
    }

    internal async Task<SubscriptionExpirationSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var checkedAt = clock();
        var credentials = await credentialReader.ReadAsync(cancellationToken);
        if (credentials is null)
        {
            return SubscriptionExpirationSnapshot.Unavailable(
                checkedAt,
                SubscriptionExpirationSource.CodexLogin,
                "登录信息不可读");
        }

        var accountId = ReadAccountId(credentials);
        var browserExpiration = browserStore?.Read(accountId, checkedAt);
        if (browserExpiration is not null)
        {
            return browserExpiration;
        }
        if (TryReadLocalExpiration(credentials, accountId, checkedAt, out var localExpiration))
        {
            return SubscriptionExpirationSnapshot.Available(
                localExpiration,
                credentials.UpdatedAt,
                SubscriptionExpirationSource.CodexLogin);
        }

        if (string.IsNullOrWhiteSpace(credentials.AccessToken))
        {
            return SubscriptionExpirationSnapshot.NotProvided(
                checkedAt,
                SubscriptionExpirationSource.CodexLogin);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(requestTimeout);
            var accountSnapshot = await FetchAccountSnapshotAsync(
                credentials.AccessToken,
                accountId,
                timeout.Token);
            accountId = FirstNonEmpty(accountSnapshot?.AccountId, accountId);
            if (accountSnapshot?.ActiveUntil is { } accountExpiration && accountExpiration > checkedAt)
            {
                return SubscriptionExpirationSnapshot.Available(
                    accountExpiration,
                    checkedAt,
                    SubscriptionExpirationSource.ChatGptSubscription);
            }

            if (accountId is not null)
            {
                var subscriptionExpiration = await FetchSubscriptionExpirationAsync(
                    credentials.AccessToken,
                    accountId,
                    timeout.Token);
                if (subscriptionExpiration is { } expiration)
                {
                    return SubscriptionExpirationSnapshot.Available(
                        expiration,
                        checkedAt,
                        SubscriptionExpirationSource.ChatGptSubscription);
                }
            }

            return SubscriptionExpirationSnapshot.NotProvided(
                checkedAt,
                SubscriptionExpirationSource.ChatGptSubscription);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return SubscriptionExpirationSnapshot.Unavailable(
                checkedAt,
                SubscriptionExpirationSource.ChatGptSubscription,
                exception.StatusCode == HttpStatusCode.Unauthorized ? "登录已失效" : "服务拒绝查询");
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException
            or InvalidDataException
            or JsonException
            or OperationCanceledException
            or FormatException)
        {
            return SubscriptionExpirationSnapshot.Unavailable(
                checkedAt,
                SubscriptionExpirationSource.ChatGptSubscription);
        }
    }

    private async Task<OnlineAccountSnapshot?> FetchAccountSnapshotAsync(
        string accessToken,
        string? preferredAccountId,
        CancellationToken cancellationToken)
    {
        var offsetMinutes = -(int)TimeZoneInfo.Local.GetUtcOffset(clock()).TotalMinutes;
        var uri = new UriBuilder(AccountsCheckUri)
        {
            Query = $"timezone_offset_min={offsetMinutes.ToString(CultureInfo.InvariantCulture)}",
        }.Uri;
        using var document = await SendAsync(
            uri,
            accessToken,
            "/backend-api/accounts/check/v4-2023-04-27",
            cancellationToken);
        var records = CollectAccountRecords(document.RootElement);
        if (records.Count == 0)
        {
            return null;
        }

        var identified = records
            .Select(record => (Record: record.Value, AccountId: ReadAccountId(record, preferredAccountId)))
            .Where(record => record.AccountId is not null)
            .ToArray();
        var selectedAccountId = preferredAccountId ?? identified.FirstOrDefault().AccountId;
        if (selectedAccountId is null
            || preferredAccountId is null && identified.Any(record => !AccountIdsEqual(record.AccountId, selectedAccountId)))
        {
            return null;
        }

        var matchingRecords = identified
            .Where(record => AccountIdsEqual(record.AccountId, selectedAccountId))
            .ToArray();
        if (matchingRecords.Length == 0)
        {
            return null;
        }

        var now = clock();
        var expirations = matchingRecords
            .Select(record => ReadAccountExpiration(record.Record, now))
            .Where(expiration => expiration is not null)
            .Distinct()
            .ToArray();
        // Aliases can repeat the same account. Conflicting dates require the account-scoped lookup.
        return new OnlineAccountSnapshot(selectedAccountId, expirations.Length == 1 ? expirations[0] : null);
    }

    private static DateTimeOffset? ReadAccountExpiration(JsonElement record, DateTimeOffset now)
    {
        var account = record.TryGetProperty("account", out var accountNode)
            && accountNode.ValueKind == JsonValueKind.Object
            ? accountNode
            : record;
        var entitlement = record.TryGetProperty("entitlement", out var entitlementNode)
            && entitlementNode.ValueKind == JsonValueKind.Object
            ? entitlementNode
            : default;
        return TryReadTimestamp(entitlement, "expires_at", out var entitlementExpiration) && entitlementExpiration > now
            ? entitlementExpiration
            : TryReadTimestamp(account, "expires_at", out var accountExpiration) && accountExpiration > now
                ? accountExpiration
                : null;
    }

    private async Task<DateTimeOffset?> FetchSubscriptionExpirationAsync(
        string accessToken,
        string accountId,
        CancellationToken cancellationToken)
    {
        var uri = new UriBuilder(SubscriptionsUri)
        {
            Query = $"account_id={Uri.EscapeDataString(accountId)}",
        }.Uri;
        using var document = await SendAsync(
            uri,
            accessToken,
            "/backend-api/subscriptions",
            cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var identity = ReadAccountIdentity(new AccountRecord(document.RootElement, null), accountId, allowRootId: false);
        if (identity.HasConflictingIds
            || identity.AccountId is not null && !AccountIdsEqual(identity.AccountId, accountId))
        {
            return null;
        }
        var now = clock();
        return TryReadTimestamp(document.RootElement, "active_until", out var activeUntil) && activeUntil > now
            ? activeUntil
            : TryReadTimestamp(document.RootElement, "expires_at", out var expiresAt) && expiresAt > now
                ? expiresAt
                : null;
    }

    private async Task<JsonDocument> SendAsync(
        Uri uri,
        string accessToken,
        string targetPath,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !string.Equals(uri.Host, "chatgpt.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort)
        {
            throw new InvalidOperationException("订阅请求目标不在允许列表中。");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd($"CodexQuotaTaskbar/{ProductVersion.Text}");
        request.Headers.Referrer = new Uri("https://chatgpt.com/");
        request.Headers.TryAddWithoutValidation("x-openai-target-path", targetPath);
        request.Headers.TryAddWithoutValidation("x-openai-target-route", targetPath);
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("ChatGPT 订阅服务暂时不可用。", null, response.StatusCode);
        }
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new InvalidDataException("ChatGPT 订阅响应超过安全上限。");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }
            if (buffer.Length + read > MaximumResponseBytes)
            {
                throw new InvalidDataException("ChatGPT 订阅响应超过安全上限。");
            }
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        buffer.Position = 0;
        return await JsonDocument.ParseAsync(
            buffer,
            new JsonDocumentOptions { MaxDepth = 32 },
            cancellationToken);
    }

    private static IReadOnlyList<AccountRecord> CollectAccountRecords(JsonElement root)
    {
        var records = new List<AccountRecord>();
        var accounts = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("accounts", out var accountsNode)
            ? accountsNode
            : root;
        if (accounts.ValueKind == JsonValueKind.Array)
        {
            records.AddRange(accounts.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object)
                .Select(item => new AccountRecord(item.Clone(), null)));
        }
        else if (accounts.ValueKind == JsonValueKind.Object)
        {
            if (LooksLikeAccountRecord(accounts))
            {
                records.Add(new AccountRecord(accounts.Clone(), null));
            }
            else
            {
                foreach (var property in accounts.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Object)
                    {
                        records.Add(new AccountRecord(property.Value.Clone(), property.Name));
                    }
                }
            }
        }
        return records;
    }

    private static bool LooksLikeAccountRecord(JsonElement node) =>
        node.TryGetProperty("account", out _)
        || node.TryGetProperty("entitlement", out _)
        || node.TryGetProperty("account_id", out _)
        || node.TryGetProperty("id", out _)
        || node.TryGetProperty("chatgpt_account_id", out _)
        || node.TryGetProperty("workspace_id", out _);

    private static string? ReadAccountId(AccountRecord record, string? preferredAccountId) =>
        ReadAccountIdentity(record, preferredAccountId).AccountId;

    private static (string? AccountId, bool HasConflictingIds) ReadAccountIdentity(
        AccountRecord record,
        string? preferredAccountId,
        bool allowRootId = true)
    {
        var account = record.Value.TryGetProperty("account", out var accountNode)
            && accountNode.ValueKind == JsonValueKind.Object
            ? accountNode
            : default;
        var strongIds = new[] { account, record.Value }
            .SelectMany(node => new[] { "account_id", "chatgpt_account_id", "workspace_id" }
                .Select(name => ReadNonEmptyString(node, name)))
            .Where(value => value is not null)
            .ToArray();
        var payloadId = strongIds.FirstOrDefault();
        if (payloadId is not null && strongIds.Any(value => !AccountIdsEqual(value, payloadId)))
        {
            return (null, true);
        }
        payloadId ??= ReadNonEmptyString(account, "id")
            ?? (allowRootId ? ReadNonEmptyString(record.Value, "id") : null);
        var keyId = record.Key is { } key
            && (string.Equals(key, preferredAccountId, StringComparison.Ordinal) || Guid.TryParseExact(key, "D", out _))
            ? key
            : null;
        if (keyId is not null && payloadId is not null && !AccountIdsEqual(keyId, payloadId))
        {
            return (null, true);
        }
        return (payloadId ?? keyId, false);
    }

    private static string? ReadNonEmptyString(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var node)
        && node.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(node.GetString())
            ? node.GetString()!.Trim()
            : null;

    private static bool AccountIdsEqual(string? left, string? right) =>
        string.Equals(left, right, StringComparison.Ordinal)
        || Guid.TryParseExact(left, "D", out var leftGuid)
            && Guid.TryParseExact(right, "D", out var rightGuid)
            && leftGuid == rightGuid;

    private static bool TryReadTimestamp(JsonElement parent, string name, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var node))
        {
            return false;
        }
        if (node.ValueKind == JsonValueKind.String)
        {
            var raw = node.GetString();
            return DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestamp);
        }
        if (node.ValueKind != JsonValueKind.Number || !node.TryGetInt64(out var value))
        {
            return false;
        }
        try
        {
            timestamp = value >= 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.FromUnixTimeSeconds(value);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryReadLocalExpiration(
        CodexAuthCredential credentials,
        string? accountId,
        DateTimeOffset checkedAt,
        out DateTimeOffset expiration)
    {
        foreach (var token in new[] { credentials.IdToken, credentials.AccessToken })
        {
            var claimAccountId = JwtSubscriptionClaimReader.ReadAccountId(token);
            if (accountId is not null && claimAccountId is not null
                && !string.Equals(accountId, claimAccountId, StringComparison.Ordinal))
            {
                continue;
            }
            if (JwtSubscriptionClaimReader.TryReadSubscriptionActiveUntil(token, out expiration)
                && expiration > checkedAt)
            {
                return true;
            }
        }
        expiration = default;
        return false;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? ReadAccountId(CodexAuthCredential credentials) => FirstNonEmpty(
        credentials.AccountId,
        JwtSubscriptionClaimReader.ReadAccountId(credentials.AccessToken),
        JwtSubscriptionClaimReader.ReadAccountId(credentials.IdToken));

    public void Dispose()
    {
        if (ownsClient)
        {
            client.Dispose();
        }
    }

    private sealed record OnlineAccountSnapshot(string? AccountId, DateTimeOffset? ActiveUntil);
    private sealed record AccountRecord(JsonElement Value, string? Key);
}
