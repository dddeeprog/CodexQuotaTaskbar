using CodexQuotaTaskbar.Host.Lifetime;

namespace CodexQuotaTaskbar.Host.Tests.Lifetime;

public sealed class HostOptionsTests
{
    [Fact]
    public void Normal_mode_uses_live_provider()
    {
        var options = HostOptions.Parse([]);

        Assert.False(options.Demo);
        Assert.False(options.SubscriptionLogin);
        Assert.Null(options.ExitAfter);
    }

    [Fact]
    public void Bounded_demo_is_explicit()
    {
        var options = HostOptions.Parse(["--demo", "--exit-after-seconds", "8"]);

        Assert.True(options.Demo);
        Assert.Equal(TimeSpan.FromSeconds(8), options.ExitAfter);
    }

    [Fact]
    public void Subscription_login_window_requires_an_explicit_startup_flag()
    {
        var options = HostOptions.Parse(["--subscription-login"]);

        Assert.True(options.SubscriptionLogin);
        Assert.False(options.Demo);
        Assert.Null(options.ExitAfter);
    }

    [Fact]
    public void Subscription_login_flag_can_be_combined_with_existing_options()
    {
        var options = HostOptions.Parse(["--subscription-login", "--exit-after-seconds", "8", "--demo"]);

        Assert.True(options.SubscriptionLogin);
        Assert.True(options.Demo);
        Assert.Equal(TimeSpan.FromSeconds(8), options.ExitAfter);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("301")]
    [InlineData("oops")]
    public void Rejects_unsafe_smoke_duration(string value)
    {
        Assert.Throws<ArgumentException>(() => HostOptions.Parse(["--exit-after-seconds", value]));
    }
}
