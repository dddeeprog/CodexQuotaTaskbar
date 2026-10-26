namespace CodexQuotaTaskbar.Host.Tests.Shortcuts;

public sealed class WebShortcutsNavigationTests
{
    [Theory]
    [InlineData("https://codex-resets.com/zh-CN", true)]
    [InlineData("https://github.com/openai/codex#readme", true)]
    [InlineData("http://example.com:8080/path?q=a#b", true)]
    [InlineData("https://example.com/;not-a-shell-command", true)]
    [InlineData("https://user:password@example.com/", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows/System32/notepad.exe", false)]
    [InlineData("data:text/html,hello", false)]
    [InlineData("shell:AppsFolder", false)]
    [InlineData("mailto:test@example.com", false)]
    public void Browser_navigation_accepts_only_explicit_web_addresses(string target, bool allowed)
    {
        Assert.Equal(allowed, App.IsAllowedWebShortcut(new Uri(target, UriKind.Absolute)));
    }

    [Fact]
    public void Bottom_reset_credit_keeps_its_original_fixed_destination()
    {
        Assert.True(App.IsAllowedResetSource(new Uri("https://codex-resets.com/zh-CN")));
        Assert.False(App.IsAllowedResetSource(new Uri("https://codex-resets.com/zh-CN?redirect=elsewhere")));
    }
}
