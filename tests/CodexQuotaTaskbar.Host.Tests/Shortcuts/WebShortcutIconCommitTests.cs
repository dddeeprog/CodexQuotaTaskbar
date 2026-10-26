using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexQuotaTaskbar.Host.Shortcuts;

namespace CodexQuotaTaskbar.Host.Tests.Shortcuts;

public sealed class WebShortcutIconCommitTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "diandao-shortcut-commit-" + Guid.NewGuid().ToString("N"));
    private string CacheDirectory => Path.Combine(directory, "manual-icons");
    private string ConfigPath => Path.Combine(directory, "web-shortcuts.json");

    [Fact]
    public async Task A_failed_real_store_save_removes_only_the_unreferenced_import_and_preserves_the_previous_icon()
    {
        var icons = new WebShortcutManualIconStore(CacheDirectory);
        var old = await ImportAsync(icons, "old.png");
        var added = await ImportAsync(icons, "added.png");
        var previous = new[] { Entry("https://example.com/", old.Key) };
        var replacement = new[] { previous[0] with { ManualIconKey = added.Key } };
        var store = new WebShortcutsStore(ConfigPath);
        Assert.Null((await store.SaveAsync(previous)).ErrorMessage);
        var before = await File.ReadAllBytesAsync(ConfigPath);
        WebShortcutsSnapshot failed;

        await using (var locked = new FileStream(ConfigPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            failed = await WebShortcutIconCommit.SaveAsync(store.SaveAsync, icons, previous, replacement, added.Key, CancellationToken.None);

        Assert.NotNull(failed.ErrorMessage);
        Assert.Equal(old.Key, Assert.Single(failed.Items).ManualIconKey);
        Assert.Equal(before, await File.ReadAllBytesAsync(ConfigPath));
        Assert.NotNull(await icons.LoadAsync(old.Key));
        Assert.Null(await icons.LoadAsync(added.Key));
        await AssertSourceUnchanged(old);
        await AssertSourceUnchanged(added);
    }

    [Fact]
    public async Task Cancellation_immediately_after_a_successful_save_does_not_delete_the_newly_referenced_icon()
    {
        var icons = new WebShortcutManualIconStore(CacheDirectory);
        var old = await ImportAsync(icons, "old.png");
        var added = await ImportAsync(icons, "added.png");
        var previous = new[] { Entry("https://example.com/", old.Key) };
        var replacement = new[] { previous[0] with { ManualIconKey = added.Key } };
        var store = new WebShortcutsStore(ConfigPath);
        Assert.Null((await store.SaveAsync(previous)).ErrorMessage);
        using var lifetime = new CancellationTokenSource();

        var saved = await WebShortcutIconCommit.SaveAsync(async (items, token) =>
        {
            var result = await store.SaveAsync(items, token);
            Assert.Null(result.ErrorMessage);
            lifetime.Cancel();
            return result;
        }, icons, previous, replacement, added.Key, lifetime.Token);

        Assert.True(lifetime.IsCancellationRequested);
        Assert.Null(saved.ErrorMessage);
        Assert.Equal(added.Key, Assert.Single((await new WebShortcutsStore(ConfigPath).LoadAsync()).Items).ManualIconKey);
        Assert.NotNull(await icons.LoadAsync(added.Key));
        Assert.Null(await icons.LoadAsync(old.Key));
        await AssertSourceUnchanged(old);
        await AssertSourceUnchanged(added);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_restore_or_remove_keeps_the_original_manual_icon(bool removeShortcut)
    {
        var icons = new WebShortcutManualIconStore(CacheDirectory);
        var original = await ImportAsync(icons, "original.png");
        var previous = new[] { Entry("https://example.com/", original.Key) };
        WebShortcut[] replacement = removeShortcut ? [] : [previous[0] with { ManualIconKey = null }];
        var store = new WebShortcutsStore(ConfigPath);
        Assert.Null((await store.SaveAsync(previous)).ErrorMessage);
        WebShortcutsSnapshot result;

        await using (var locked = new FileStream(ConfigPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            result = await WebShortcutIconCommit.SaveAsync(store.SaveAsync, icons, previous, replacement, null, CancellationToken.None);

        Assert.NotNull(result.ErrorMessage);
        Assert.Equal(original.Key, Assert.Single((await new WebShortcutsStore(ConfigPath).LoadAsync()).Items).ManualIconKey);
        Assert.NotNull(await icons.LoadAsync(original.Key));
        await AssertSourceUnchanged(original);
    }

    [Fact]
    public async Task Removing_one_of_two_shortcuts_with_a_shared_key_preserves_the_remaining_reference()
    {
        var icons = new WebShortcutManualIconStore(CacheDirectory);
        var shared = await ImportAsync(icons, "shared.png");
        var previous = new[] { Entry("https://example.com/", shared.Key), Entry("https://github.com/", shared.Key) };
        var store = new WebShortcutsStore(ConfigPath);
        Assert.Null((await store.SaveAsync(previous)).ErrorMessage);

        var result = await WebShortcutIconCommit.SaveAsync(store.SaveAsync, icons, previous, [previous[1]], null, CancellationToken.None);

        Assert.Null(result.ErrorMessage);
        Assert.Equal(previous[1].Id, Assert.Single(result.Items).Id);
        Assert.NotNull(await icons.LoadAsync(shared.Key));
        await AssertSourceUnchanged(shared);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_successful_restore_or_last_remove_cleans_only_the_unreferenced_owned_copy(bool removeShortcut)
    {
        var icons = new WebShortcutManualIconStore(CacheDirectory);
        var original = await ImportAsync(icons, "original.png");
        var previous = new[] { Entry("https://example.com/", original.Key) };
        WebShortcut[] replacement = removeShortcut ? [] : [previous[0] with { ManualIconKey = null }];
        var store = new WebShortcutsStore(ConfigPath);
        Assert.Null((await store.SaveAsync(previous)).ErrorMessage);

        var result = await WebShortcutIconCommit.SaveAsync(store.SaveAsync, icons, previous, replacement, null, CancellationToken.None);

        Assert.Null(result.ErrorMessage);
        Assert.Null(await icons.LoadAsync(original.Key));
        Assert.Equal(replacement.Length, (await new WebShortcutsStore(ConfigPath).LoadAsync()).Items.Count);
        await AssertSourceUnchanged(original);
    }

    [Fact]
    public async Task A_cancelled_save_discards_the_uncommitted_import_with_a_non_cancelled_cleanup_token()
    {
        var icons = new WebShortcutManualIconStore(CacheDirectory);
        var old = await ImportAsync(icons, "old.png");
        var added = await ImportAsync(icons, "added.png");
        var previous = new[] { Entry("https://example.com/", old.Key) };
        var replacement = new[] { previous[0] with { ManualIconKey = added.Key } };
        using var lifetime = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WebShortcutIconCommit.SaveAsync((_, token) =>
        {
            lifetime.Cancel();
            return Task.FromCanceled<WebShortcutsSnapshot>(token);
        }, icons, previous, replacement, added.Key, lifetime.Token));

        Assert.Null(await icons.LoadAsync(added.Key));
        Assert.NotNull(await icons.LoadAsync(old.Key));
        await AssertSourceUnchanged(old);
        await AssertSourceUnchanged(added);
    }

    [Fact]
    public async Task Returned_reference_state_protects_an_import_even_if_the_result_reports_an_error()
    {
        var icons = new WebShortcutManualIconStore(CacheDirectory);
        var old = await ImportAsync(icons, "old.png");
        var added = await ImportAsync(icons, "added.png");
        var previous = new[] { Entry("https://example.com/", old.Key) };
        var replacement = new[] { previous[0] with { ManualIconKey = added.Key } };

        var result = await WebShortcutIconCommit.SaveAsync((_, _) => Task.FromResult(new WebShortcutsSnapshot(replacement, "reported state")),
            icons, previous, replacement, added.Key, CancellationToken.None);

        Assert.NotNull(result.ErrorMessage);
        Assert.NotNull(await icons.LoadAsync(added.Key));
        Assert.NotNull(await icons.LoadAsync(old.Key));
    }

    [Fact]
    public async Task A_cleanup_sharing_violation_does_not_turn_a_committed_save_into_a_failure()
    {
        var icons = new WebShortcutManualIconStore(CacheDirectory);
        var original = await ImportAsync(icons, "original.png");
        var previous = new[] { Entry("https://example.com/", original.Key) };
        var store = new WebShortcutsStore(ConfigPath);
        Assert.Null((await store.SaveAsync(previous)).ErrorMessage);
        WebShortcutsSnapshot result;

        await using (var locked = new FileStream(Path.Combine(CacheDirectory, original.Key), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            result = await WebShortcutIconCommit.SaveAsync(store.SaveAsync, icons, previous, [], null, CancellationToken.None);

        Assert.Null(result.ErrorMessage);
        Assert.Empty((await new WebShortcutsStore(ConfigPath).LoadAsync()).Items);
        Assert.NotNull(await icons.LoadAsync(original.Key)); // A safe orphan is preferable to undoing the user's saved change.
        await AssertSourceUnchanged(original);
    }

    [Fact]
    public async Task A_save_exception_is_preserved_while_the_new_import_is_cleaned()
    {
        var icons = new WebShortcutManualIconStore(CacheDirectory);
        var old = await ImportAsync(icons, "old.png");
        var added = await ImportAsync(icons, "added.png");
        var previous = new[] { Entry("https://example.com/", old.Key) };
        var expected = new IOException("isolated save failure");

        var actual = await Assert.ThrowsAsync<IOException>(() => WebShortcutIconCommit.SaveAsync((_, _) => Task.FromException<WebShortcutsSnapshot>(expected),
            icons, previous, [previous[0] with { ManualIconKey = added.Key }], added.Key, CancellationToken.None));

        Assert.Same(expected, actual);
        Assert.Null(await icons.LoadAsync(added.Key));
        Assert.NotNull(await icons.LoadAsync(old.Key));
    }

    private async Task<Imported> ImportAsync(WebShortcutManualIconStore icons, string name)
    {
        var sourceDirectory = Path.Combine(directory, "source");
        Directory.CreateDirectory(sourceDirectory);
        var path = Path.Combine(sourceDirectory, name);
        var bitmap = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, new byte[16 * 16 * 4], 16 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var bytes = new MemoryStream();
        encoder.Save(bytes);
        var original = bytes.ToArray();
        await File.WriteAllBytesAsync(path, original);
        var result = await icons.ImportAsync(path);
        Assert.Null(result.ErrorMessage);
        Assert.NotNull(result.Key);
        return new(result.Key!, path, original);
    }

    private static WebShortcut Entry(string target, string key) => new(Guid.NewGuid().ToString("D"), "Example", new Uri(target)) { ManualIconKey = key };
    private static async Task AssertSourceUnchanged(Imported imported) => Assert.Equal(imported.Original, await File.ReadAllBytesAsync(imported.Source));
    private sealed record Imported(string Key, string Source, byte[] Original);

    public void Dispose()
    {
        var resolved = Path.GetFullPath(directory);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("diandao-shortcut-commit-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing cleanup outside the isolated commit test directory.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
