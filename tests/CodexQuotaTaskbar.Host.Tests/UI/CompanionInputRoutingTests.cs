using System.Runtime.ExceptionServices;
using System.Windows;
using CodexQuotaTaskbar.Host.Platform;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class CompanionInputRoutingTests
{
    [Fact]
    public void Current_press_release_state_survives_a_long_hold_and_clears_on_source_failure() => Sta(() =>
    {
        var source = new FakeSource();
        using var visual = new CompanionBallVisual(() => 60_000);
        visual.SetState("01", true); visual.SetTypingInteraction(true);
        using var controller = new KeyboardInteractionController(() => source, visual.Input);
        controller.Update(true, true);
        source.Emit(new(CompanionInputKind.Key, 0x1e, true));
        source.Emit(new(CompanionInputKind.Key, 0x39, true));
        source.Emit(new(CompanionInputKind.Key, 0x1e, true)); // OS repeat must not steal the current paw.
        Assert.Equal((ushort)0x39, visual.TypingFrame.Key);
        source.Emit(new(CompanionInputKind.Key, 0x39, false));
        Assert.Equal((ushort)0x1e, visual.TypingFrame.Key);
        source.Emit(new(CompanionInputKind.MouseButton, IsDown: true, MouseButton: CompanionMouseButton.Right));
        Assert.True(visual.TypingFrame.MouseRightDown);
        source.Fail();
        Assert.Equal((ushort)0, visual.TypingFrame.Key);
        Assert.False(visual.TypingFrame.MouseRightDown);
        Assert.Equal("01", visual.Emotion);
        Assert.False(visual.AnimationRunning); // Never loaded/shown; no desktop render or native registration.
    });

    [Fact]
    public void Hiding_and_reopening_does_not_restore_old_pressed_keys_or_mouse_buttons() => Sta(() =>
    {
        var sources = new List<FakeSource>();
        using var visual = new CompanionBallVisual(() => 0);
        visual.SetState("01", true); visual.SetTypingInteraction(true);
        using var controller = new KeyboardInteractionController(() => { var source = new FakeSource(); sources.Add(source); return source; }, visual.Input);
        controller.Update(true, true);
        sources[0].Emit(new(CompanionInputKind.Key, 0x39, true));
        sources[0].Emit(new(CompanionInputKind.MouseButton, IsDown: true, MouseButton: CompanionMouseButton.Left));
        visual.Visibility = Visibility.Collapsed;
        controller.Update(true, false);
        visual.Visibility = Visibility.Visible;
        controller.Update(true, true);
        Assert.Equal((ushort)0, visual.TypingFrame.Key);
        Assert.False(visual.TypingFrame.MouseLeftDown);
        sources[0].Emit(new(CompanionInputKind.Key, 0x39, true));
        Assert.Equal((ushort)0, visual.TypingFrame.Key);
        sources[1].Emit(new(CompanionInputKind.Key, 0x1e, true));
        Assert.Equal((ushort)0x1e, visual.TypingFrame.Key);
        controller.Update(false, true);
        Assert.Equal((ushort)0, visual.TypingFrame.Key);
    });

    private sealed class FakeSource : IKeyboardActivitySource
    {
        public event Action<CompanionInputEvent>? Activity;
        public bool IsRunning { get; private set; }
        public bool TryStart() => IsRunning = true;
        public void Stop() => IsRunning = false;
        public void Dispose() => IsRunning = false;
        internal void Emit(CompanionInputEvent value) => Activity?.Invoke(value);
        internal void Fail() { IsRunning = false; Emit(new(CompanionInputKind.Reset)); }
    }

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
