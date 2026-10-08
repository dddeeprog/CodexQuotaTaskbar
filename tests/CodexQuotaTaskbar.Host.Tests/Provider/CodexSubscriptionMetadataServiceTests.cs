using System.Net;
using System.Text;
using System.Text.Json;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Host.Provider;

namespace CodexQuotaTaskbar.Host.Tests.Provider;

public sealed class CodexSubscriptionMetadataServiceTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "CodexQuotaTaskbar.SubscriptionTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Jwt_parser_reads_subscription_claim_but_never_uses_token_expiration()
    {
        var subscription = CreateJwt(new
        {
            exp = 1_900_000_000,
            auth = new Dictionary<string, object?>
            {
                ["chatgpt_subscription_active_until"] = "2030-01-02T03:04:05Z",
            },
        });
        var tokenOnly = CreateJwt(new { exp = 1_900_000_000 });

        Assert.True(JwtSubscriptionClaimReader.TryReadSubscriptionActiveUntil(subscription, out var activeUntil));
        Assert.Equal(DateTimeOffset.Parse("2030-01-02T03:04:05Z"), activeUntil);
        Assert.False(JwtSubscriptionClaimReader.TryReadSubscriptionActiveUntil(tokenOnly, out _));
        Assert.False(JwtSubscriptionClaimReader.TryReadSubscriptionActiveUntil("not-a-jwt", out _));
    }

    [Fact]
    public async Task Credential_reader_reads_only_bounded_official_token_fields()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "credentials.json");
        await File.WriteAllTextAsync(path, """
            { "auth_mode": "chatgpt", "tokens": { "id_token": "id-value", "access_token": "access-value", "refresh_token": "must-not-be-returned", "account_id": "acc-1" } }
            """);
        var reader = new CodexAuthCredentialReader(path);

        var result = await reader.ReadAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("id-value", result.IdToken);
        Assert.Equal("access-value", result.AccessToken);
        Assert.Equal("acc-1", result.AccountId);
        Assert.DoesNotContain("refresh", result.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("id-value", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("access-value", result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"not-an-object\"")]
    [InlineData("{}")]
    [InlineData("{\"tokens\":[]}")]
    [InlineData("{\"tokens\":{}}")]
    public async Task Invalid_or_missing_credential_fields_return_null(string json)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "credentials.json");
        await File.WriteAllTextAsync(path, json);

        var result = await new CodexAuthCredentialReader(path).ReadAsync(CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Missing_credential_file_returns_null()
    {
        var path = Path.Combine(directory, "does-not-exist.json");

        var result = await new CodexAuthCredentialReader(path).ReadAsync(CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Oversized_credential_file_returns_null()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "credentials.json");
        await File.WriteAllTextAsync(path, new string(' ', 1024 * 1024) + "{}");

        var result = await new CodexAuthCredentialReader(path).ReadAsync(CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Future_local_claim_avoids_private_network_request()
    {
        var now = DateTimeOffset.Parse("2026-10-08T08:00:00Z");
        var authPath = await WriteAuthAsync(
            CreateJwt(new
            {
                auth = new Dictionary<string, object?>
                {
                    ["chatgpt_subscription_active_until"] = "2026-11-08T08:00:00Z",
                },
            }),
            CreateJwt(new { }));
        var handler = new RouterHandler(_ => throw new Xunit.Sdk.XunitException("Local claim must not use the network."));
        using var http = new HttpClient(handler);
        using var service = new CodexSubscriptionMetadataService(
            new CodexAuthCredentialReader(authPath),
            http,
            () => now);

        var result = await service.ReadAsync(CancellationToken.None);

        Assert.Equal(SubscriptionExpirationAvailability.Available, result.Availability);
        Assert.Equal(SubscriptionExpirationSource.CodexLogin, result.Source);
        Assert.Equal(DateTimeOffset.Parse("2026-11-08T08:00:00Z"), result.ExpiresAt);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Online_account_check_matches_the_current_account_exactly()
    {
        var now = DateTimeOffset.Parse("2026-10-08T08:00:00Z");
        var authPath = await WriteAuthAsync(
            CreateJwt(new { }),
            CreateJwt(new
            {
                auth = new Dictionary<string, object?> { ["chatgpt_account_id"] = "acc-current" },
            }),
            "acc-current");
        var handler = new RouterHandler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("/backend-api/accounts/check/v4-2023-04-27", request.Headers.GetValues("x-openai-target-path").Single());
            return JsonResponse("""
                { "accounts": [
                  { "account": { "account_id": "acc-other" }, "entitlement": { "expires_at": "2099-01-01T00:00:00Z" } },
                  { "account": { "account_id": "acc-current" }, "entitlement": { "expires_at": "2026-12-31T00:00:00Z" } }
                ] }
                """);
        });
        using var http = new HttpClient(handler);
        using var service = new CodexSubscriptionMetadataService(
            new CodexAuthCredentialReader(authPath),
            http,
            () => now);

        var result = await service.ReadAsync(CancellationToken.None);

        Assert.Equal(SubscriptionExpirationAvailability.Available, result.Availability);
        Assert.Equal(SubscriptionExpirationSource.ChatGptSubscription, result.Source);
        Assert.Equal(DateTimeOffset.Parse("2026-12-31T00:00:00Z"), result.ExpiresAt);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task A_single_nonmatching_account_cannot_supply_the_current_accounts_expiration()
    {
        var now = DateTimeOffset.Parse("2026-10-08T08:00:00Z");
        var authPath = await WriteAuthAsync(CreateJwt(new { }), CreateJwt(new { }), "acc-current");
        var handler = new RouterHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/backend-api/accounts/check/v4-2023-04-27" => JsonResponse("""
                { "accounts": [{ "account": { "account_id": "acc-other" }, "entitlement": { "expires_at": "2099-01-01T00:00:00Z" } }] }
                """),
            "/backend-api/subscriptions" => JsonResponse("{}"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var http = new HttpClient(handler);
        using var service = new CodexSubscriptionMetadataService(new CodexAuthCredentialReader(authPath), http, () => now);

        var result = await service.ReadAsync(CancellationToken.None);

        Assert.Equal(SubscriptionExpirationAvailability.NotProvided, result.Availability);
        Assert.Null(result.ExpiresAt);
        Assert.Equal(2, handler.CallCount);
        Assert.Contains("account_id=acc-current", handler.RequestUris[1].Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"accounts":{"acc-current":{"account":{"plan_type":"pro"},"entitlement":{"expires_at":"2026-12-31T00:00:00Z"}}}}""")]
    [InlineData("""{"accounts":[{"account_id":"acc-current","account":{"plan_type":"pro"},"entitlement":{"expires_at":"2026-12-31T00:00:00Z"}}]}""")]
    [InlineData("""{"accounts":{"default":{"account_id":"acc-current","entitlement":{"expires_at":"2026-12-31T00:00:00Z"}},"acc-current":{"account":{"account_id":"acc-current"},"entitlement":{"expires_at":"2026-12-31T00:00:00Z"}}}}""")]
    public async Task Account_keys_outer_ids_and_equivalent_aliases_match_the_current_account(string json)
    {
        var (result, handler) = await QueryAccountCheckAsync(json);

        Assert.Equal(SubscriptionExpirationAvailability.Available, result.Availability);
        Assert.Equal(DateTimeOffset.Parse("2026-12-31T00:00:00Z"), result.ExpiresAt);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData("""{"accounts":[{"account_id":"acc-current","account":{"id":"unrelated-record"},"entitlement":{"expires_at":"2026-12-31T00:00:00Z"}}]}""")]
    [InlineData("""{"accounts":[{"id":"unrelated-record","account":{"chatgpt_account_id":"acc-current","id":"another-record"},"entitlement":{"expires_at":"2026-12-31T00:00:00Z"}}]}""")]
    [InlineData("""{"accounts":[{"id":"unrelated-record","account":{"id":"acc-current"},"entitlement":{"expires_at":"2026-12-31T00:00:00Z"}}]}""")]
    [InlineData("""{"id":"acc-current","expires_at":"2026-12-31T00:00:00Z"}""")]
    public async Task Strong_account_ids_win_over_generic_record_ids_and_nested_generic_id_wins_over_outer_id(string json)
    {
        var (result, handler) = await QueryAccountCheckAsync(json);

        Assert.Equal(DateTimeOffset.Parse("2026-12-31T00:00:00Z"), result.ExpiresAt);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData("""{"accounts":[{"account_id":"acc-current","account":{"workspace_id":"acc-other"},"entitlement":{"expires_at":"2026-12-31T00:00:00Z"}}]}""")]
    [InlineData("""{"accounts":[{"account":{"account_id":"acc-current","chatgpt_account_id":"acc-other"},"entitlement":{"expires_at":"2026-12-31T00:00:00Z"}}]}""")]
    [InlineData("""{"accounts":{"acc-current":{"account_id":"acc-other","entitlement":{"expires_at":"2026-12-31T00:00:00Z"}}}}""")]
    [InlineData("""{"accounts":{"11111111-2222-3333-4444-555555555555":{"account_id":"acc-current","entitlement":{"expires_at":"2026-12-31T00:00:00Z"}}}}""")]
    [InlineData("""{"accounts":{"default":{"account":{"plan_type":"pro"},"entitlement":{"expires_at":"2026-12-31T00:00:00Z"}}}}""")]
    [InlineData("""{"accounts":[{"account_id":"ACC-CURRENT","entitlement":{"expires_at":"2026-12-31T00:00:00Z"}}]}""")]
    public async Task Conflicting_ids_unidentified_aliases_and_case_different_opaque_ids_do_not_supply_an_expiration(string json)
    {
        var (result, handler) = await QueryAccountCheckAsync(json);

        Assert.Equal(SubscriptionExpirationAvailability.NotProvided, result.Availability);
        Assert.Null(result.ExpiresAt);
        Assert.Equal(2, handler.CallCount);
        Assert.Contains("account_id=acc-current", handler.RequestUris[1].Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"accounts":{"ABCDEF01-2345-6789-ABCD-EF0123456789":{"account":{"plan_type":"pro"},"entitlement":{"expires_at":"2026-12-31T00:00:00Z"}}}}""")]
    [InlineData("""{"accounts":[{"account_id":"ABCDEF01-2345-6789-ABCD-EF0123456789","entitlement":{"expires_at":"2026-12-31T00:00:00Z"}}]}""")]
    [InlineData("""{"accounts":{"ABCDEF01-2345-6789-ABCD-EF0123456789":{"account_id":"abcdef01-2345-6789-abcd-ef0123456789","entitlement":{"expires_at":"2026-12-31T00:00:00Z"}}}}""")]
    public async Task Guid_account_keys_and_fields_are_equivalent_regardless_of_hexadecimal_case(string json)
    {
        var (result, handler) = await QueryAccountCheckAsync(json, "abcdef01-2345-6789-abcd-ef0123456789");

        Assert.Equal(DateTimeOffset.Parse("2026-12-31T00:00:00Z"), result.ExpiresAt);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Duplicate_account_aliases_with_conflicting_future_dates_use_the_fixed_account_subscription_lookup()
    {
        var (result, handler) = await QueryAccountCheckAsync("""
            {"accounts":{
                "default":{"account_id":"acc-current","entitlement":{"expires_at":"2026-12-31T00:00:00Z"}},
                "acc-current":{"account_id":"acc-current","entitlement":{"expires_at":"2027-01-31T00:00:00Z"}}
            }}
            """, fallback: """{"active_until":"2026-11-30T00:00:00Z"}""");

        Assert.Equal(DateTimeOffset.Parse("2026-11-30T00:00:00Z"), result.ExpiresAt);
        Assert.Equal(2, handler.CallCount);
        Assert.Contains("account_id=acc-current", handler.RequestUris[1].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_account_aliases_merge_equal_instants_and_ignore_expired_dates()
    {
        var (result, handler) = await QueryAccountCheckAsync("""
            {"accounts":[
                {"account_id":"acc-current","entitlement":{"expires_at":"2026-12-31T00:00:00Z"}},
                {"account_id":"acc-current","entitlement":{"expires_at":"2026-12-31T08:00:00+08:00"}},
                {"account_id":"acc-current","entitlement":{"expires_at":"2026-01-31T00:00:00Z"}}
            ]}
            """);

        Assert.Equal(DateTimeOffset.Parse("2026-12-31T00:00:00Z"), result.ExpiresAt);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Local_claim_for_another_account_is_skipped_before_online_lookup()
    {
        var now = DateTimeOffset.Parse("2026-10-08T08:00:00Z");
        var authPath = await WriteAuthAsync(
            SubscriptionJwt("acc-other", "2099-01-01T00:00:00Z"),
            CreateJwt(new { }),
            "acc-current");
        var handler = new RouterHandler(_ => JsonResponse("""
            { "accounts": [{ "account": { "account_id": "acc-current" }, "entitlement": { "expires_at": "2026-12-31T00:00:00Z" } }] }
            """));
        using var http = new HttpClient(handler);
        using var service = new CodexSubscriptionMetadataService(new CodexAuthCredentialReader(authPath), http, () => now);

        var result = await service.ReadAsync(CancellationToken.None);

        Assert.Equal(DateTimeOffset.Parse("2026-12-31T00:00:00Z"), result.ExpiresAt);
        Assert.Equal(SubscriptionExpirationSource.ChatGptSubscription, result.Source);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Current_access_token_claim_wins_over_another_accounts_id_token_claim()
    {
        var now = DateTimeOffset.Parse("2026-10-08T08:00:00Z");
        var authPath = await WriteAuthAsync(
            SubscriptionJwt("acc-other", "2099-01-01T00:00:00Z"),
            SubscriptionJwt("acc-current", "2026-11-08T08:00:00Z"),
            "acc-current");
        var handler = new RouterHandler(_ => throw new Xunit.Sdk.XunitException("The matching local claim must avoid network access."));
        using var http = new HttpClient(handler);
        using var service = new CodexSubscriptionMetadataService(new CodexAuthCredentialReader(authPath), http, () => now);

        var result = await service.ReadAsync(CancellationToken.None);

        Assert.Equal(DateTimeOffset.Parse("2026-11-08T08:00:00Z"), result.ExpiresAt);
        Assert.Equal(SubscriptionExpirationSource.CodexLogin, result.Source);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Token_with_an_embedded_newline_degrades_without_sending_a_request()
    {
        var authPath = await WriteAuthAsync(CreateJwt(new { }), "synthetic\ninvalid-token", "acc-current");
        var handler = new RouterHandler(_ => throw new Xunit.Sdk.XunitException("Malformed credentials must not be sent."));
        using var http = new HttpClient(handler);
        using var service = new CodexSubscriptionMetadataService(new CodexAuthCredentialReader(authPath), http);

        var result = await service.ReadAsync(CancellationToken.None);

        Assert.NotEqual(SubscriptionExpirationAvailability.Available, result.Availability);
        Assert.Null(result.ExpiresAt);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Expired_account_entitlement_falls_back_to_subscriptions_endpoint()
    {
        var now = DateTimeOffset.Parse("2026-10-08T08:00:00Z");
        var authPath = await WriteAuthAsync(CreateJwt(new { }), CreateJwt(new { }), "acc-1");
        var handler = new RouterHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/backend-api/accounts/check/v4-2023-04-27" => JsonResponse("""
                { "accounts": [{ "account": { "account_id": "acc-1" }, "entitlement": { "expires_at": "2026-01-01T00:00:00Z" } }] }
                """),
            "/backend-api/subscriptions" => JsonResponse("""
                { "active_until": "2027-01-01T00:00:00Z" }
                """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var http = new HttpClient(handler);
        using var service = new CodexSubscriptionMetadataService(
            new CodexAuthCredentialReader(authPath),
            http,
            () => now);

        var result = await service.ReadAsync(CancellationToken.None);

        Assert.Equal(DateTimeOffset.Parse("2027-01-01T00:00:00Z"), result.ExpiresAt);
        Assert.Equal(2, handler.CallCount);
        Assert.Contains("account_id=acc-1", handler.RequestUris[1].Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"account_id":"acc-other","active_until":"2026-12-31T00:00:00Z"}""")]
    [InlineData("""{"chatgpt_account_id":"acc-other","active_until":"2026-12-31T00:00:00Z"}""")]
    [InlineData("""{"workspace_id":"acc-other","active_until":"2026-12-31T00:00:00Z"}""")]
    [InlineData("""{"account":{"id":"acc-other"},"active_until":"2026-12-31T00:00:00Z"}""")]
    [InlineData("""{"account":{"account_id":"acc-other"},"expires_at":"2026-12-31T00:00:00Z"}""")]
    [InlineData("""{"account_id":"acc-current","account":{"workspace_id":"acc-other"},"active_until":"2026-12-31T00:00:00Z"}""")]
    [InlineData("""{"account":{"account_id":"acc-current","chatgpt_account_id":"acc-other"},"active_until":"2026-12-31T00:00:00Z"}""")]
    [InlineData("""{"account_id":"ACC-CURRENT","active_until":"2026-12-31T00:00:00Z"}""")]
    public async Task Subscription_response_with_a_different_or_conflicting_account_cannot_supply_an_expiration(string response)
    {
        var (result, handler) = await QueryAccountCheckAsync("""
            {"accounts":[{"account_id":"acc-other"}]}
            """, fallback: response);

        Assert.Equal(SubscriptionExpirationAvailability.NotProvided, result.Availability);
        Assert.Null(result.ExpiresAt);
        Assert.Equal(2, handler.CallCount);
        Assert.Contains("account_id=acc-current", handler.RequestUris[1].Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"active_until":"2026-12-31T00:00:00Z"}""")]
    [InlineData("""{"id":"subscription-record","active_until":"2026-12-31T00:00:00Z"}""")]
    [InlineData("""{"account_id":"acc-current","id":"subscription-record","active_until":"2026-12-31T00:00:00Z"}""")]
    [InlineData("""{"account":{"id":"acc-current"},"id":"subscription-record","active_until":"2026-12-31T00:00:00Z"}""")]
    [InlineData("""{"account":{"workspace_id":"acc-current"},"expires_at":"2026-12-31T00:00:00Z"}""")]
    [InlineData("""{"account_id":"acc-current","account":{"chatgpt_account_id":"acc-current"},"active_until":"2026-12-31T00:00:00Z"}""")]
    public async Task Subscription_response_accepts_the_matching_account_or_missing_identity_but_not_record_ids(string response)
    {
        var (result, handler) = await QueryAccountCheckAsync("{}", fallback: response);

        Assert.Equal(SubscriptionExpirationAvailability.Available, result.Availability);
        Assert.Equal(DateTimeOffset.Parse("2026-12-31T00:00:00Z"), result.ExpiresAt);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Subscription_response_accepts_equivalent_guid_account_ids()
    {
        var (result, _) = await QueryAccountCheckAsync("{}", "abcdef01-2345-6789-abcd-ef0123456789", """
            {"account_id":"ABCDEF01-2345-6789-ABCD-EF0123456789","active_until":"2026-12-31T00:00:00Z"}
            """);

        Assert.Equal(DateTimeOffset.Parse("2026-12-31T00:00:00Z"), result.ExpiresAt);
    }

    [Theory]
    [InlineData("""{"active_until":"2026-01-01T00:00:00Z"}""")]
    [InlineData("""{"expires_at":"2026-10-08T08:00:00Z"}""")]
    [InlineData("""{"active_until":"2026-01-01T00:00:00Z","expires_at":"2026-09-30T00:00:00Z"}""")]
    public async Task Expired_subscription_response_does_not_supply_an_expiration(string response)
    {
        var (result, _) = await QueryAccountCheckAsync("{}", fallback: response);

        Assert.Equal(SubscriptionExpirationAvailability.NotProvided, result.Availability);
        Assert.Null(result.ExpiresAt);
    }

    [Fact]
    public async Task Expired_active_until_can_fall_back_to_future_expires_at()
    {
        var (result, _) = await QueryAccountCheckAsync("{}", fallback: """
            {"active_until":"2026-01-01T00:00:00Z","expires_at":"2026-12-31T00:00:00Z"}
            """);

        Assert.Equal(SubscriptionExpirationAvailability.Available, result.Availability);
        Assert.Equal(DateTimeOffset.Parse("2026-12-31T00:00:00Z"), result.ExpiresAt);
    }

    [Fact]
    public async Task Direct_account_record_is_not_mistaken_for_its_nested_objects()
    {
        var now = DateTimeOffset.Parse("2026-10-08T08:00:00Z");
        var authPath = await WriteAuthAsync(CreateJwt(new { }), CreateJwt(new { }), "acc-1");
        var handler = new RouterHandler(_ => JsonResponse("""
            { "account": { "account_id": "acc-1" }, "entitlement": { "expires_at": "2026-12-01T00:00:00Z" } }
            """));
        using var http = new HttpClient(handler);
        using var service = new CodexSubscriptionMetadataService(
            new CodexAuthCredentialReader(authPath),
            http,
            () => now);

        var result = await service.ReadAsync(CancellationToken.None);

        Assert.Equal(SubscriptionExpirationAvailability.Available, result.Availability);
        Assert.Equal(DateTimeOffset.Parse("2026-12-01T00:00:00Z"), result.ExpiresAt);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Redirect_or_missing_credentials_degrades_without_exposing_an_exception()
    {
        var now = DateTimeOffset.Parse("2026-10-08T08:00:00Z");
        var authPath = await WriteAuthAsync(CreateJwt(new { }), CreateJwt(new { }), "acc-1");
        var handler = new RouterHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://example.invalid/") },
        });
        using var http = new HttpClient(handler);
        using var service = new CodexSubscriptionMetadataService(
            new CodexAuthCredentialReader(authPath),
            http,
            () => now);

        var result = await service.ReadAsync(CancellationToken.None);

        Assert.Equal(SubscriptionExpirationAvailability.Unavailable, result.Availability);
        Assert.Equal("暂不可用", result.StatusText);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("false")]
    [InlineData("\"not-an-object\"")]
    public async Task Nonobject_service_responses_do_not_throw_or_invent_an_expiration(string json)
    {
        var authPath = await WriteAuthAsync(CreateJwt(new { }), CreateJwt(new { }), "acc-current");
        var handler = new RouterHandler(_ => JsonResponse(json));
        using var http = new HttpClient(handler);
        using var service = new CodexSubscriptionMetadataService(new CodexAuthCredentialReader(authPath), http);

        var result = await service.ReadAsync(CancellationToken.None);

        Assert.Equal(SubscriptionExpirationAvailability.NotProvided, result.Availability);
        Assert.Null(result.ExpiresAt);
        Assert.Equal(2, handler.CallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "登录已失效")]
    [InlineData(HttpStatusCode.Forbidden, "服务拒绝查询")]
    public async Task Login_and_service_rejections_have_distinct_safe_status_text(HttpStatusCode status, string expected)
    {
        var authPath = await WriteAuthAsync(CreateJwt(new { }), CreateJwt(new { }), "acc-current");
        var handler = new RouterHandler(_ => new HttpResponseMessage(status));
        using var http = new HttpClient(handler);
        using var service = new CodexSubscriptionMetadataService(new CodexAuthCredentialReader(authPath), http);

        var result = await service.ReadAsync(CancellationToken.None);

        Assert.Equal(SubscriptionExpirationAvailability.Unavailable, result.Availability);
        Assert.Equal(expected, result.StatusText);
        Assert.Null(result.ExpiresAt);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Oversized_response_is_rejected_with_or_without_a_content_length(bool knownLength)
    {
        var authPath = await WriteAuthAsync(CreateJwt(new { }), CreateJwt(new { }), "acc-current");
        var bytes = Encoding.UTF8.GetBytes(new string(' ', 1024 * 1024) + "{}");
        var handler = new RouterHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = knownLength
                ? new ByteArrayContent(bytes)
                : new StreamContent(new SyntheticResponseStream(bytes)),
        });
        using var http = new HttpClient(handler);
        using var service = new CodexSubscriptionMetadataService(new CodexAuthCredentialReader(authPath), http);

        var result = await service.ReadAsync(CancellationToken.None);

        Assert.Equal(SubscriptionExpirationAvailability.Unavailable, result.Availability);
        Assert.Null(result.ExpiresAt);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task A_response_body_that_stalls_is_cancelled_by_the_request_deadline()
    {
        var authPath = await WriteAuthAsync(CreateJwt(new { }), CreateJwt(new { }), "acc-current");
        var responseStream = new SyntheticResponseStream();
        var handler = new RouterHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(responseStream),
        });
        using var http = new HttpClient(handler);
        using var service = new CodexSubscriptionMetadataService(
            new CodexAuthCredentialReader(authPath),
            http,
            requestTimeout: TimeSpan.FromMilliseconds(100));

        var result = await service.ReadAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(SubscriptionExpirationAvailability.Unavailable, result.Availability);
        Assert.True(responseStream.CancellationObserved);
        Assert.Equal(1, handler.CallCount);
    }

    private async Task<(SubscriptionExpirationSnapshot Result, RouterHandler Handler)> QueryAccountCheckAsync(
        string response,
        string accountId = "acc-current",
        string fallback = "{}")
    {
        var now = DateTimeOffset.Parse("2026-10-08T08:00:00Z");
        var authPath = await WriteAuthAsync(CreateJwt(new { }), CreateJwt(new { }), accountId);
        var handler = new RouterHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/backend-api/accounts/check/v4-2023-04-27" => JsonResponse(response),
            "/backend-api/subscriptions" => JsonResponse(fallback),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var http = new HttpClient(handler);
        using var service = new CodexSubscriptionMetadataService(new CodexAuthCredentialReader(authPath), http, () => now);
        return (await service.ReadAsync(CancellationToken.None), handler);
    }

    private async Task<string> WriteAuthAsync(string idToken, string accessToken, string? accountId = null)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "credentials.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            auth_mode = "chatgpt",
            tokens = new
            {
                id_token = idToken,
                access_token = accessToken,
                refresh_token = "test-refresh-token",
                account_id = accountId,
            },
        }));
        return path;
    }

    private static string CreateJwt(object claims)
    {
        var claimMap = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(claims))!;
        if (claimMap.Remove("auth", out var auth))
        {
            claimMap["https://api.openai.com/auth"] = auth;
        }
        return $"{Encode(new { alg = "none", typ = "JWT" })}.{Encode(claimMap)}.signature";
    }

    private static string SubscriptionJwt(string accountId, string activeUntil) => CreateJwt(new
    {
        auth = new Dictionary<string, object?>
        {
            ["chatgpt_account_id"] = accountId,
            ["chatgpt_subscription_active_until"] = activeUntil,
        },
    });

    private static string Encode(object value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    private sealed class RouterHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        internal int CallCount { get; private set; }
        internal List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            RequestUris.Add(request.RequestUri!);
            return Task.FromResult(route(request));
        }
    }

    private sealed class SyntheticResponseStream(byte[]? bytes = null) : Stream
    {
        private readonly MemoryStream? content = bytes is null ? null : new MemoryStream(bytes, writable: false);

        internal bool CancellationObserved { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (content is not null)
            {
                return await content.ReadAsync(buffer, cancellationToken);
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            content?.Read(buffer, offset, count) ?? throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                content?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
