using CodexQuotaTaskbar.Host.Platform;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

// Synthetic physical state only; no registration, simulated OS keys or input history.
public sealed class CompanionTypingMotionTests
{
    [Fact]
    public void Off_by_default_and_enable_does_not_press_any_key()
    {
        var motion = new CompanionTypingMotion();
        Assert.False(motion.Input(Key(30, true)));
        Assert.Equal(CompanionTypingFrame.Rest, motion.Sample(0));
        motion.SetEnabled(true);
        Assert.Equal(CompanionTypingFrame.Idle, motion.Sample(0));
        Assert.Equal(0, motion.HeldCount);
    }

    [Fact]
    public void Long_hold_remains_on_the_same_key_until_its_release_without_a_timer_tail()
    {
        var motion = Enabled();
        Assert.True(motion.Input(Key(30, true)));
        Assert.Equal((ushort)30, motion.Sample(0).Key);
        Assert.Equal(motion.Sample(0), motion.Sample(60_000));
        Assert.True(motion.Input(Key(30, false)));
        Assert.Equal(CompanionTypingFrame.Idle, motion.Sample(60_001));
    }

    [Fact]
    public void Duplicate_keydown_does_not_reorder_held_keys_or_retrigger_a_paw()
    {
        var motion = Enabled();
        motion.Input(Key(30, true));
        motion.Input(Key(48, true));
        var before = motion.Sample(10);
        for (var i = 0; i < 100; i++) Assert.False(motion.Input(Key(30, true)));
        Assert.Equal(before, motion.Sample(10_000));
        Assert.Equal((ushort)48, before.Key);
        Assert.Equal(2, motion.HeldCount);
        motion.Input(Key(48, false));
        Assert.Equal((ushort)30, motion.Sample(10_001).Key);
    }

    [Fact]
    public void Chord_release_falls_back_to_last_still_held_key_in_press_order()
    {
        var motion = Enabled();
        motion.Input(Key(29, true));
        motion.Input(Key(42, true));
        motion.Input(Key(30, true));
        Assert.Equal((ushort)30, motion.Sample(0).Key);
        motion.Input(Key(30, false));
        Assert.Equal((ushort)42, motion.Sample(1).Key);
        motion.Input(Key(29, false));
        Assert.Equal((ushort)42, motion.Sample(2).Key);
        motion.Input(Key(42, false));
        Assert.Equal(CompanionTypingFrame.Idle, motion.Sample(3));
    }

    [Fact]
    public void Unmapped_function_key_or_unmatched_release_does_not_steal_the_paw()
    {
        var motion = Enabled();
        motion.Input(Key(30, true));
        Assert.False(motion.Input(Key(0x3b, true)));
        Assert.False(motion.Input(Key(0x3b, false)));
        Assert.False(motion.Input(Key(31, false)));
        Assert.Equal((ushort)30, motion.Sample(0).Key);
        Assert.Equal(1, motion.HeldCount);
    }

    [Fact]
    public void Fast_down_up_edges_are_not_throttled_or_saved_as_future_animation()
    {
        var motion = Enabled();
        for (var i = 0; i < 1000; i++)
        {
            var key = CompanionTypingKeyboard.Keys[i % CompanionTypingKeyboard.Keys.Count].Scan;
            Assert.True(motion.Input(Key(key, true)));
            Assert.Equal(key, motion.Sample(1).Key);
            Assert.True(motion.Input(Key(key, false)));
            Assert.Equal(0, motion.HeldCount);
        }
        Assert.Equal(CompanionTypingFrame.Idle, motion.Sample(60_000));
    }

    [Fact]
    public void Held_state_is_bounded_by_the_unique_physical_key_table()
    {
        var motion = Enabled();
        foreach (var key in CompanionTypingKeyboard.Keys) motion.Input(Key(key.Scan, true));
        Assert.Equal(61, motion.HeldCount);
        foreach (var key in CompanionTypingKeyboard.Keys) Assert.False(motion.Input(Key(key.Scan, true)));
        Assert.Equal(61, motion.HeldCount);
        foreach (var key in CompanionTypingKeyboard.Keys.Reverse()) motion.Input(Key(key.Scan, false));
        Assert.Equal(0, motion.HeldCount);
    }

    [Theory]
    [InlineData(29, 0x11d)]
    [InlineData(56, 0x138)]
    [InlineData(42, 54)]
    public void Both_physical_sides_of_modifiers_are_independent(int left, int right)
    {
        var motion = Enabled();
        motion.Input(Key((ushort)left, true));
        motion.Input(Key((ushort)right, true));
        Assert.Equal((ushort)right, motion.Sample(0).Key);
        motion.Input(Key((ushort)right, false));
        Assert.Equal((ushort)left, motion.Sample(0).Key);
    }

    [Fact]
    public void Mouse_buttons_are_independent_of_each_other_and_keyboard_paw()
    {
        var motion = Enabled();
        motion.Input(Key(30, true));
        motion.Input(Mouse(CompanionMouseButton.Left, true));
        motion.Input(Mouse(CompanionMouseButton.Right, true));
        Assert.True(motion.Sample(0).MouseLeftDown);
        Assert.True(motion.Sample(0).MouseRightDown);
        motion.Input(Mouse(CompanionMouseButton.Left, false));
        Assert.False(motion.Sample(0).MouseLeftDown);
        Assert.True(motion.Sample(0).MouseRightDown);
        Assert.Equal((ushort)30, motion.Sample(0).Key);
        motion.Input(Mouse(CompanionMouseButton.Right, false));
        Assert.False(motion.Sample(0).MouseRightDown);
    }

    [Fact]
    public void Pointer_is_direction_preserving_bounded_and_does_not_change_key_state()
    {
        var motion = Enabled();
        motion.Input(Key(30, true));
        motion.Pointer(3, 4);
        Assert.Equal(.6, motion.Sample(0).MouseX, 8);
        Assert.Equal(.8, motion.Sample(0).MouseY, 8);
        var before = motion.Sample(0);
        motion.Pointer(double.NaN, 0);
        Assert.Equal(before, motion.Sample(0));
        Assert.Equal((ushort)30, motion.Sample(0).Key);
    }

    [Fact]
    public void New_pointer_targets_ease_on_the_existing_clock_without_extra_samples_or_overshoot()
    {
        var motion = Enabled();
        Assert.Equal(0, motion.Sample(0).MouseX);
        motion.Pointer(1, 0);
        Assert.Equal(0, motion.Sample(0).MouseX);
        var early = motion.Sample(33).MouseX;
        Assert.InRange(early, .3, .5);
        Assert.InRange(motion.Sample(66).MouseX, early, 1);
        Assert.InRange(motion.Sample(1000).MouseX, .99, 1);
        motion.Input(new(CompanionInputKind.Reset));
        Assert.Equal(CompanionTypingFrame.Idle, motion.Sample(1000));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reset_or_disable_clears_every_held_key_and_button_without_replay(bool disable)
    {
        var motion = Enabled();
        motion.Input(Key(30, true)); motion.Input(Key(29, true));
        motion.Input(Mouse(CompanionMouseButton.Left, true));
        motion.Input(Mouse(CompanionMouseButton.Right, true));
        if (disable) motion.SetEnabled(false);
        else motion.Input(new(CompanionInputKind.Reset));
        Assert.Equal(0, motion.HeldCount);
        if (disable) Assert.Equal(CompanionTypingFrame.Rest, motion.Sample(1));
        motion.SetEnabled(true);
        Assert.Equal(CompanionTypingFrame.Idle, motion.Sample(60_000));
        Assert.False(motion.Input(Key(30, false)));
    }

    [Fact]
    public void Each_keyboard_row_has_a_complete_fifteen_unit_footprint_and_distinct_scans()
    {
        Assert.Equal(61, CompanionTypingKeyboard.Keys.Count);
        Assert.Equal(61, CompanionTypingKeyboard.Keys.Select(k => k.Scan).Distinct().Count());
        foreach (var row in CompanionTypingKeyboard.Keys.GroupBy(k => k.Y))
            Assert.Equal(15d, row.Sum(k => k.Width));
        Assert.True(CompanionTypingKeyboard.TryGet(30, out var a));
        Assert.True(CompanionTypingKeyboard.TryGet(57, out var space));
        Assert.NotEqual(CompanionTypingKeyboard.Project(a.X, a.Y), CompanionTypingKeyboard.Project(space.X, space.Y));
        Assert.True(space.Width > a.Width * 5);
    }

    private static CompanionTypingMotion Enabled() { var motion = new CompanionTypingMotion(); motion.SetEnabled(true); return motion; }
    private static CompanionInputEvent Key(ushort scan, bool down) => new(CompanionInputKind.Key, scan, down);
    private static CompanionInputEvent Mouse(CompanionMouseButton button, bool down) => new(CompanionInputKind.MouseButton, 0, down, button);
}
