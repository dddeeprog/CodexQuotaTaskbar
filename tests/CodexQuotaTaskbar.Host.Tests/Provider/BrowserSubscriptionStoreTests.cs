using System.Text;
using System.Text.Json;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Host.Provider;

namespace CodexQuotaTaskbar.Host.Tests.Provider;

public sealed class BrowserSubscriptionStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "CodexQuotaTaskbar.BrowserSubscriptionTests", Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(directory, "subscription-web.json");

    [Fact]
    public void Store_round_trips_only_subscription_metadata_and_keeps_accounts_separate()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new BrowserSubscriptionStore(StorePath);
        Assert.True(store.TrySave("account-one", now.AddDays(20), now));
        Assert.True(store.TrySave("account-two", now.AddDays(30), now));

        var reopened = new BrowserSubscriptionStore(StorePath);
        Assert.Equal(now.AddDays(20), reopened.Read("account-one", now)?.ExpiresAt);
        Assert.Equal(now.AddDays(30), reopened.Read("account-two", now)?.ExpiresAt);
        Assert.Null(reopened.Read("account-other", now));
        Assert.Null(reopened.Read(null, now));
        using var json = JsonDocument.Parse(File.ReadAllText(StorePath));
        Assert.All(json.RootElement.EnumerateArray(), node => Assert.Equal(
            ["accountId", "expiresAt", "checkedAt"],
            node.EnumerateObject().Select(property => property.Name).ToArray()));
    }

    [Fact]
    public void Cache_becomes_stale_after_three_minutes_and_disappears_after_expiration()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new BrowserSubscriptionStore(StorePath);
        Assert.True(store.TrySave("account-one", now.AddHours(1), now));

        var fresh = store.Read("account-one", now.AddMinutes(2));
        Assert.NotNull(fresh);
        Assert.False(fresh.IsStale);
        Assert.Equal(SubscriptionExpirationSource.ChatGptWeb, fresh.Source);
        Assert.True(store.Read("account-one", now.AddMinutes(3))!.IsStale);
        Assert.Null(store.Read("account-one", now.AddHours(1)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" account-one")]
    [InlineData("account\none")]
    public void Invalid_account_ids_are_never_saved(string accountId)
    {
        var now = DateTimeOffset.UtcNow;
        var store = new BrowserSubscriptionStore(StorePath);
        Assert.False(store.TrySave(accountId, now.AddDays(10), now));
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void Invalid_dates_are_never_saved()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new BrowserSubscriptionStore(StorePath);
        Assert.False(store.TrySave("account-one", now.AddDays(-1), now));
        Assert.False(store.TrySave("account-one", now.AddYears(11), now));
        Assert.False(store.TrySave("account-one", now.AddDays(10), now.AddHours(1)));
        Assert.False(store.TrySave("account-one", now.AddDays(10), DateTimeOffset.MinValue));
        Assert.False(File.Exists(StorePath));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{\"accountId\":\"account-one\",\"expiresAt\":\"invalid\",\"checkedAt\":\"invalid\"}]")]
    [InlineData("[{\"accountId\":\"account-one\",\"accessToken\":\"must-not-be-read\"}]")]
    public void Invalid_json_or_unexpected_fields_are_ignored(string payload)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(StorePath, payload);
        Assert.Null(new BrowserSubscriptionStore(StorePath).Read("account-one", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Oversized_or_duplicate_account_cache_is_ignored()
    {
        var now = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(directory);
        File.WriteAllText(StorePath, new string(' ', 64 * 1024) + "[]");
        var store = new BrowserSubscriptionStore(StorePath);
        Assert.Null(store.Read("account-one", now));
        var record = new { accountId = "account-one", expiresAt = now.AddDays(20), checkedAt = now };
        File.WriteAllText(StorePath, JsonSerializer.Serialize(new[] { record, record }));
        Assert.Null(store.Read("account-one", now));
    }

    [Fact]
    public async Task Concurrent_writes_preserve_complete_records_for_each_account()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new BrowserSubscriptionStore(StorePath);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
        {
            Assert.True(store.TrySave($"account-{index}", now.AddDays(index + 1), now));
            Assert.NotNull(store.Read($"account-{index}", now));
        })));

        Assert.All(Enumerable.Range(0, 8), index => Assert.Equal(
            now.AddDays(index + 1), store.Read($"account-{index}", now)?.ExpiresAt));
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public void Clear_removes_only_the_stores_exact_file()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new BrowserSubscriptionStore(StorePath);
        Assert.True(store.TrySave("account-one", now.AddDays(20), now));
        var otherPath = Path.Combine(directory, "other.json");
        File.WriteAllText(otherPath, "{}");

        Assert.True(store.Clear());
        Assert.Null(store.Read("account-one", now));
        Assert.False(File.Exists(StorePath));
        Assert.True(File.Exists(otherPath));
        Assert.True(store.Clear());
    }

    [Fact]
    public async Task Service_prefers_matching_web_cache_over_local_claim_and_network()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new BrowserSubscriptionStore(StorePath);
        Assert.True(store.TrySave("account-one", now.AddDays(20), now));
        var credentials = await WriteCredentialsAsync("account-one", now.AddDays(1));
        using var http = new HttpClient(new RejectNetworkHandler());
        using var service = new CodexSubscriptionMetadataService(
            new CodexAuthCredentialReader(credentials), http, () => now.AddMinutes(4), browserStore: store);

        var snapshot = await service.ReadAsync(CancellationToken.None);

        Assert.Equal("account-one", await service.GetCurrentAccountIdAsync(CancellationToken.None));
        Assert.Equal(SubscriptionExpirationSource.ChatGptWeb, snapshot.Source);
        Assert.Equal(now.AddDays(20), snapshot.ExpiresAt);
        Assert.True(snapshot.IsStale);
    }

    [Fact]
    public async Task Service_does_not_apply_web_cache_to_a_different_or_missing_local_account()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new BrowserSubscriptionStore(StorePath);
        Assert.True(store.TrySave("account-one", now.AddDays(20), now));
        var credentials = await WriteCredentialsAsync("account-two", now.AddDays(1));
        using var http = new HttpClient(new RejectNetworkHandler());
        using var service = new CodexSubscriptionMetadataService(
            new CodexAuthCredentialReader(credentials), http, () => now, browserStore: store);

        var snapshot = await service.ReadAsync(CancellationToken.None);
        Assert.Equal(SubscriptionExpirationSource.CodexLogin, snapshot.Source);
        Assert.Equal(now.AddDays(1), snapshot.ExpiresAt);
        File.Delete(credentials);
        Assert.Null(await service.GetCurrentAccountIdAsync(CancellationToken.None));
        Assert.Equal(SubscriptionExpirationAvailability.Unavailable, (await service.ReadAsync(CancellationToken.None)).Availability);
    }

    [Fact]
    public async Task Account_identity_is_reloaded_for_each_query()
    {
        var now = DateTimeOffset.UtcNow;
        var credentials = await WriteCredentialsAsync("account-one", now.AddDays(1));
        using var http = new HttpClient(new RejectNetworkHandler());
        using var service = new CodexSubscriptionMetadataService(new CodexAuthCredentialReader(credentials), http);
        Assert.Equal("account-one", await service.GetCurrentAccountIdAsync(CancellationToken.None));
        await WriteCredentialsAsync("account-two", now.AddDays(2));
        Assert.Equal("account-two", await service.GetCurrentAccountIdAsync(CancellationToken.None));
    }

    private async Task<string> WriteCredentialsAsync(string accountId, DateTimeOffset expiresAt)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "credentials.json");
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["https://api.openai.com/auth"] = new { chatgpt_account_id = accountId, chatgpt_subscription_active_until = expiresAt },
        });
        var token = "synthetic." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { tokens = new { account_id = accountId, id_token = token } }));
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("A valid matched cache/local claim must not query the network.");
    }
}
