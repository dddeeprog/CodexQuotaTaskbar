using System.Text.Json;
using CodexQuotaTaskbar.Host.Provider;

namespace CodexQuotaTaskbar.Host.Tests.Provider;

public sealed class BrowserSubscriptionProtocolTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-08T08:00:00Z");
    private const string Nonce = "2cf1662743dd4455b143937d37d2d6c8f";
    private const string Account = "account-current";

    [Theory]
    [InlineData("https://chatgpt.com/")]
    [InlineData("https://CHATGPT.COM/auth/login")]
    [InlineData("https://chatgpt.com:443/?source=login")]
    public void Exact_https_chatgpt_origin_is_accepted(string address)
    {
        Assert.True(BrowserSubscriptionProtocol.IsChatGptOrigin(address));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://chatgpt.com/")]
    [InlineData("https://chatgpt.com:8443/")]
    [InlineData("https://chatgpt.com.example.com/")]
    [InlineData("https://sub.chatgpt.com/")]
    [InlineData("https://user@chatgpt.com/")]
    [InlineData("https://chatgpt.com@example.com/")]
    [InlineData("https://chatgpt.com./")]
    [InlineData("file:///C:/chatgpt.com/index.html")]
    public void Untrusted_or_ambiguous_origins_are_rejected(string? address)
    {
        Assert.False(BrowserSubscriptionProtocol.IsChatGptOrigin(address));
    }

    [Theory]
    [InlineData("https://chatgpt.com/auth/login")]
    [InlineData("https://auth.openai.com/log-in")]
    [InlineData("https://tenant.auth0.com/authorize")]
    [InlineData("https://accounts.google.com/signin")]
    [InlineData("https://login.microsoftonline.com/")]
    [InlineData("https://login.live.com/")]
    [InlineData("https://appleid.apple.com/auth/authorize")]
    [InlineData("https://challenges.cloudflare.com/")]
    public void Supported_https_login_sites_can_navigate(string address)
    {
        Assert.True(BrowserSubscriptionProtocol.CanNavigate(address));
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("https://notgoogle.com/")]
    [InlineData("https://openai.com.attacker.example/")]
    [InlineData("https://attacker.example/?next=https://chatgpt.com/")]
    [InlineData("https://user@auth.openai.com/")]
    [InlineData("https://accounts.google.com:444/")]
    [InlineData("http://auth.openai.com/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/login.html")]
    [InlineData("ms-settings:privacy")]
    public void Navigation_does_not_allow_unrelated_hosts_or_external_protocols(string address)
    {
        Assert.False(BrowserSubscriptionProtocol.CanNavigate(address));
    }

    [Fact]
    public void Matched_message_returns_only_a_valid_future_date()
    {
        var result = Parse(Message());

        Assert.NotNull(result);
        Assert.Equal("ok", result.Status);
        Assert.Equal(Now.AddDays(30), result.ExpiresAt);
    }

    [Theory]
    [InlineData("https://auth.openai.com/")]
    [InlineData("https://chatgpt.com.attacker.example/")]
    [InlineData("https://user@chatgpt.com/")]
    [InlineData("http://chatgpt.com/")]
    public void Message_source_must_be_the_exact_chatgpt_origin(string source)
    {
        Assert.Null(BrowserSubscriptionProtocol.Parse(source, Message(), Nonce, Account, Now));
    }

    [Fact]
    public void Nonce_and_account_are_exact_case_sensitive_matches()
    {
        Assert.Null(Parse(Message(nonce: "previous-request")));
        Assert.Null(Parse(Message(accountId: "account-other")));
        Assert.Null(Parse(Message(accountId: "Account-current")));
    }

    [Theory]
    [InlineData("auth_required")]
    [InlineData("verification_required")]
    [InlineData("account_mismatch")]
    [InlineData("account_unverified")]
    [InlineData("not_provided")]
    [InlineData("failed")]
    public void Recognized_failure_states_do_not_contain_expiration(string status)
    {
        var result = Parse(Message(status: status, expiresAt: null));

        Assert.NotNull(result);
        Assert.Equal(status, result.Status);
        Assert.Null(result.ExpiresAt);
    }

    [Fact]
    public void Unknown_states_and_wrong_message_kinds_are_rejected()
    {
        Assert.Null(Parse(Message(status: "unexpected")));
        Assert.Null(Parse(Message().Replace("cqtb.subscription", "cqtb.other", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("2026-10-08T08:00:00Z")]
    [InlineData("2025-10-08T08:00:00Z")]
    [InlineData("2037-10-08T08:00:00Z")]
    [InlineData("not-a-date")]
    public void Invalid_expired_or_implausibly_distant_dates_are_rejected(string expiresAt)
    {
        Assert.Null(Parse(Message(expiresAt: expiresAt)));
    }

    [Fact]
    public void Missing_extra_duplicate_and_wrongly_typed_fields_are_rejected()
    {
        Assert.Null(Parse("{}"));
        Assert.Null(Parse("null"));
        Assert.Null(Parse("[]"));
        Assert.Null(Parse(Message().Replace("\"kind\":\"cqtb.subscription\",", "", StringComparison.Ordinal)));
        Assert.Null(Parse(Message().Insert(1, "\"extra\":\"discard-this\",")));
        Assert.Null(Parse(Message().Insert(1, "\"nonce\":\"duplicate\",")));
        Assert.Null(Parse(Message().Replace($"\"{Nonce}\"", "42", StringComparison.Ordinal)));
        Assert.Null(Parse(Message().Replace($"\"{Account}\"", "{}", StringComparison.Ordinal)));
        Assert.Null(Parse(Message(expiresAt: null, status: "ok")));
        Assert.Null(Parse(Message(expiresAt: 1_900_000_000)));
    }

    [Fact]
    public void Oversized_or_deeply_nested_messages_are_rejected()
    {
        Assert.Null(Parse(new string(' ', 4097) + Message()));
        Assert.Null(Parse("{\"outer\":{\"inner\":{\"next\":{\"last\":{\"value\":1}}}}}"));
    }

    [Fact]
    public void Embedded_reader_script_is_available_to_the_window()
    {
        var script = BrowserSubscriptionProtocol.ReadScript();

        Assert.Contains("cqtb.readSubscription", script, StringComparison.Ordinal);
        Assert.Contains("cqtb.subscription", script, StringComparison.Ordinal);
    }

    private static BrowserSubscriptionResult? Parse(string json) =>
        BrowserSubscriptionProtocol.Parse("https://chatgpt.com/", json, Nonce, Account, Now);

    private static string Message(string nonce = Nonce, string accountId = Account, string status = "ok") =>
        Message(Now.AddDays(30), nonce, accountId, status);

    private static string Message(object? expiresAt, string nonce = Nonce, string accountId = Account, string status = "ok") =>
        JsonSerializer.Serialize(new { kind = "cqtb.subscription", nonce, status, accountId, expiresAt });
}
