using System.Text.Json;
using CodexQuotaTaskbar.Host.Settings;

namespace CodexQuotaTaskbar.Host.Tests.Settings;

public sealed class BongoAppearanceSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "diandao-bongo-settings-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Legacy_and_missing_settings_keep_white_ball_and_real_input_disabled()
    {
        var path = Path.Combine(directory, "settings.json");
        var store = new SettingsStore(path);
        Assert.Equal("white-ball", AppSettings.Default.EffectiveCompanionAppearance);
        Assert.Equal(192, AppSettings.Default.EffectiveBongoSizeDip);
        Assert.False(AppSettings.Default.CompanionTyping);
        Assert.Equal(AppSettings.Default, await store.LoadAsync(CancellationToken.None));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, """
            { "ShowAllTaskbars": false, "StartWithWindows": false, "LowQuotaNotifications": true,
              "LowQuotaThreshold": 29, "DotRealtime": true, "UnifiedPanel": true }
            """);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("white-ball", loaded.EffectiveCompanionAppearance);
        Assert.False(loaded.BongoAppearanceSelected);
        Assert.False(loaded.CompanionTyping);
        Assert.Equal(192, loaded.EffectiveBongoSizeDip);
        Assert.True(loaded.DotRealtime);
        Assert.True(loaded.UnifiedPanel);
        Assert.False(loaded.ShowAllTaskbars);
        Assert.Equal(29, loaded.LowQuotaThreshold);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Appearance_and_size_roundtrip_without_enabling_input_or_changing_other_preferences(bool visible, bool reduced)
    {
        var original = new AppSettings(false, false, false, 31, false, false, 37, true,
            visible, reduced, false, true, false, "bongo-cat", 240);
        var path = Path.Combine(directory, "settings.json");
        var store = new SettingsStore(path);
        await store.SaveAsync(original, CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(original, loaded);
        Assert.True(loaded.BongoAppearanceSelected);
        Assert.Equal(visible, loaded.CompanionBongoEnabled);
        Assert.False(loaded.CompanionTyping);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal("bongo-cat", document.RootElement.GetProperty(nameof(AppSettings.CompanionAppearance)).GetString());
        Assert.Equal(240, document.RootElement.GetProperty(nameof(AppSettings.BongoSizeDip)).GetInt32());
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Theory]
    [InlineData("bongo-cat", 192, "bongo-cat", 192)]
    [InlineData("white-ball", 240, "white-ball", 240)]
    [InlineData("unknown", 999, "white-ball", 240)]
    [InlineData("BONGO-CAT", -1, "white-ball", 140)]
    [InlineData(null, int.MinValue, "white-ball", 140)]
    public async Task Invalid_appearance_or_size_is_normalized_without_resetting_unrelated_values(string? appearance, int size, string expectedAppearance, int expectedSize)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        var original = new AppSettings(false, false, true, 47, false, true, 22, true,
            true, false, false, true, true, appearance!, size);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(original));
        var store = new SettingsStore(path);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(original with { CompanionAppearance = expectedAppearance, BongoSizeDip = expectedSize }, loaded);
        await store.SaveAsync(original, CancellationToken.None);
        Assert.Equal(loaded, await store.LoadAsync(CancellationToken.None));
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(directory);
        var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith("diandao-bongo-settings-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing cleanup outside isolated Bongo settings fixture.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}
