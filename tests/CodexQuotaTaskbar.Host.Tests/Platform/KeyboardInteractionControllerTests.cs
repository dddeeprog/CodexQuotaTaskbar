using CodexQuotaTaskbar.Host.Platform;

namespace CodexQuotaTaskbar.Host.Tests.Platform;

public sealed class KeyboardInteractionControllerTests
{
    [Fact]
    public void Disabled_or_invisible_never_constructs_a_native_source()
    {
        using var controller = new KeyboardInteractionController(() => throw new Exception("Must not be constructed"), _ => throw new Exception("No input expected"));
        controller.Update(false, false);
        controller.Update(false, true);
        controller.Update(true, false);
        Assert.False(controller.IsRunning);
    }

    [Fact]
    public void Active_source_is_started_once_and_passes_every_press_and_release_in_order()
    {
        var source = new FakeSource(); var events = new List<CompanionInputEvent>(); var created = 0;
        using var controller = new KeyboardInteractionController(() => { created++; return source; }, events.Add);
        controller.Update(true, true);
        controller.Update(true, true);
        var down = new CompanionInputEvent(CompanionInputKind.Key, 0x1e, true);
        var up = down with { IsDown = false };
        source.Raise(down); source.Raise(up);
        Assert.True(controller.IsRunning);
        Assert.Equal(1, created);
        Assert.Equal(1, source.Starts);
        Assert.Equal(new[] { down, up }, events);
        controller.Update(false, true);
        source.Raise(down);
        Assert.Equal(3, events.Count);
        Assert.Equal(CompanionInputKind.Reset, events[^1].Kind);
        Assert.Equal(1, source.Stops);
        Assert.Equal(1, source.Disposes);
    }

    [Fact]
    public void Hiding_or_reducing_motion_releases_state_and_resuming_creates_a_new_source()
    {
        var sources = new List<FakeSource>(); var events = new List<CompanionInputEvent>();
        using var controller = new KeyboardInteractionController(() => { var item = new FakeSource(); sources.Add(item); return item; }, events.Add);
        controller.Update(true, true);
        sources[0].Raise(new(CompanionInputKind.Key, 0x39, true));
        controller.Update(true, false);
        Assert.False(controller.IsRunning);
        Assert.Equal(CompanionInputKind.Reset, events[^1].Kind);
        Assert.Equal(1, sources[0].Disposes);
        controller.Update(true, true);
        Assert.Equal(2, sources.Count);
        Assert.True(controller.IsRunning);
        sources[0].Raise(new(CompanionInputKind.Key, 0x1e, true));
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void Source_failure_reset_passes_after_running_flag_is_cleared_but_later_edges_do_not()
    {
        var source = new FakeSource(); var events = new List<CompanionInputEvent>();
        using var controller = new KeyboardInteractionController(() => source, events.Add);
        controller.Update(true, true);
        source.Raise(new(CompanionInputKind.MouseButton, IsDown: true, MouseButton: CompanionMouseButton.Left));
        source.Fail();
        source.Raise(new(CompanionInputKind.Key, 0x39, true));
        Assert.Equal(2, events.Count);
        Assert.Equal(CompanionInputKind.Reset, events[^1].Kind);
        Assert.False(controller.IsRunning);
    }

    [Fact]
    public void Registration_failure_is_closed_and_does_not_loop_until_the_gate_changes()
    {
        var attempts = 0; var failed = new FakeSource { StartSucceeds = false };
        using var controller = new KeyboardInteractionController(() => { attempts++; return attempts == 1 ? failed : new FakeSource(); }, _ => { });
        controller.Update(true, true);
        Assert.True(controller.Unavailable);
        Assert.False(controller.IsRunning);
        controller.Update(true, true);
        Assert.Equal(1, attempts);
        Assert.Equal(1, failed.Disposes);
        controller.Update(false, true);
        controller.Update(true, true);
        Assert.Equal(2, attempts);
        Assert.True(controller.IsRunning);
    }

    [Fact]
    public void Failed_construction_does_not_escape_or_retry_each_tick()
    {
        var attempts = 0;
        using var controller = new KeyboardInteractionController(() => { attempts++; throw new InvalidOperationException(); }, _ => { });
        controller.Update(true, true); controller.Update(true, true);
        Assert.True(controller.Unavailable);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void Disposing_clears_held_state_unsubscribes_and_never_restarts()
    {
        var source = new FakeSource(); var events = new List<CompanionInputEvent>();
        var controller = new KeyboardInteractionController(() => source, events.Add);
        controller.Update(true, true);
        controller.Dispose(); controller.Dispose();
        source.Raise(new(CompanionInputKind.Key, 0x1e, true)); controller.Update(true, true);
        Assert.Equal(CompanionInputKind.Reset, Assert.Single(events).Kind);
        Assert.Equal(1, source.Starts);
        Assert.Equal(1, source.Stops);
        Assert.Equal(1, source.Disposes);
    }

    private sealed class FakeSource : IKeyboardActivitySource
    {
        public event Action<CompanionInputEvent>? Activity;
        public bool IsRunning { get; private set; }
        internal bool StartSucceeds = true;
        internal int Starts, Stops, Disposes;
        public bool TryStart() { Starts++; return IsRunning = StartSucceeds; }
        public void Stop() { Stops++; IsRunning = false; }
        public void Dispose() { Disposes++; IsRunning = false; }
        internal void Raise(CompanionInputEvent value) => Activity?.Invoke(value);
        internal void Fail() { IsRunning = false; Raise(new(CompanionInputKind.Reset)); }
    }
}
