using System.Text;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.Provider;

namespace CodexQuotaTaskbar.Host.Tests.Provider;

public sealed class DotUnreadStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "CodexQuotaTaskbar.Tests", "Unread", Guid.NewGuid().ToString("N"));
    private string StatePath => Path.Combine(directory, "dot-unread.json");

    [Fact]
    public async Task Missing_state_does_not_create_a_directory_and_roundtrip_preserves_unread()
    {
        var store = new DotUnreadStore(StatePath);
        Assert.Null(await store.LoadAsync());
        Assert.False(store.LastLoadInvalid);
        Assert.False(Directory.Exists(directory));
        var inbox = Inbox(70); inbox.MarkRead("message-69");
        await store.SaveAsync(inbox.Capture());
        var snapshot = Assert.IsType<DotUnreadSnapshot>(await new DotUnreadStore(StatePath).LoadAsync());
        var restored = new DotUnreadInbox(); restored.Restore(snapshot);
        Assert.Equal(69, restored.UnreadCount);
        Assert.Equal(6, restored.EarlierUnreadCount);
        Assert.False(restored.ObserveNotification(Message("message-69", "stream after restart")));
        Assert.False(restored.Messages[^1].IsUnread);
        Assert.True(restored.MarkRead("message-0"));
        Assert.Equal(5, restored.EarlierUnreadCount);
    }

    [Fact]
    public async Task A_valid_persisted_snapshot_produces_a_valid_inline_wire_state_at_id_length_boundary()
    {
        var inbox = new DotUnreadInbox(); inbox.ObserveNotification(Message(new string('x', 256)));
        var store = new DotUnreadStore(StatePath); await store.SaveAsync(inbox.Capture());
        var saved = Assert.IsType<DotUnreadSnapshot>(await store.LoadAsync());
        var state = new DotInlineState(Guid.NewGuid().ToString("N"), 1, 0, "",
            saved.Messages.Select(message => new DotInlineMessage(message.Id, message.Text, message.Time, message.IsUnread)).ToArray(),
            1, 0, saved.Messages[0].Id, 1, true, false, false, false, true, false, "connected");
        Assert.True(DotInlineWire.ValidState(state));
        Assert.True(DotInlineWire.ValidState(Assert.IsType<DotInlineState>(
            DotInlineWire.Deserialize<DotInlineState>(DotInlineWire.Serialize(state)))));
    }

    [Fact]
    public async Task Too_long_persisted_id_is_rejected_as_invalid_cache_instead_of_breaking_the_pipe()
    {
        Directory.CreateDirectory(directory);
        var id = new string('x', 257);
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1, messages = new[] { new { id, text = "synthetic", time = (DateTimeOffset?)null, isUnread = true } },
            knownIds = new[] { id }, earlierUnreadCount = 0,
        });
        await File.WriteAllBytesAsync(StatePath, bytes);
        var store = new DotUnreadStore(StatePath);
        Assert.Null(await store.LoadAsync());
        Assert.True(store.LastLoadInvalid);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(StatePath));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"schema\":1,\"messages\":[],\"knownIds\":[],\"earlierUnreadCount\":-1}")]
    [InlineData("{\"schema\":2,\"messages\":[],\"knownIds\":[],\"earlierUnreadCount\":0}")]
    public async Task Corrupt_state_is_unchanged_on_load_and_preserved_before_safe_replacement(string corrupt)
    {
        Directory.CreateDirectory(directory); await File.WriteAllTextAsync(StatePath, corrupt);
        var original = await File.ReadAllBytesAsync(StatePath);
        var store = new DotUnreadStore(StatePath);
        Assert.Null(await store.LoadAsync());
        Assert.True(store.LastLoadInvalid);
        Assert.Equal(original, await File.ReadAllBytesAsync(StatePath));
        await store.SaveAsync(Inbox(1).Capture());
        Assert.Equal(original, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(directory, "*.bak"))));
        Assert.Single(Assert.IsType<DotUnreadSnapshot>(await store.LoadAsync()).Messages);
        Assert.False(store.LastLoadInvalid);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task Oversized_state_is_not_overwritten_or_copied_as_an_unbounded_backup()
    {
        Directory.CreateDirectory(directory);
        await using (var file = File.Create(StatePath)) file.SetLength(DotUnreadStore.MaximumFileBytes + 1L);
        var store = new DotUnreadStore(StatePath);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        Assert.True(store.LastLoadInvalid);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(Inbox(1).Capture()));
        Assert.Equal(DotUnreadStore.MaximumFileBytes + 1L, new FileInfo(StatePath).Length);
        Assert.Empty(Directory.GetFiles(directory, "*.bak"));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task Canceled_operations_preserve_existing_state_and_leave_no_temporary_files()
    {
        var store = new DotUnreadStore(StatePath); await store.SaveAsync(Inbox(1).Capture());
        var original = await File.ReadAllBytesAsync(StatePath);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(Inbox(2).Capture(), cancellation.Token));
        Assert.Equal(original, await File.ReadAllBytesAsync(StatePath));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task Concurrent_saves_are_serial_and_always_leave_a_complete_valid_snapshot()
    {
        var store = new DotUnreadStore(StatePath);
        await Task.WhenAll(Enumerable.Range(1, 24).Select(count => store.SaveAsync(Inbox(count).Capture())));
        var saved = Assert.IsType<DotUnreadSnapshot>(await store.LoadAsync());
        Assert.InRange(saved.Messages.Length, 1, 24);
        Assert.True(DotUnreadInbox.IsValid(saved));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task Directory_destination_is_rejected_without_modifying_it()
    {
        Directory.CreateDirectory(StatePath);
        var store = new DotUnreadStore(StatePath);
        await Assert.ThrowsAsync<IOException>(() => store.LoadAsync());
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(Inbox(1).Capture()));
        Assert.True(Directory.Exists(StatePath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(StatePath));
    }

    [Fact]
    public async Task A_regular_file_cannot_be_used_as_a_parent_directory()
    {
        Directory.CreateDirectory(directory);
        var parent = Path.Combine(directory, "parent-file");
        await File.WriteAllTextAsync(parent, "protected synthetic data");
        var store = new DotUnreadStore(Path.Combine(parent, "state.json"));
        await Assert.ThrowsAsync<IOException>(() => store.LoadAsync());
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(Inbox(1).Capture()));
        Assert.Equal("protected synthetic data", await File.ReadAllTextAsync(parent));
    }

    [Fact]
    public async Task Invalid_snapshot_never_creates_state()
    {
        var store = new DotUnreadStore(StatePath);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new(1, [], [], -1)));
        Assert.False(File.Exists(StatePath));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task Unknown_json_fields_are_not_silently_accepted()
    {
        Directory.CreateDirectory(directory);
        var bytes = Encoding.UTF8.GetBytes("{\"schema\":1,\"messages\":[],\"knownIds\":[],\"earlierUnreadCount\":0,\"extra\":true}");
        await File.WriteAllBytesAsync(StatePath, bytes);
        Assert.Null(await new DotUnreadStore(StatePath).LoadAsync());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(StatePath));
    }

    private static DotUnreadInbox Inbox(int count)
    {
        var inbox = new DotUnreadInbox();
        for (var index = 0; index < count; index++) inbox.ObserveNotification(Message($"message-{index}"));
        return inbox;
    }

    private static DotMessage Message(string id, string text = "synthetic notification") => new(id, "assistant", text,
        DateTimeOffset.Parse("2026-10-24T16:00:00Z"));

    public void Dispose()
    {
        var resolved = Path.GetFullPath(directory);
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CodexQuotaTaskbar.Tests", "Unread")) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test cleanup root.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
