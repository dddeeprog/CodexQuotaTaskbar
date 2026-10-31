using CodexQuotaTaskbar.Host.Provider;
using CodexQuotaTaskbar.Core.Quota;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace CodexQuotaTaskbar.Host.Tests.Provider;

public sealed class ResetCreditUseTransportTests
{
    private const string Connection = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static JsonElement Json(string value) { using var d = JsonDocument.Parse(value); return d.RootElement.Clone(); }
    private static JsonElement Account(string workspace = "Synthetic Workspace") => Json(JsonSerializer.Serialize(new
        { account = new { type = "chatgpt", email = "synthetic@example.invalid", workspaceName = workspace } }));
    private static readonly QuotaResetCreditsSnapshot Cards = new(1, null, DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"accountId\":null}")]
    [InlineData("{\"accountId\":1}")]
    [InlineData("{\"accountId\":\"\"}")]
    [InlineData("{\"accountId\":\"has space\"}")]
    public void Strict_binding_rejects_missing_or_invalid_account_id(string limits) =>
        Assert.Null(CodexRateLimitProvider.CreateResetUseContext(Account(), Json(limits), Account(), Connection, Connection, 0, 0, Cards));

    [Theory]
    [InlineData(null, 0, 0, "Synthetic Workspace")]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 0, 0, "Synthetic Workspace")]
    [InlineData(Connection, 0, 1, "Synthetic Workspace")]
    [InlineData(Connection, 0, 0, "Other Workspace")]
    public void Strict_binding_rejects_connection_generation_or_workspace_change(string? afterConnection, long beforeGeneration, long afterGeneration, string workspace) =>
        Assert.Null(CodexRateLimitProvider.CreateResetUseContext(Account(), Json("{\"accountId\":\"synthetic-account\"}"), Account(workspace),
            Connection, afterConnection, beforeGeneration, afterGeneration, Cards));

    [Fact]
    public void Strict_binding_keeps_only_opaque_hash_and_connection_epoch()
    {
        var context = CodexRateLimitProvider.CreateResetUseContext(Account(), Json("{\"accountId\":\"synthetic-account\"}"), Account(), Connection, Connection, 7, 7, Cards);
        Assert.NotNull(context);
        Assert.Equal(64, context.AccountFingerprint.Length);
        Assert.Equal("78572eb4988b2d748a6a0400be9b103bad945b6e5aff8e4bda509e8ba700ac07", context.AccountFingerprint);
        Assert.DoesNotContain("synthetic-account", context.AccountFingerprint);
        Assert.Equal(Connection + ":7", context.ConnectionEpoch);
        Assert.Same(Cards, context.Cards);
        var changedWorkspace = CodexRateLimitProvider.CreateResetUseContext(Account("Other Workspace"), Json("{\"accountId\":\"synthetic-account\"}"),
            Account("Other Workspace"), Connection, Connection, 7, 7, Cards);
        Assert.NotNull(changedWorkspace);
        Assert.NotEqual(context.AccountFingerprint, changedWorkspace.AccountFingerprint);
        Assert.NotNull(context.WorkspaceBinding);
        Assert.NotEqual(context.WorkspaceBinding, changedWorkspace.WorkspaceBinding);
    }

    [Theory]
    [InlineData("requestLock")]
    [InlineData("writeLock")]
    public async Task Actual_client_checks_authorization_after_queue_before_any_bytes(string queueName)
    {
        await using var client = new CodexAppServerClient();
        using var output = new MemoryStream();
        var writer = new StreamWriter(output, new UTF8Encoding(false), 1024, leaveOpen: true);
        typeof(CodexAppServerClient).GetField("input", Fields)!.SetValue(client, writer);
        var queue = (SemaphoreSlim)typeof(CodexAppServerClient).GetField(queueName, Fields)!.GetValue(client)!;
        await queue.WaitAsync();
        var authorized = true;
        var request = client.RequestAsync("account/rateLimitResetCredit/consume", new { idempotencyKey = "synthetic-key" }, CancellationToken.None, () => authorized);
        Assert.False(request.IsCompleted);
        authorized = false;
        queue.Release();
        await Assert.ThrowsAsync<InvalidOperationException>(() => request);
        await writer.FlushAsync();
        Assert.Equal(0, output.Length);
        Assert.Null(typeof(CodexAppServerClient).GetField("process", Fields)!.GetValue(client));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_denies_queued_consume_after_disconnect_or_failed_refresh(bool failedRefresh)
    {
        var fake = new FakeClient();
        await using var provider = Provider(fake);
        var context = await provider.ReadContextAsync(CancellationToken.None);
        Assert.NotNull(context);
        fake.HoldConsume = true;
        var request = provider.ConsumeAsync(context, Guid.NewGuid().ToString("N"), CancellationToken.None);
        await fake.ConsumeEntered.Task;
        if (failedRefresh) { fake.FailReads = true; await provider.RefreshAsync(CancellationToken.None); }
        else fake.LoseConnection();
        fake.ReleaseConsume.TrySetResult(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => request);
        Assert.Empty(fake.Writes);
        Assert.False(fake.Started);
    }

    [Fact]
    public async Task Provider_uses_official_method_single_idempotency_key_and_no_credit_id()
    {
        var fake = new FakeClient();
        await using var provider = Provider(fake);
        var context = await provider.ReadContextAsync(CancellationToken.None);
        Assert.NotNull(context);
        var key = Guid.NewGuid().ToString("N");
        Assert.Equal("reset", await provider.ConsumeAsync(context, key, CancellationToken.None));
        var write = Assert.Single(fake.Writes);
        Assert.Equal("account/rateLimitResetCredit/consume", write.Method);
        var property = Assert.Single(write.Parameters.EnumerateObject());
        Assert.Equal("idempotencyKey", property.Name);
        Assert.Equal(key, property.Value.GetString());
        Assert.False(fake.Started);
    }

    private static CodexRateLimitProvider Provider(FakeClient fake)
    {
        var provider = new CodexRateLimitProvider(subscriptionDetailsEnabled: false, client: fake);
        typeof(CodexRateLimitProvider).GetField("started", Fields)!.SetValue(provider, true);
        return provider;
    }
    private sealed class FakeClient : ICodexAppServerClient
    {
        public string? InitializedConnectionId { get; private set; } = Connection;
        public event EventHandler<string>? Notification { add { } remove { } }
        public event Action? ConnectionLost;
        internal bool Started, HoldConsume, FailReads;
        internal readonly TaskCompletionSource<bool> ConsumeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> ReleaseConsume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<(string Method, JsonElement Parameters)> Writes = [];
        public Task StartAsync(CancellationToken ct) { Started = true; throw new InvalidOperationException("No real start in tests"); }
        public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken ct, Func<bool>? authorizeBeforeSend = null)
        {
            if (method == "account/read") return FailReads ? throw new IOException("synthetic read failure") : Account();
            if (method == "account/rateLimits/read") return Json("""{"accountId":"synthetic-account","rateLimits":{"primary":null,"secondary":null},"rateLimitResetCredits":{"availableCount":2,"credits":null}}""");
            Assert.Equal("account/rateLimitResetCredit/consume", method);
            ConsumeEntered.TrySetResult(true);
            if (HoldConsume) await ReleaseConsume.Task.WaitAsync(ct);
            if (authorizeBeforeSend is null || !authorizeBeforeSend()) throw new InvalidOperationException("Denied before synthetic write");
            Writes.Add((method, JsonSerializer.SerializeToElement(parameters)));
            return Json("{\"outcome\":\"reset\"}");
        }
        internal void LoseConnection() { InitializedConnectionId = null; ConnectionLost?.Invoke(); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
