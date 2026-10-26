using System.Text.Json;
using CodexQuotaTaskbar.Host.Shortcuts;

namespace CodexQuotaTaskbar.Host.Tests.Shortcuts;

public sealed class WebShortcutsManualIconPersistenceTests : IDisposable
{
    private const string ValidKey = "0123456789abcdef0123456789abcdef.png";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "diandao-manual-icon-config-" + Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(directory, "web-shortcuts.json");

    [Fact]
    public async Task Existing_schema_one_without_a_manual_icon_is_still_accepted()
    {
        var entry = Entry();
        await WriteOriginalAsync(entry, includeKey: false);

        var loaded = await new WebShortcutsStore(StorePath).LoadAsync();

        Assert.Null(loaded.ErrorMessage);
        Assert.Null(Assert.Single(loaded.Items).ManualIconKey);
        Assert.Equal(entry.Id, loaded.Items[0].Id);
        Assert.Equal(entry.TargetUri.AbsoluteUri, loaded.Items[0].TargetUri.AbsoluteUri);
    }

    [Fact]
    public async Task Optional_manual_key_survives_restart_and_can_be_removed_without_changing_the_shortcut()
    {
        var entry = Entry() with { ManualIconKey = ValidKey };
        Assert.Null((await new WebShortcutsStore(StorePath).SaveAsync([entry])).ErrorMessage);
        var reopened = new WebShortcutsStore(StorePath);
        var loaded = await reopened.LoadAsync();

        Assert.Equal(ValidKey, Assert.Single(loaded.Items).ManualIconKey);
        var automatic = loaded.Items[0] with { ManualIconKey = null };
        Assert.Null((await reopened.SaveAsync([automatic])).ErrorMessage);
        var restored = Assert.Single((await new WebShortcutsStore(StorePath).LoadAsync()).Items);
        Assert.Null(restored.ManualIconKey);
        Assert.Equal(entry.Id, restored.Id);
        Assert.Equal(entry.Name, restored.Name);
        Assert.Equal(entry.TargetUri.AbsoluteUri, restored.TargetUri.AbsoluteUri);
        Assert.DoesNotContain("ManualIconKey", await File.ReadAllTextAsync(StorePath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../0123456789abcdef0123456789abcdef.png")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF.png")]
    [InlineData("0123456789abcdef0123456789abcdef.PNG")]
    [InlineData("0123456789abcdef0123456789abcdef.jpg")]
    [InlineData("C:\\private\\image.png")]
    [InlineData("https://example.com/icon.png")]
    public async Task Invalid_manual_keys_cannot_replace_a_saved_list(string invalid)
    {
        var store = new WebShortcutsStore(StorePath);
        var original = Entry();
        Assert.Null((await store.SaveAsync([original])).ErrorMessage);
        var before = await File.ReadAllBytesAsync(StorePath);

        var failed = await store.SaveAsync([original with { ManualIconKey = invalid }]);

        Assert.NotNull(failed.ErrorMessage);
        Assert.Equal(before, await File.ReadAllBytesAsync(StorePath));
        Assert.Null(Assert.Single(failed.Items).ManualIconKey);
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("not-an-icon-key")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF.png")]
    public async Task Invalid_manual_keys_in_configuration_are_reported_as_corrupt_without_rewriting_the_file(string key)
    {
        await WriteOriginalAsync(Entry(), includeKey: true, key);
        var original = await File.ReadAllBytesAsync(StorePath);

        var loaded = await new WebShortcutsStore(StorePath).LoadAsync();

        Assert.NotNull(loaded.ErrorMessage);
        Assert.Equal(original, await File.ReadAllBytesAsync(StorePath));
        Assert.Empty(Directory.GetFiles(directory, "*.bak"));
    }

    [Fact]
    public async Task A_non_string_manual_key_is_not_silently_discarded()
    {
        await WriteOriginalAsync(Entry(), includeKey: true, 123);
        var before = await File.ReadAllBytesAsync(StorePath);

        var result = await new WebShortcutsStore(StorePath).LoadAsync();

        Assert.NotNull(result.ErrorMessage);
        Assert.Equal(before, await File.ReadAllBytesAsync(StorePath));
    }

    private async Task WriteOriginalAsync(WebShortcut entry, bool includeKey, object? key = null)
    {
        Directory.CreateDirectory(directory);
        var value = new Dictionary<string, object?>
        {
            ["Id"] = entry.Id, ["Name"] = entry.Name, ["TargetUri"] = entry.TargetUri.AbsoluteUri,
        };
        if (includeKey) value["ManualIconKey"] = key;
        await File.WriteAllTextAsync(StorePath, JsonSerializer.Serialize(new { Schema = 1, Items = new[] { value } }));
    }

    private static WebShortcut Entry() => new(Guid.NewGuid().ToString("D"), "示例网站", new Uri("https://example.com/path?query=1#part"));

    public void Dispose()
    {
        var resolved = Path.GetFullPath(directory);
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("diandao-manual-icon-config-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing cleanup outside this test's isolated directory.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
