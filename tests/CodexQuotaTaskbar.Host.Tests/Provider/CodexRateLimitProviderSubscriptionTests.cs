using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Host.Provider;

namespace CodexQuotaTaskbar.Host.Tests.Provider;

public sealed class CodexRateLimitProviderSubscriptionTests
{
    [Fact]
    public void Clearing_web_metadata_preserves_quota_but_removes_its_expiration()
    {
        var now = DateTimeOffset.UtcNow;
        var previous = SubscriptionExpirationSnapshot.Available(now.AddDays(20), now, SubscriptionExpirationSource.ChatGptWeb);
        var snapshot = QuotaSnapshot.Available([], now, "PRO 5X", previous);

        var cleared = CodexRateLimitProvider.RemoveBrowserSubscriptionMetadata(snapshot, enabled: true);

        Assert.Null(cleared.MembershipExpiresAt);
        Assert.Equal(SubscriptionExpirationAvailability.Unavailable, cleared.SubscriptionExpiration.Availability);
        Assert.Equal("网页登录数据已清除", cleared.SubscriptionExpiration.StatusText);
        Assert.Same(snapshot.Windows, cleared.Windows);
        Assert.Equal(snapshot.SubscriptionPlan, cleared.SubscriptionPlan);
        Assert.Equal(snapshot.CapturedAt, cleared.CapturedAt);
        Assert.Equal(snapshot.Availability, cleared.Availability);
    }

    [Fact]
    public void Clearing_web_metadata_honors_disabled_subscription_setting()
    {
        var now = DateTimeOffset.UtcNow;
        var previous = SubscriptionExpirationSnapshot.Available(now.AddDays(20), now, SubscriptionExpirationSource.ChatGptWeb);
        var snapshot = QuotaSnapshot.Available([], now, subscriptionExpiration: previous);

        var cleared = CodexRateLimitProvider.RemoveBrowserSubscriptionMetadata(snapshot, enabled: false);

        Assert.Equal(SubscriptionExpirationAvailability.Disabled, cleared.SubscriptionExpiration.Availability);
        Assert.Null(cleared.MembershipExpiresAt);
    }

    [Fact]
    public void Clearing_web_metadata_does_not_remove_local_login_metadata()
    {
        var now = DateTimeOffset.UtcNow;
        var previous = SubscriptionExpirationSnapshot.Available(now.AddDays(20), now, SubscriptionExpirationSource.CodexLogin);
        var snapshot = QuotaSnapshot.Available([], now, subscriptionExpiration: previous);

        Assert.Same(snapshot, CodexRateLimitProvider.RemoveBrowserSubscriptionMetadata(snapshot, enabled: true));
    }

    [Fact]
    public void Failed_refresh_cannot_restore_web_metadata_that_the_service_no_longer_returns()
    {
        var now = DateTimeOffset.UtcNow;
        var previous = SubscriptionExpirationSnapshot.Available(now.AddDays(20), now, SubscriptionExpirationSource.ChatGptWeb);
        var unavailable = SubscriptionExpirationSnapshot.Unavailable(now, SubscriptionExpirationSource.ChatGptSubscription);

        var result = CodexRateLimitProvider.ResolveSubscriptionRefresh(unavailable, previous);

        Assert.Same(unavailable, result);
        Assert.Null(result.ExpiresAt);
    }

    [Fact]
    public void Failed_refresh_still_preserves_nonbrowser_metadata_as_stale()
    {
        var now = DateTimeOffset.UtcNow;
        var previous = SubscriptionExpirationSnapshot.Available(now.AddDays(20), now, SubscriptionExpirationSource.CodexLogin);
        var unavailable = SubscriptionExpirationSnapshot.Unavailable(now, SubscriptionExpirationSource.ChatGptSubscription);

        var result = CodexRateLimitProvider.ResolveSubscriptionRefresh(unavailable, previous);

        Assert.Equal(previous.ExpiresAt, result.ExpiresAt);
        Assert.True(result.IsStale);
        Assert.Equal(SubscriptionExpirationSource.CodexLogin, result.Source);
    }
}
