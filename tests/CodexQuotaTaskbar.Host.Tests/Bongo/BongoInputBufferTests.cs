using System.Text.Json;
using CodexQuotaTaskbar.Host.Bongo;
using CodexQuotaTaskbar.Host.Platform;

namespace CodexQuotaTaskbar.Host.Tests.Bongo;

public sealed class BongoInputBufferTests
{
    [Fact]
    public void Repeated_downs_do_not_reorder_held_keys_and_releases_are_retained()
    {
        var buffer = new BongoInputBuffer();
        buffer.Accept(Key(0x1e, true));
        buffer.Accept(Key(0x1e, true));
        buffer.Accept(Key(0x39, true));
        buffer.Accept(Key(0x1e, true));

        var first = buffer.Drain();

        Assert.False(first.Reset);
        Assert.Equal(new[] { new BongoKeyEdge(4, true), new BongoKeyEdge(44, true) }, first.Keys);
        Assert.Equal(2, buffer.HeldKeyCount);
        buffer.Accept(Key(0x39, false));
        buffer.Accept(Key(0x39, false));
        Assert.Equal(new[] { new BongoKeyEdge(44, false) }, buffer.Drain().Keys);
        Assert.Equal(1, buffer.HeldKeyCount);
        buffer.Accept(Key(0x1e, false));
        Assert.Equal(new[] { new BongoKeyEdge(4, false) }, buffer.Drain().Keys);
        Assert.Equal(0, buffer.HeldKeyCount);
    }

    [Fact]
    public void Unknown_keys_and_unmatched_releases_have_no_effect()
    {
        var buffer = new BongoInputBuffer();
        buffer.Accept(Key(0xffff, true));
        buffer.Accept(Key(0x1e, false));
        buffer.Accept(new((CompanionInputKind)99, 0x1e, true));
        buffer.Accept(new(CompanionInputKind.MouseButton, IsDown: true));

        var batch = buffer.Drain();

        Assert.Empty(batch.Keys);
        Assert.False(batch.Reset);
        Assert.False(batch.MouseLeft);
        Assert.False(batch.MouseRight);
        Assert.Equal(0, buffer.HeldKeyCount);
    }

    [Fact]
    public void Left_and_right_modifiers_keep_their_physical_identity()
    {
        var buffer = new BongoInputBuffer();
        buffer.Accept(Key(0x1d, true));
        buffer.Accept(Key(0x11d, true));
        buffer.Accept(Key(0x1d, false));

        Assert.Equal(new[] { new BongoKeyEdge(0xe0, true), new BongoKeyEdge(0xe4, true), new BongoKeyEdge(0xe0, false) }, buffer.Drain().Keys);
        Assert.Equal(1, buffer.HeldKeyCount);
    }

    [Fact]
    public void Mouse_buttons_are_independent_and_complete_short_taps_cannot_stick()
    {
        var buffer = new BongoInputBuffer();
        buffer.Accept(Mouse(CompanionMouseButton.Left, true));
        buffer.Accept(Mouse(CompanionMouseButton.Right, true));
        var both = buffer.Drain();
        Assert.True(both.MouseLeft); Assert.True(both.MouseRight);
        buffer.Accept(Mouse(CompanionMouseButton.Left, false));
        var right = buffer.Drain();
        Assert.False(right.MouseLeft); Assert.True(right.MouseRight);
        buffer.Accept(Mouse(CompanionMouseButton.Right, false));
        buffer.Accept(Mouse(CompanionMouseButton.Left, true));
        buffer.Accept(Mouse(CompanionMouseButton.Left, false));
        var released = buffer.Drain();
        Assert.False(released.MouseLeft); Assert.False(released.MouseRight);
        Assert.Empty(released.Keys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reset_discards_old_edges_and_precedes_only_new_input(bool useEvent)
    {
        var buffer = new BongoInputBuffer();
        buffer.Accept(Key(0x1e, true));
        buffer.Accept(Mouse(CompanionMouseButton.Left, true));
        buffer.Accept(Mouse(CompanionMouseButton.Right, true));
        if (useEvent) buffer.Accept(new(CompanionInputKind.Reset)); else buffer.Reset();
        buffer.Accept(Key(0x30, true));

        var batch = buffer.Drain();

        Assert.True(batch.Reset);
        Assert.Equal(new[] { new BongoKeyEdge(5, true) }, batch.Keys);
        Assert.False(batch.MouseLeft); Assert.False(batch.MouseRight);
        Assert.Equal(1, buffer.HeldKeyCount);
        Assert.False(buffer.Drain().Reset);
    }

    [Fact]
    public void Draining_has_a_fixed_limit_without_losing_queued_releases()
    {
        var buffer = new BongoInputBuffer();
        var expected = new List<BongoKeyEdge>();
        for (var index = 0; index < 70; index++)
        {
            buffer.Accept(Key(0x1e, true)); buffer.Accept(Key(0x1e, false));
            expected.Add(new(4, true)); expected.Add(new(4, false));
        }

        var first = buffer.Drain(); var second = buffer.Drain(); var third = buffer.Drain();

        Assert.Equal(64, first.Keys.Count); Assert.Equal(64, second.Keys.Count); Assert.Equal(12, third.Keys.Count);
        Assert.Equal(expected, first.Keys.Concat(second.Keys).Concat(third.Keys));
        Assert.Empty(buffer.Drain().Keys);
        Assert.Equal(0, buffer.HeldKeyCount);
    }

    [Fact]
    public void Overflow_releases_everything_and_never_preserves_a_sticky_key_or_button()
    {
        var buffer = new BongoInputBuffer();
        buffer.Accept(Mouse(CompanionMouseButton.Right, true));
        for (var index = 0; index < BongoInputBuffer.MaximumPendingEdges / 2; index++)
        { buffer.Accept(Key(0x1e, true)); buffer.Accept(Key(0x1e, false)); }
        Assert.Equal(BongoInputBuffer.MaximumPendingEdges, buffer.PendingEdgeCount);

        buffer.Accept(Key(0x1e, true));
        var batch = buffer.Drain();

        Assert.True(batch.Reset);
        Assert.Empty(batch.Keys);
        Assert.False(batch.MouseLeft); Assert.False(batch.MouseRight);
        Assert.Equal(0, buffer.PendingEdgeCount); Assert.Equal(0, buffer.HeldKeyCount);
        buffer.Accept(Key(0x1e, false));
        buffer.Accept(Key(0x30, true));
        Assert.Equal(new[] { new BongoKeyEdge(5, true) }, buffer.Drain().Keys);
    }

    [Fact]
    public void Drain_results_are_detached_and_key_json_uses_the_native_contract()
    {
        var buffer = new BongoInputBuffer();
        buffer.Accept(Key(0x1e, true));
        var first = buffer.Drain();
        buffer.Reset();
        Assert.Equal(new[] { new BongoKeyEdge(4, true) }, first.Keys);
        Assert.Throws<NotSupportedException>(() => ((IList<BongoKeyEdge>)first.Keys)[0] = new(5, false));
        Assert.Equal("{\"hid\":4,\"down\":true}", JsonSerializer.Serialize(first.Keys[0]));
    }

    [Fact]
    public async Task Concurrent_producers_and_consumer_cannot_grow_the_queue_beyond_its_bound()
    {
        var buffer = new BongoInputBuffer();
        var producer = Task.Run(() => Parallel.For(0, 10_000, index =>
        {
            buffer.Accept(Key((ushort)(index % 2 == 0 ? 0x1e : 0x30), true));
            buffer.Accept(Key((ushort)(index % 2 == 0 ? 0x1e : 0x30), false));
            Assert.InRange(buffer.PendingEdgeCount, 0, BongoInputBuffer.MaximumPendingEdges);
        }));
        while (!producer.IsCompleted)
        {
            Assert.InRange(buffer.Drain().Keys.Count, 0, BongoInputBuffer.MaximumEdgesPerBatch);
            await Task.Yield();
        }
        await producer;
        buffer.Reset();
        var final = buffer.Drain();
        Assert.True(final.Reset); Assert.Empty(final.Keys);
        Assert.False(final.MouseLeft); Assert.False(final.MouseRight);
        Assert.Equal(0, buffer.HeldKeyCount); Assert.Equal(0, buffer.PendingEdgeCount);
    }

    private static CompanionInputEvent Key(ushort scan, bool down) => new(CompanionInputKind.Key, scan, down);
    private static CompanionInputEvent Mouse(CompanionMouseButton button, bool down) => new(CompanionInputKind.MouseButton, IsDown: down, MouseButton: button);
}
