using System.IO;
using CodexQuotaTaskbar.Host.Bongo;
using CodexQuotaTaskbar.Host.Installer;
using CodexQuotaTaskbar.Host.Settings;

namespace CodexQuotaTaskbar.Host.Tests.Installer;

public sealed class InstallerSetupTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "InstallerSetupTests", Guid.NewGuid().ToString("N"));
    private readonly CoreTermsDocument document = new("synthetic/1", "Test component text", "Test official text");
    private string SettingsPath => Path.Combine(root, "settings.json");
    private CoreConsentStore Store => new(Path.Combine(root, CoreConsentStore.FileName));
    private int downloads;
    private Task Download(CancellationToken token) { downloads++; return Task.CompletedTask; }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Fresh_decline_and_close_never_download_or_install_defaults(int choiceValue)
    {
        var choice = (CoreConsentDecision)choiceValue;
        await InstallerSetup.CompleteAsync(document, Store, new(SettingsPath), () => choice, Download, CancellationToken.None);
        Assert.Equal(0, downloads); Assert.False(File.Exists(SettingsPath));
        Assert.NotEqual(CoreConsentState.Accepted, await Store.LoadAsync(document, CancellationToken.None));
    }

    [Fact]
    public async Task Fresh_explicit_acceptance_saves_only_public_template_then_downloads()
    {
        await InstallerSetup.CompleteAsync(document, Store, new(SettingsPath), () => CoreConsentDecision.Accepted, Download, CancellationToken.None);
        Assert.Equal(1, downloads);
        var settings = await new SettingsStore(SettingsPath).LoadAsync(CancellationToken.None);
        Assert.Equal(AppSettings.InstallerDefault, settings);
        Assert.True(settings.UnifiedPanel); Assert.Equal(3, settings.MaterialTransparencyPercent);
        Assert.True(settings.CompanionBallEnabled); Assert.True(settings.EffectiveBongoCatVisible); Assert.Equal(192, settings.BongoSizeDip);
        Assert.False(settings.DotRealtime); Assert.False(settings.CompanionTyping); Assert.False(settings.StartWithWindows);
        using var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(SettingsPath));
        Assert.All(json.RootElement.EnumerateObject(), field => Assert.Contains(field.Value.ValueKind,
            new[] { System.Text.Json.JsonValueKind.True, System.Text.Json.JsonValueKind.False, System.Text.Json.JsonValueKind.Number, System.Text.Json.JsonValueKind.String }));
        Assert.Equal("bongo-cat", json.RootElement.EnumerateObject().Single(f => f.Value.ValueKind == System.Text.Json.JsonValueKind.String).Value.GetString());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    public async Task Upgrade_preserves_exact_existing_settings_and_matching_receipt_without_prompt(int priorValue)
    {
        var prior = (CoreConsentDecision)priorValue;
        Directory.CreateDirectory(root);
        const string existing = "{\"custom\":\"unrecognized preference preserved\"}";
        await File.WriteAllTextAsync(SettingsPath, existing);
        await Store.SaveDecisionAsync(document, prior, CancellationToken.None);
        var receiptBefore = await File.ReadAllBytesAsync(Path.Combine(root, CoreConsentStore.FileName));
        await InstallerSetup.CompleteAsync(document, Store, new(SettingsPath), () => throw new Exception("Unexpected re-consent"), Download, CancellationToken.None);
        Assert.Equal(existing, await File.ReadAllTextAsync(SettingsPath));
        Assert.Equal(receiptBefore, await File.ReadAllBytesAsync(Path.Combine(root, CoreConsentStore.FileName)));
        Assert.Equal(prior == CoreConsentDecision.Accepted ? 1 : 0, downloads);
    }

    [Fact]
    public async Task Changed_terms_require_new_choice_and_close_does_not_replace_old_receipt()
    {
        await Store.SaveDecisionAsync(document, CoreConsentDecision.Accepted, CancellationToken.None);
        var before = await File.ReadAllBytesAsync(Path.Combine(root, CoreConsentStore.FileName));
        var prompts = 0;
        await InstallerSetup.CompleteAsync(new("synthetic/2", "changed", "changed"), Store, new(SettingsPath),
            () => { prompts++; return CoreConsentDecision.Closed; }, Download, CancellationToken.None);
        Assert.Equal(1, prompts); Assert.Equal(0, downloads); Assert.False(File.Exists(SettingsPath));
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(root, CoreConsentStore.FileName)));
    }

    [Fact]
    public async Task Failed_download_does_not_erase_settings_or_forge_success()
    {
        await Assert.ThrowsAsync<IOException>(() => InstallerSetup.CompleteAsync(document, Store, new(SettingsPath),
            () => CoreConsentDecision.Accepted, _ => throw new IOException("synthetic offline"), CancellationToken.None));
        Assert.Equal(AppSettings.InstallerDefault, await new SettingsStore(SettingsPath).LoadAsync(CancellationToken.None));
    }

    [Fact]
    public void Base_assembly_has_manifest_and_terms_but_no_core_payload_resource()
    {
        var names = typeof(BongoPayload).Assembly.GetManifestResourceNames();
        Assert.DoesNotContain(BongoPayload.ArchiveResourceName, names);
        Assert.Contains(BongoPayload.ManifestResourceName, names);
        Assert.Contains(CoreTermsDocument.OfficialResourceName, names);
    }

    [Fact]
    public async Task Concurrent_initializers_cannot_overwrite_an_existing_preference_file()
    {
        Directory.CreateDirectory(root); await File.WriteAllTextAsync(SettingsPath, "keep unchanged");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => new SettingsStore(SettingsPath).InitializeInstallerDefaultsAsync(CancellationToken.None)));
        Assert.All(results, value => Assert.False(value)); Assert.Equal("keep unchanged", await File.ReadAllTextAsync(SettingsPath));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
