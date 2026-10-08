using System.Reflection;
using System.Text.Json;
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
        Assert.True(defaults.SubscriptionDetailsEnabled);
        Assert.Equal(0, defaults.MaterialTransparencyPercent);
        Assert.Equal(0, defaults.EffectiveMaterialTransparencyPercent);

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
        Assert.True(value.SubscriptionDetailsEnabled);
        Assert.Equal(0, value.MaterialTransparencyPercent);
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

    [Theory]
    [InlineData(int.MinValue, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(37, 37)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    [InlineData(int.MaxValue, 100)]
    public void Effective_material_transparency_is_clamped(int configured, int expected)
    {
        var settings = AppSettings.Default with { MaterialTransparencyPercent = configured };

        Assert.Equal(expected, settings.EffectiveMaterialTransparencyPercent);
        Assert.Equal(configured, settings.MaterialTransparencyPercent);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(37, 37)]
    [InlineData(101, 100)]
    public async Task Load_normalizes_transparency_without_resetting_other_preferences(int configured, int expected)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        var settings = new AppSettings(false, true, false, 25, false, false, configured);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(settings));

        var loaded = await new SettingsStore(path).LoadAsync(CancellationToken.None);

        Assert.Equal(settings with { MaterialTransparencyPercent = expected }, loaded);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(37, 37)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    public async Task Save_roundtrips_normalized_transparency_and_other_preferences(int configured, int expected)
    {
        var path = Path.Combine(directory, "settings.json");
        var store = new SettingsStore(path);
        var settings = new AppSettings(false, true, false, 25, false, false, configured);

        await store.SaveAsync(settings, CancellationToken.None);

        Assert.Equal(settings with { MaterialTransparencyPercent = expected }, await store.LoadAsync(CancellationToken.None));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(expected, document.RootElement.GetProperty(nameof(AppSettings.MaterialTransparencyPercent)).GetInt32());
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task Concurrent_saves_wait_their_turn_and_preserve_the_last_requested_settings()
    {
        var path = Path.Combine(directory, "settings.json");
        var store = new SettingsStore(path);
        var gate = GetSaveGate(store);
        await gate.WaitAsync();
        var settings = Enumerable.Range(0, 101)
            .Select(value => AppSettings.Default with { MaterialTransparencyPercent = value })
            .ToArray();
        Task[] saves;
        try
        {
            saves = settings.Select(value => store.SaveAsync(value, CancellationToken.None)).ToArray();
            Assert.All(saves, save => Assert.False(save.IsCompleted));
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            gate.Release();
        }

        await Task.WhenAll(saves);

        Assert.Equal(settings[^1], await store.LoadAsync(CancellationToken.None));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task Cancelled_queued_save_does_not_overwrite_settings_or_block_later_saves()
    {
        var path = Path.Combine(directory, "settings.json");
        var store = new SettingsStore(path);
        var original = AppSettings.Default with { MaterialTransparencyPercent = 25 };
        await store.SaveAsync(original, CancellationToken.None);
        var gate = GetSaveGate(store);
        await gate.WaitAsync();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var cancelledSave = store.SaveAsync(original with { MaterialTransparencyPercent = 75 }, cancellation.Token);
            Assert.False(cancelledSave.IsCompleted);

            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledSave);
            Assert.Equal(original, await store.LoadAsync(CancellationToken.None));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            gate.Release();
        }

        var last = original with { MaterialTransparencyPercent = 90 };
        await store.SaveAsync(last, CancellationToken.None);
        Assert.Equal(last, await store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Failed_atomic_replace_cleans_temporary_file_and_releases_save_gate()
    {
        var path = Path.Combine(directory, "settings.json");
        var store = new SettingsStore(path);
        var original = AppSettings.Default with { MaterialTransparencyPercent = 25 };
        await store.SaveAsync(original, CancellationToken.None);

        await using (var lockedFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var exception = await Record.ExceptionAsync(() => store.SaveAsync(AppSettings.Default, CancellationToken.None));
            Assert.True(exception is IOException or UnauthorizedAccessException);
        }

        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        Assert.Equal(original, await store.LoadAsync(CancellationToken.None));
        var settings = AppSettings.Default with { MaterialTransparencyPercent = 50 };
        await store.SaveAsync(settings, CancellationToken.None);
        Assert.Equal(settings, await store.LoadAsync(CancellationToken.None));
    }

    private static SemaphoreSlim GetSaveGate(SettingsStore store) => Assert.IsType<SemaphoreSlim>(
        typeof(SettingsStore).GetField("saveGate", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(store));

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
