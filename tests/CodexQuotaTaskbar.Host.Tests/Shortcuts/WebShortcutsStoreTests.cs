using System.Text;
using CodexQuotaTaskbar.Host.Shortcuts;

namespace CodexQuotaTaskbar.Host.Tests.Shortcuts;

public sealed class WebShortcutsStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "diandao-shortcuts-tests-" + Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(directory, "web-shortcuts.json");

    [Fact]
    public async Task First_load_has_only_the_existing_reset_website_and_does_not_create_user_files()
    {
        var value = await new WebShortcutsStore(StorePath).LoadAsync();

        Assert.Null(value.ErrorMessage);
        var entry = Assert.Single(value.Items);
        Assert.Equal("重置公告", entry.Name);
        Assert.Equal("https://codex-resets.com/zh-CN", entry.TargetUri.AbsoluteUri);
        Assert.True(Guid.TryParse(entry.Id, out _));
        Assert.False(File.Exists(StorePath));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task Added_sites_and_their_order_survive_a_new_store_instance()
    {
        var entries = new[] { Entry("https://example.com/path?q=1#first", "示例网站"), Entry("https://github.com/", "GitHub") };
        var saved = await new WebShortcutsStore(StorePath).SaveAsync(entries);
        var reopened = await new WebShortcutsStore(StorePath).LoadAsync();

        Assert.Null(saved.ErrorMessage);
        Assert.Null(reopened.ErrorMessage);
        Assert.Equal(entries.Select(item => item.Id), reopened.Items.Select(item => item.Id));
        Assert.Equal(entries.Select(item => item.Name), reopened.Items.Select(item => item.Name));
        Assert.Equal(entries.Select(item => item.TargetUri.AbsoluteUri), reopened.Items.Select(item => item.TargetUri.AbsoluteUri));
        Assert.True(File.Exists(StorePath));
    }

    [Fact]
    public async Task Deleting_a_site_persists_and_does_not_change_existing_settings()
    {
        Directory.CreateDirectory(directory);
        var settingsPath = Path.Combine(directory, "settings.json");
        const string settings = "{\"keep-this-unrelated-setting\":true}";
        await File.WriteAllTextAsync(settingsPath, settings);
        var first = Entry("https://example.com/", "First");
        var remaining = Entry("https://github.com/", "Second");
        var store = new WebShortcutsStore(StorePath);
        Assert.Null((await store.SaveAsync([first, remaining])).ErrorMessage);

        var deleted = await store.SaveAsync([remaining]);
        var reopened = await new WebShortcutsStore(StorePath).LoadAsync();

        Assert.Null(deleted.ErrorMessage);
        Assert.Equal(remaining.Id, Assert.Single(reopened.Items).Id);
        Assert.Equal(settings, await File.ReadAllTextAsync(settingsPath));
    }

    [Fact]
    public async Task An_explicitly_saved_empty_grid_stays_empty_after_restart()
    {
        var store = new WebShortcutsStore(StorePath);
        Assert.NotEmpty((await store.LoadAsync()).Items);

        Assert.Null((await store.SaveAsync([])).ErrorMessage);
        var reopened = await new WebShortcutsStore(StorePath).LoadAsync();

        Assert.Null(reopened.ErrorMessage);
        Assert.Empty(reopened.Items);
        Assert.True(File.Exists(StorePath));
    }

    [Fact]
    public async Task Duplicate_normalized_urls_keep_the_first_item_without_merging_distinct_fragments()
    {
        var first = Entry("https://EXAMPLE.COM:443/", "First");
        var duplicate = Entry("https://example.com/", "Duplicate");
        var anchorOne = Entry("https://example.com/#one", "One");
        var anchorTwo = Entry("https://example.com/#two", "Two");

        var saved = await new WebShortcutsStore(StorePath).SaveAsync([first, duplicate, anchorOne, anchorTwo]);
        var reopened = await new WebShortcutsStore(StorePath).LoadAsync();

        Assert.Null(saved.ErrorMessage);
        Assert.Equal(3, reopened.Items.Count);
        Assert.Equal(first.Id, reopened.Items[0].Id);
        Assert.Equal(new[] { "https://example.com/", "https://example.com/#one", "https://example.com/#two" },
            reopened.Items.Select(item => item.TargetUri.AbsoluteUri));
    }

    [Fact]
    public async Task Duplicate_ids_for_different_sites_cannot_overwrite_the_last_good_file()
    {
        var store = new WebShortcutsStore(StorePath);
        var first = Entry("https://example.com/", "First");
        Assert.Null((await store.SaveAsync([first])).ErrorMessage);
        var before = await File.ReadAllBytesAsync(StorePath);
        var collision = new WebShortcut(first.Id, "Other", new Uri("https://github.com/"));

        var value = await store.SaveAsync([first, collision]);

        Assert.NotNull(value.ErrorMessage);
        Assert.Equal(first.Id, Assert.Single(value.Items).Id);
        Assert.Equal(before, await File.ReadAllBytesAsync(StorePath));
    }

    [Fact]
    public async Task Invalid_ids_and_unsafe_destinations_do_not_replace_existing_shortcuts()
    {
        var store = new WebShortcutsStore(StorePath);
        var original = Entry("https://example.com/", "Original");
        Assert.Null((await store.SaveAsync([original])).ErrorMessage);
        var before = await File.ReadAllBytesAsync(StorePath);
        foreach (var invalid in new[]
        {
            new WebShortcut("not-a-guid", "Invalid", new Uri("https://github.com/")),
            new WebShortcut(Guid.NewGuid().ToString("D"), "Invalid", new Uri("javascript:alert(1)")),
            new WebShortcut(Guid.NewGuid().ToString("D"), "Invalid", new Uri("file:///C:/private")),
        })
        {
            var value = await store.SaveAsync([invalid]);
            Assert.NotNull(value.ErrorMessage);
            Assert.Equal(before, await File.ReadAllBytesAsync(StorePath));
        }
    }

    [Fact]
    public async Task Names_are_bounded_plain_text_and_a_blank_name_uses_the_domain()
    {
        var longName = "  A\tSite\r\n" + new string('x', 100);
        var value = await new WebShortcutsStore(StorePath).SaveAsync([
            Entry("https://example.com/", longName), Entry("https://github.com/", " "),
        ]);

        Assert.Null(value.ErrorMessage);
        Assert.InRange(value.Items[0].Name.Length, 1, 80);
        Assert.DoesNotContain(value.Items[0].Name, char.IsControl);
        Assert.Equal("github.com", value.Items[1].Name);
        Assert.All(value.Items, item => Assert.Equal(item.Name.Trim(), item.Name));
    }

    [Fact]
    public async Task More_than_the_supported_grid_capacity_does_not_change_the_file()
    {
        var store = new WebShortcutsStore(StorePath);
        var original = Entry("https://example.com/", "Original");
        Assert.Null((await store.SaveAsync([original])).ErrorMessage);
        var before = await File.ReadAllBytesAsync(StorePath);
        var tooMany = Enumerable.Range(0, 49).Select(index => Entry($"https://example.com/{index}", $"Site {index}")).ToArray();

        var failed = await store.SaveAsync(tooMany);

        Assert.NotNull(failed.ErrorMessage);
        Assert.Equal(before, await File.ReadAllBytesAsync(StorePath));
        Assert.Equal(original.Id, Assert.Single(failed.Items).Id);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("{\"Schema\":999,\"Items\":[],\"schema\":999,\"items\":[]}")]
    [InlineData("{\"Schema\":1,\"Items\":null,\"schema\":1,\"items\":null}")]
    public async Task A_damaged_or_unknown_configuration_is_reported_and_never_changed_by_load(string payload)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(StorePath, payload);
        var before = await File.ReadAllBytesAsync(StorePath);

        var value = await new WebShortcutsStore(StorePath).LoadAsync();

        Assert.NotNull(value.ErrorMessage);
        Assert.Equal(before, await File.ReadAllBytesAsync(StorePath));
        Assert.Empty(Directory.GetFiles(directory, "*.bak"));
    }

    [Fact]
    public async Task Explicit_save_after_a_corrupt_configuration_first_preserves_a_recoverable_copy()
    {
        Directory.CreateDirectory(directory);
        const string corrupt = "{this original file must remain recoverable}";
        await File.WriteAllTextAsync(StorePath, corrupt);
        var originalBytes = await File.ReadAllBytesAsync(StorePath);
        var store = new WebShortcutsStore(StorePath);
        Assert.NotNull((await store.LoadAsync()).ErrorMessage);
        var added = Entry("https://github.com/", "GitHub");

        var saved = await store.SaveAsync([added]);

        Assert.Null(saved.ErrorMessage);
        var backup = Assert.Single(Directory.GetFiles(directory, "web-shortcuts.json.corrupt-*.bak"));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(backup));
        Assert.Equal(added.Id, Assert.Single((await new WebShortcutsStore(StorePath).LoadAsync()).Items).Id);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task Oversized_existing_configuration_is_not_read_or_overwritten_by_an_unbounded_recovery()
    {
        Directory.CreateDirectory(directory);
        var oversized = Encoding.UTF8.GetBytes(new string('x', 129 * 1024));
        await File.WriteAllBytesAsync(StorePath, oversized);
        var store = new WebShortcutsStore(StorePath);

        Assert.NotNull((await store.LoadAsync()).ErrorMessage);
        var value = await store.SaveAsync([Entry("https://github.com/", "GitHub")]);

        Assert.NotNull(value.ErrorMessage);
        Assert.Equal(oversized, await File.ReadAllBytesAsync(StorePath));
        Assert.Empty(Directory.GetFiles(directory, "*.bak"));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task A_locked_destination_cannot_destroy_the_last_saved_grid()
    {
        var store = new WebShortcutsStore(StorePath);
        var original = Entry("https://example.com/", "Original");
        Assert.Null((await store.SaveAsync([original])).ErrorMessage);
        var before = await File.ReadAllBytesAsync(StorePath);
        WebShortcutsSnapshot failed;
        await using (var locked = new FileStream(StorePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            failed = await store.SaveAsync([Entry("https://github.com/", "Replacement")]);
        }

        Assert.NotNull(failed.ErrorMessage);
        Assert.Equal(before, await File.ReadAllBytesAsync(StorePath));
        Assert.Equal(original.Id, Assert.Single(failed.Items).Id);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task Precancelled_load_and_save_preserve_the_current_file_without_temporary_artifacts()
    {
        var store = new WebShortcutsStore(StorePath);
        var original = Entry("https://example.com/", "Original");
        Assert.Null((await store.SaveAsync([original])).ErrorMessage);
        var before = await File.ReadAllBytesAsync(StorePath);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync([], cancelled.Token));

        Assert.Equal(before, await File.ReadAllBytesAsync(StorePath));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Theory]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("  HTTPS://EXAMPLE.COM:443/path?q=1#part  ", "https://example.com/path?q=1#part")]
    [InlineData("http://example.com:80/", "http://example.com/")]
    [InlineData("http://localhost:8080/", "http://localhost:8080/")]
    public void User_addresses_normalize_to_http_or_https_without_losing_path_query_or_fragment(string input, string expected)
    {
        Assert.True(WebShortcut.TryNormalizeUrl(input, out var uri, out var error));
        Assert.Null(error);
        Assert.Equal(expected, uri!.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,test")]
    [InlineData("file:///C:/private")]
    [InlineData("shell:AppsFolder")]
    [InlineData("ftp://example.com/")]
    [InlineData("https://user:password@example.com/")]
    [InlineData("https://example.com/a\nb")]
    [InlineData("https://example.com/a\tb")]
    [InlineData("https://example.com/hello world")]
    [InlineData("https://example.com\\@other.example/")]
    [InlineData("https://example.com:99999/")]
    public void Unsafe_or_ambiguous_user_addresses_are_rejected(string? input)
    {
        Assert.False(WebShortcut.TryNormalizeUrl(input, out var uri, out var error));
        Assert.Null(uri);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Overlong_user_addresses_are_rejected_before_persistence()
    {
        Assert.False(WebShortcut.TryNormalizeUrl("https://example.com/" + new string('x', 2048), out var uri, out var error));
        Assert.Null(uri);
        Assert.NotNull(error);
    }

    private static WebShortcut Entry(string target, string name) => new(Guid.NewGuid().ToString("D"), name, new Uri(target));

    public void Dispose()
    {
        var resolved = Path.GetFullPath(directory);
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("diandao-shortcuts-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing cleanup outside this test's isolated temporary directory.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
