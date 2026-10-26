using CodexQuotaTaskbar.Host.Shortcuts;

namespace CodexQuotaTaskbar.Host.Tests.Shortcuts;

public sealed class WebShortcutIconRequestsTests
{
    private static WebShortcut Item(string? key = null) => new("fc167997-590e-4833-aedd-7f38a5278bd0", "example", new("https://example.com/")) { ManualIconKey = key };

    [Fact]
    public void Automatic_completion_cannot_override_a_manual_selection()
    {
        var requests = new WebShortcutIconRequests();
        var automatic = Item();
        Assert.True(requests.TryBegin(automatic, out var old));
        var manual = Item("11111111111111111111111111111111.png");
        requests.Retain([manual]);
        Assert.False(requests.IsCurrent(old, [manual]));
        Assert.True(requests.TryBegin(manual, out var selected));
        Assert.True(requests.IsCurrent(selected, [manual]));
    }

    [Fact]
    public void Restoring_auto_creates_a_new_request_and_rejects_both_older_completions()
    {
        var requests = new WebShortcutIconRequests();
        var automatic = Item();
        requests.TryBegin(automatic, out var oldAuto);
        var manual = Item("11111111111111111111111111111111.png");
        requests.Retain([manual]);
        requests.TryBegin(manual, out var oldManual);
        requests.Retain([automatic]);
        Assert.True(requests.TryBegin(automatic, out var restored));
        Assert.False(requests.IsCurrent(oldAuto, [automatic]));
        Assert.False(requests.IsCurrent(oldManual, [automatic]));
        Assert.True(requests.IsCurrent(restored, [automatic]));
    }

    [Fact]
    public void Replace_delete_and_recreate_do_not_reuse_pending_results()
    {
        var requests = new WebShortcutIconRequests();
        var first = Item("11111111111111111111111111111111.png");
        requests.TryBegin(first, out var old);
        var second = Item("22222222222222222222222222222222.png");
        requests.Retain([second]);
        requests.TryBegin(second, out var next);
        Assert.False(requests.IsCurrent(old, [second]));
        requests.Retain([]);
        Assert.False(requests.IsCurrent(next, []));
        Assert.True(requests.TryBegin(second, out var recreated));
        Assert.NotEqual(next.Revision, recreated.Revision);
        Assert.False(requests.IsCurrent(next, [second]));
    }

    [Fact]
    public void Repeated_render_and_name_updates_do_not_refetch_an_unchanged_selection()
    {
        var requests = new WebShortcutIconRequests();
        var item = Item();
        Assert.True(requests.TryBegin(item, out var first));
        var renamed = item with { Name = "new title" };
        requests.Retain([renamed]);
        Assert.False(requests.TryBegin(renamed, out var same));
        Assert.Equal(first, same);
        Assert.True(requests.IsCurrent(first, [renamed]));
    }
}
