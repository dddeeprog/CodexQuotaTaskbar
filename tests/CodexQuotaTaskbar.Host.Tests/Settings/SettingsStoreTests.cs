using CodexQuotaTaskbar.Host.Settings;

namespace CodexQuotaTaskbar.Host.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "CodexQuotaTaskbar.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Missing_file_returns_safe_defaults_and_roundtrips()
    {
        var store = new SettingsStore(Path.Combine(directory, "settings.json"));

        var defaults = await store.LoadAsync(CancellationToken.None);
        Assert.True(defaults.ShowAllTaskbars);
        Assert.True(defaults.LowQuotaNotifications);
        Assert.True(defaults.AutomaticUpdatesEnabled);

        var changed = defaults with { ShowAllTaskbars = false, StartWithWindows = true };
        await store.SaveAsync(changed, CancellationToken.None);

        Assert.Equal(changed, await store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Legacy_settings_enable_automatic_updates_by_default()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        await File.WriteAllTextAsync(path, """
            { "ShowAllTaskbars": true, "StartWithWindows": false, "LowQuotaNotifications": true, "LowQuotaThreshold": 10 }
            """);

        var value = await new SettingsStore(path).LoadAsync(CancellationToken.None);

        Assert.True(value.AutomaticUpdatesEnabled);
    }

    [Fact]
    public async Task Invalid_file_is_ignored_without_exposing_content()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        await File.WriteAllTextAsync(path, "secret-not-json");

        var value = await new SettingsStore(path).LoadAsync(CancellationToken.None);

        Assert.Equal(AppSettings.Default, value);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
