using System.Text.Json;
using CodexQuotaTaskbar.Host.Settings;

namespace CodexQuotaTaskbar.Host.Tests.Settings;

public sealed class CompanionTypingSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "diandao-typing-settings-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Keyboard_interaction_is_opt_in_for_missing_and_legacy_settings()
    {
        Assert.False(AppSettings.Default.CompanionTyping);
        var path = Path.Combine(directory, "settings.json");
        var store = new SettingsStore(path);
        Assert.False((await store.LoadAsync(CancellationToken.None)).CompanionTyping);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, """
            { "ShowAllTaskbars": false, "StartWithWindows": false, "LowQuotaNotifications": true,
              "LowQuotaThreshold": 25, "CompanionBall": true, "ReduceCompanionMotion": false,
              "DotRealtime": true, "UnifiedPanel": true }
            """);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.False(loaded.CompanionTyping);
        Assert.True(loaded.CompanionBallEnabled);
        Assert.True(loaded.DotRealtime);
        Assert.True(loaded.UnifiedPanel);
        Assert.False(loaded.ShowAllTaskbars);
        Assert.Equal(25, loaded.LowQuotaThreshold);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    public async Task Saving_preserves_the_choice_even_when_ball_or_motion_is_paused(bool typing, bool ball, bool reduced)
    {
        var path = Path.Combine(directory, "settings.json");
        var settings = new AppSettings(false, false, false, 31, false, true, 27, true,
            ball, reduced, false, true, typing);
        await new SettingsStore(path).SaveAsync(settings, CancellationToken.None);

        var loaded = await new SettingsStore(path).LoadAsync(CancellationToken.None);

        Assert.Equal(settings, loaded);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(typing, document.RootElement.GetProperty(nameof(AppSettings.CompanionTyping)).GetBoolean());
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(directory);
        var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith("diandao-typing-settings-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing cleanup outside this isolated settings fixture.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}
