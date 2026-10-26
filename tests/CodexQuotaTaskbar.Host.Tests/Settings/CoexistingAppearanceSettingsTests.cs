using System.Text.Json;
using CodexQuotaTaskbar.Host.Settings;

namespace CodexQuotaTaskbar.Host.Tests.Settings;

public sealed class CoexistingAppearanceSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "diandao-coexist-settings-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(true, "bongo-cat", true)]
    [InlineData(false, "bongo-cat", false)]
    [InlineData(null, "bongo-cat", true)]
    [InlineData(true, "white-ball", false)]
    [InlineData(false, "white-ball", false)]
    [InlineData(true, "unknown", false)]
    public void Legacy_settings_derive_cat_visibility_without_disabling_the_status_ball(
        bool? statusBall, string legacyAppearance, bool expectedCat)
    {
        var settings = AppSettings.Default with { CompanionBall = statusBall, CompanionAppearance = legacyAppearance };

        Assert.Null(settings.BongoCatVisible);
        Assert.Equal(expectedCat, settings.EffectiveBongoCatVisible);
        Assert.Equal(expectedCat, settings.CompanionBongoEnabled);
        Assert.Equal(statusBall is not false, settings.CompanionBallEnabled);
        Assert.False(settings.CompanionTyping);
    }

    [Theory]
    [InlineData(true, true, "bongo-cat")]
    [InlineData(true, true, "white-ball")]
    [InlineData(true, false, "bongo-cat")]
    [InlineData(true, false, "white-ball")]
    [InlineData(false, true, "bongo-cat")]
    [InlineData(false, true, "white-ball")]
    [InlineData(false, false, "bongo-cat")]
    [InlineData(false, false, "white-ball")]
    public void Explicit_cat_visibility_is_independent_of_the_status_display_and_legacy_shape(
        bool cat, bool statusBall, string legacyAppearance)
    {
        var settings = AppSettings.Default with
        {
            CompanionBall = statusBall, CompanionAppearance = legacyAppearance, BongoCatVisible = cat,
        };

        Assert.Equal(cat, settings.EffectiveBongoCatVisible);
        Assert.Equal(cat, settings.CompanionBongoEnabled);
        Assert.Equal(statusBall, settings.CompanionBallEnabled);
        Assert.Equal(legacyAppearance, settings.CompanionAppearance);
        Assert.False(settings.CompanionTyping);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cat_override_roundtrips_in_a_private_store_without_changing_other_preferences(bool? visible)
    {
        var original = new AppSettings(false, false, false, 37, false, false, 28, true,
            false, true, false, true, false, "bongo-cat", 240) with { BongoCatVisible = visible };
        var path = Path.Combine(directory, "settings.json");
        var store = new SettingsStore(path);

        await store.SaveAsync(original, CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(original, loaded);
        Assert.Equal(visible ?? false, loaded.EffectiveBongoCatVisible);
        Assert.False(loaded.CompanionBallEnabled);
        Assert.False(loaded.CompanionTyping);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var property = json.RootElement.GetProperty(nameof(AppSettings.BongoCatVisible));
        if (visible is null) Assert.Equal(JsonValueKind.Null, property.ValueKind);
        else Assert.Equal(visible.Value, property.GetBoolean());
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task Reading_a_legacy_cat_configuration_does_not_rewrite_or_enable_input()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        var content = """
            { "ShowAllTaskbars": false, "StartWithWindows": false, "LowQuotaNotifications": false,
              "LowQuotaThreshold": 31, "CompanionBall": true, "CompanionAppearance": "bongo-cat",
              "BongoSizeDip": 160, "CompanionTyping": false, "UnifiedPanel": true }
            """;
        await File.WriteAllTextAsync(path, content);

        var settings = await new SettingsStore(path).LoadAsync(CancellationToken.None);

        Assert.True(settings.CompanionBallEnabled);
        Assert.True(settings.EffectiveBongoCatVisible);
        Assert.Null(settings.BongoCatVisible);
        Assert.False(settings.CompanionTyping);
        Assert.Equal(160, settings.EffectiveBongoSizeDip);
        Assert.Equal(content, await File.ReadAllTextAsync(path));
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(directory);
        var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith("diandao-coexist-settings-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing cleanup outside private coexistence fixture.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}
