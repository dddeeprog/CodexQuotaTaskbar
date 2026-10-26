using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.Overlay;
using CodexQuotaTaskbar.Host.Resets;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

// Unshown controls deliberately finish immediately. True WPF clock frames,
// cancellation during a visible transition and physical anchoring are proved by
// the task's independent NoActivate native fixture, never a user's window.
[Collection("Native UI isolation")]
public sealed class ExtensionMotionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T18:30:00Z");

    [Theory]
    [InlineData(0d, 37d)] [InlineData(0d, 26d)] [InlineData(12.5d, 37d)]
    public void Expansion_uses_two_hundred_ms_monotone_ease_out_without_bounce(double from, double to) => Sta(() =>
    {
        var animation = QuotaCapsuleWindow.CreateExtensionHeightAnimation(from, to);
        Assert.Equal(from, animation.From);
        Assert.Equal(to, animation.To);
        Assert.Equal(TimeSpan.FromMilliseconds(200), animation.Duration.TimeSpan);
        Assert.Null(Timeline.GetDesiredFrameRate(animation));
        var easing = Assert.IsType<CubicEase>(animation.EasingFunction);
        Assert.Equal(EasingMode.EaseOut, easing.EasingMode);
        Assert.True(easing.Ease(.5) > .5);
        AssertMonotone(easing);
    });

    [Theory]
    [InlineData(37d, 0d)] [InlineData(26d, 0d)] [InlineData(29.5d, 10d)]
    public void Retraction_uses_one_hundred_sixty_ms_monotone_ease_in_without_bounce(double from, double to) => Sta(() =>
    {
        var animation = QuotaCapsuleWindow.CreateExtensionHeightAnimation(from, to);
        Assert.Equal(from, animation.From);
        Assert.Equal(to, animation.To);
        Assert.Equal(TimeSpan.FromMilliseconds(160), animation.Duration.TimeSpan);
        Assert.Null(Timeline.GetDesiredFrameRate(animation));
        var easing = Assert.IsType<CubicEase>(animation.EasingFunction);
        Assert.Equal(EasingMode.EaseIn, easing.EasingMode);
        Assert.True(easing.Ease(.5) < .5);
        AssertMonotone(easing);
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Unshown_top_and_footer_extensions_settle_without_moving_or_replacing_the_middle(bool footerFirst) => Sta(() =>
    {
        var owner = new QuotaCapsuleWindow("extension-unshown", true);
        try
        {
            owner.ApplySessions(Sessions());
            var list = Named<StackPanel>(owner, "PanelSessionsList");
            var tasks = list.Children.Cast<UIElement>().ToArray();
            var coreHeight = owner.CoreBodyDesiredHeight;
            var coreReserved = owner.CoreBodyReservedHeight;
            var notice = Notice("Synthetic short summary");
            if (footerFirst) owner.ApplyResetSchedule(Fresh(), Now, TimeZoneInfo.Utc);
            owner.ShowDotTopExtension(notice, notice.PreferredHeightDip);
            if (!footerFirst) owner.ApplyResetSchedule(Fresh(), Now, TimeZoneInfo.Utc);
            Assert.Equal(37, owner.DotTopExtensionHeight);
            Assert.Equal(37, owner.DotTopExtensionTargetHeight);
            Assert.Equal(26, owner.ResetScheduleHeightForLayout);
            Assert.Equal(26, owner.ResetScheduleTargetHeightForLayout);
            Assert.Equal(coreHeight + 63, owner.DesiredHeight);
            Assert.Equal(coreReserved + 63, owner.ReservedHeight);
            Assert.Equal(coreHeight, owner.CoreBodyDesiredHeight);
            Assert.Equal(coreReserved, owner.CoreBodyReservedHeight);
            Assert.Equal(tasks, list.Children.Cast<UIElement>());
            Assert.False(owner.TopExtensionAnimating);
            Assert.False(owner.ResetScheduleAnimating);
            Assert.False(owner.IsVisible);
            Assert.Equal(IntPtr.Zero, new WindowInteropHelper(owner).Handle);
            owner.HideDotTopExtension(animate: true); // hidden/unloaded never keeps an outgoing child
            owner.ApplyResetSchedule(new(null, Now, false, null), Now, TimeZoneInfo.Utc);
            Assert.Equal(coreHeight, owner.DesiredHeight);
            Assert.Equal(0, owner.DotTopExtensionHeight);
            Assert.Equal(0, owner.ResetScheduleHeightForLayout);
            Assert.Null(owner.DotTopContent);
            Assert.Equal(tasks, list.Children.Cast<UIElement>());
        }
        finally { owner.Close(); }
    });

    [Fact]
    public void Repeated_same_targets_and_minute_text_updates_do_not_restart_motion_or_emit_layout() => Sta(() =>
    {
        var owner = new QuotaCapsuleWindow("extension-dedup", true);
        try
        {
            var notice = Notice("Synthetic first summary");
            owner.ShowDotTopExtension(notice, notice.PreferredHeightDip);
            owner.ApplyResetSchedule(Fresh(), Now, TimeZoneInfo.Utc);
            var generation = owner.ExtensionMotionGeneration;
            var layoutCount = owner.ExtensionMotionLayoutCount;
            var changes = 0;
            owner.PresentationChanged += (_, _) => changes++;
            for (var index = 0; index < 120; index++)
            {
                notice.Display(new("message", "Synthetic updated summary " + index, null, true), 3);
                owner.ShowDotTopExtension(notice, notice.PreferredHeightDip);
                owner.RefreshResetSchedule(Now.AddSeconds(index), TimeZoneInfo.Utc);
            }
            Assert.Equal(generation, owner.ExtensionMotionGeneration);
            Assert.Equal(layoutCount, owner.ExtensionMotionLayoutCount);
            Assert.Equal(0, changes);
            Assert.Equal(37, owner.DotTopExtensionHeight);
            Assert.Equal(26, owner.ResetScheduleHeightForLayout);
        }
        finally { owner.Close(); }
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Replacing_a_notice_with_a_reply_and_back_does_not_discard_draft_or_selection(bool reduced) => Sta(() =>
    {
        var owner = new QuotaCapsuleWindow("extension-draft", true);
        try
        {
            owner.SetCompanion(false, reduced);
            var reply = new DotInlineView();
            reply.InputControl.Text = "Synthetic unsent draft\nsecond line";
            reply.InputControl.Select(2, 5);
            owner.ShowInlineDot(reply);
            var draft = reply.DraftText;
            var notice = Notice("Synthetic cat summary");
            owner.HideDotTopExtension();
            owner.ShowDotTopExtension(notice, notice.PreferredHeightDip);
            owner.FinishExtensionTransitions();
            owner.HideDotTopExtension(animate: true);
            owner.ShowInlineDot(reply);
            owner.FinishExtensionTransitions();
            Assert.Same(reply, owner.DotTopContent);
            Assert.Equal(draft, reply.DraftText);
            Assert.Equal(2, reply.InputControl.SelectionStart);
            Assert.Equal(5, reply.InputControl.SelectionLength);
            Assert.False(owner.TopExtensionAnimating);
            Assert.Equal(reply.PreferredHeightDip + 1, owner.DotTopExtensionHeight);
            Assert.Equal(owner.DotTopExtensionHeight, owner.DotTopExtensionTargetHeight);
            Assert.False(owner.IsVisible);
        }
        finally { owner.Close(); }
    });

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void Cancelled_stale_expired_or_missing_reset_removes_old_text_and_can_recover(int reason) => Sta(() =>
    {
        var owner = new QuotaCapsuleWindow("extension-reset-validity", true);
        try
        {
            owner.ApplyResetSchedule(Fresh(), Now, TimeZoneInfo.Utc);
            Assert.False(string.IsNullOrWhiteSpace(Named<TextBlock>(owner, "ResetScheduleCountdown").Text));
            var hidden = reason switch
            {
                0 => new ResetScheduleSnapshot(null, Now, false, null),
                1 => Fresh() with { IsStale = true },
                2 => Fresh() with { Schedule = Fresh().Schedule! with { ScheduledFor = Now } },
                _ => Fresh() with { ErrorMessage = "Synthetic offline" },
            };
            owner.ApplyResetSchedule(hidden, Now, TimeZoneInfo.Utc);
            Assert.Equal(0, owner.ResetScheduleHeightForLayout);
            Assert.Equal(0, owner.ResetScheduleTargetHeightForLayout);
            Assert.Equal(Visibility.Collapsed, Named<Border>(owner, "ResetScheduleRow").Visibility);
            Assert.Empty(Named<TextBlock>(owner, "ResetScheduleCountdown").Text);
            Assert.Empty(Named<TextBlock>(owner, "ResetScheduleLocalTime").Text);
            owner.ApplyResetSchedule(Fresh() with { LastUpdatedAt = Now.AddSeconds(1) }, Now.AddSeconds(1), TimeZoneInfo.Utc);
            Assert.Equal(26, owner.ResetScheduleHeightForLayout);
            Assert.Equal(26, owner.ResetScheduleTargetHeightForLayout);
            Assert.Equal(Visibility.Visible, Named<Border>(owner, "ResetScheduleRow").Visibility);
        }
        finally { owner.Close(); }
    });

    [Fact]
    public void Finish_transitions_is_idempotent_and_does_not_detach_an_active_notice() => Sta(() =>
    {
        var owner = new QuotaCapsuleWindow("extension-finish", true);
        try
        {
            var notice = Notice("Synthetic active reminder");
            owner.ShowDotTopExtension(notice, notice.PreferredHeightDip);
            owner.ApplyResetSchedule(Fresh(), Now, TimeZoneInfo.Utc);
            owner.FinishExtensionTransitions();
            var layout = owner.ExtensionMotionLayoutCount;
            var changes = 0;
            owner.PresentationChanged += (_, _) => changes++;
            for (var index = 0; index < 20; index++) owner.FinishExtensionTransitions();
            Assert.Same(notice, owner.DotTopContent);
            Assert.True(owner.DotTopExtensionVisible);
            Assert.Equal(37, owner.DotTopExtensionHeight);
            Assert.Equal(26, owner.ResetScheduleHeightForLayout);
            Assert.False(owner.TopExtensionAnimating);
            Assert.False(owner.ResetScheduleAnimating);
            Assert.Equal(layout, owner.ExtensionMotionLayoutCount);
            Assert.Equal(0, changes);
            owner.HideDotTopExtension();
            Assert.Null(notice.Parent);
            Assert.Null(owner.DotTopContent);
        }
        finally { owner.Close(); }
    });

    [Fact]
    public void Short_work_area_suppression_preserves_valid_reset_data_and_restores_the_same_view() => Sta(() =>
    {
        var owner = new QuotaCapsuleWindow("extension-footer-suppress", true);
        try
        {
            var snapshot = Fresh();
            owner.ApplyResetSchedule(snapshot, Now, TimeZoneInfo.Utc);
            var presentation = Field<object>(owner, "resetScheduleView");
            var countdown = Named<TextBlock>(owner, "ResetScheduleCountdown").Text;
            var localTime = Named<TextBlock>(owner, "ResetScheduleLocalTime").Text;
            var tooltip = Named<Border>(owner, "ResetScheduleRow").ToolTip;
            owner.SetResetSchedulePresentationAllowed(false);
            Assert.Equal(0, owner.ResetScheduleHeightForLayout);
            Assert.Equal(0, owner.ResetScheduleTargetHeightForLayout);
            Assert.False(owner.ResetScheduleAnimating);
            Assert.Equal(Visibility.Collapsed, Named<Border>(owner, "ResetScheduleRow").Visibility);
            Assert.Same(snapshot, Field<ResetScheduleSnapshot>(owner, "resetSchedule"));
            Assert.Same(presentation, Field<object>(owner, "resetScheduleView"));
            Assert.Equal(countdown, Named<TextBlock>(owner, "ResetScheduleCountdown").Text);
            Assert.Equal(localTime, Named<TextBlock>(owner, "ResetScheduleLocalTime").Text);
            owner.SetResetSchedulePresentationAllowed(true);
            Assert.Equal(26, owner.ResetScheduleHeightForLayout);
            Assert.Equal(26, owner.ResetScheduleTargetHeightForLayout);
            Assert.False(owner.ResetScheduleAnimating); // unshown control still switches directly
            Assert.Same(snapshot, Field<ResetScheduleSnapshot>(owner, "resetSchedule"));
            Assert.Same(presentation, Field<object>(owner, "resetScheduleView"));
            Assert.Same(tooltip, Named<Border>(owner, "ResetScheduleRow").ToolTip);
            Assert.Equal(countdown, Named<TextBlock>(owner, "ResetScheduleCountdown").Text);
            Assert.Equal(localTime, Named<TextBlock>(owner, "ResetScheduleLocalTime").Text);
        }
        finally { owner.Close(); }
    });

    [Fact]
    public void Reset_expiry_or_cancellation_while_suppressed_cannot_be_revived_when_space_returns() => Sta(() =>
    {
        foreach (var expired in new[] { false, true })
        {
            var owner = new QuotaCapsuleWindow("extension-footer-suppress-expiry", true);
            try
            {
                owner.ApplyResetSchedule(Fresh(), Now, TimeZoneInfo.Utc);
                owner.SetResetSchedulePresentationAllowed(false);
                if (expired) owner.RefreshResetSchedule(Now.AddHours(1), TimeZoneInfo.Utc);
                else owner.ApplyResetSchedule(new(null, Now, false, null), Now, TimeZoneInfo.Utc);
                owner.SetResetSchedulePresentationAllowed(true);
                Assert.Equal(0, owner.ResetScheduleHeightForLayout);
                Assert.Equal(0, owner.ResetScheduleTargetHeightForLayout);
                Assert.False(owner.ResetScheduleAnimating);
                Assert.Equal(Visibility.Collapsed, Named<Border>(owner, "ResetScheduleRow").Visibility);
                Assert.Empty(Named<TextBlock>(owner, "ResetScheduleCountdown").Text);
                Assert.Empty(Named<TextBlock>(owner, "ResetScheduleLocalTime").Text);
            }
            finally { owner.Close(); }
        }
    });

    [Fact]
    public void Repeated_same_footer_space_decision_never_retargets_or_emits_another_layout() => Sta(() =>
    {
        var owner = new QuotaCapsuleWindow("extension-footer-gate-dedup", true);
        try
        {
            owner.ApplyResetSchedule(Fresh(), Now, TimeZoneInfo.Utc);
            foreach (var allowed in new[] { true, false, true, false })
            {
                owner.SetResetSchedulePresentationAllowed(allowed);
                var generation = owner.ExtensionMotionGeneration;
                var layout = owner.ExtensionMotionLayoutCount;
                var events = 0;
                EventHandler changed = (_, _) => events++;
                owner.PresentationChanged += changed;
                for (var index = 0; index < 120; index++) owner.SetResetSchedulePresentationAllowed(allowed);
                owner.PresentationChanged -= changed;
                Assert.Equal(generation, owner.ExtensionMotionGeneration);
                Assert.Equal(layout, owner.ExtensionMotionLayoutCount);
                Assert.Equal(0, events);
                Assert.Equal(allowed ? 26 : 0, owner.ResetScheduleHeightForLayout);
                Assert.Equal(allowed ? 26 : 0, owner.ResetScheduleTargetHeightForLayout);
            }
        }
        finally { owner.Close(); }
    });

    [Fact]
    public void Default_standalone_unshown_control_keeps_height_fallback_and_current_event_geometry() => Sta(() =>
    {
        var owner = new QuotaCapsuleWindow("extension-standalone-height", true);
        try
        {
            Assert.False(owner.UsesCoordinatedPlacement);
            AssertUnshownHeightFallback(owner);
        }
        finally { owner.HideDotTopExtension(); owner.Close(); }
    });

    [Fact]
    public void Production_factory_opts_into_atomic_placement_but_unshown_controls_still_keep_fallback() => Sta(() =>
    {
        // This invokes the actual production factory without Start/Reconcile/Show.
        // The visible atomic Top+size commit is intentionally left to the native
        // fixture; no reflection is used to fake IsVisible or an animation frame.
        using var coordinator = new OverlayCoordinator(false, unifiedPanel: true, observePointer: false);
        Field<DispatcherTimer>(coordinator, "timer").Stop();
        var factory = typeof(OverlayCoordinator).GetMethod("CreateWindow", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var owner = Assert.IsType<QuotaCapsuleWindow>(factory.Invoke(coordinator, ["extension-factory-height"]));
        try
        {
            Assert.True(owner.UsesCoordinatedPlacement);
            AssertUnshownHeightFallback(owner);
        }
        finally { owner.HideDotTopExtension(); owner.Close(); }
    });

    private static void AssertUnshownHeightFallback(QuotaCapsuleWindow owner)
    {
        owner.ApplySessions(Sessions());
        var core = owner.CoreBodyDesiredHeight;
        var snapshots = new List<(double Height, double Desired, double Top, double Footer)>();
        owner.PresentationChanged += (_, _) => snapshots.Add((owner.Height, owner.DesiredHeight,
            owner.DotTopExtensionHeight, owner.ResetScheduleHeightForLayout));
        var notice = Notice("Synthetic atomic-placement summary");
        owner.ShowDotTopExtension(notice, notice.PreferredHeightDip);
        owner.ApplyResetSchedule(Fresh(), Now, TimeZoneInfo.Utc);
        owner.HideDotTopExtension();
        owner.ApplyResetSchedule(new(null, Now, false, null), Now, TimeZoneInfo.Utc);
        Assert.NotEmpty(snapshots);
        Assert.All(snapshots, snapshot =>
        {
            Assert.Equal(core + snapshot.Top + snapshot.Footer, snapshot.Desired);
            Assert.Equal(snapshot.Desired, snapshot.Height);
        });
        Assert.Contains(snapshots, snapshot => snapshot.Top == 37 && snapshot.Footer == 26);
        Assert.Equal(core, owner.Height);
        Assert.Equal(core, owner.DesiredHeight);
        Assert.False(owner.IsVisible);
        Assert.Equal(IntPtr.Zero, new WindowInteropHelper(owner).Handle);
    }

    private static void AssertMonotone(CubicEase easing)
    {
        Assert.Equal(0, easing.Ease(0));
        Assert.Equal(1, easing.Ease(1));
        var previous = 0d;
        for (var index = 1; index <= 40; index++)
        {
            var sample = easing.Ease(index / 40d);
            Assert.InRange(sample, previous, 1);
            previous = sample;
        }
    }

    private static DotTopNoticeView Notice(string text)
    {
        var notice = new DotTopNoticeView();
        notice.Display(new("synthetic-message", text, null, true), 2);
        return notice;
    }

    private static ResetScheduleSnapshot Fresh() => new(new("synthetic-reset", ResetKind.Regular,
        Now.AddMinutes(-10), Now.AddHours(1), new Uri("https://codex-resets.com/zh-CN")), Now, false, null);

    private static CodexSessionsSnapshot Sessions() => new(Enumerable.Range(0, 3)
        .Select(i => new CodexSessionSnapshot("synthetic-task-" + i, "Synthetic task", CodexSessionState.Running, Now)).ToArray(), Now);

    private static T Named<T>(Window owner, string name) where T : FrameworkElement => Assert.IsType<T>(owner.FindName(name));
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
