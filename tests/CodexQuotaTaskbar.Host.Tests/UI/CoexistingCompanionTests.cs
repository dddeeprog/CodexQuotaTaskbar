using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Host.Bongo;
using CodexQuotaTaskbar.Host.Platform;
using CodexQuotaTaskbar.Host.UI;
using Size = System.Windows.Size;

namespace CodexQuotaTaskbar.Host.Tests.UI;

// Synthetic frames and routed Loaded on private, never-shown controls only.
// No global keyboard/pointer source, browser, account or real message is used.
[Collection("Native UI isolation")]
public sealed class CoexistingCompanionTests
{
    [Theory]
    [InlineData(true, 160)] [InlineData(true, 192)] [InlineData(true, 240)]
    [InlineData(false, 160)] [InlineData(false, 192)] [InlineData(false, 240)]
    public void Cat_and_status_ball_share_the_existing_card_without_adding_height_or_covering_protected_controls(bool unified, int size) => Sta(() =>
    {
        var owner = CreateOwner(unified);
        try
        {
            var ball = Ball(owner);
            var whiteVisual = ball.Visual;
            var baseHeight = owner.DesiredHeight;
            owner.ConfigureBongoSurface(true, size);
            owner.PresentBongoFrame(Frame());
            var surface = Assert.IsType<BongoIslandSurface>(Named<Border>(owner, "GlassBorder").Child);
            Assert.True(owner.BongoHasFrame);
            Assert.True(owner.StatusBallVisibleForLayout);
            Assert.Same(ball, Ball(owner));
            Assert.Same(whiteVisual, ball.Visual);
            Assert.Equal(Visibility.Visible, ball.Visual!.Visibility);
            Assert.Same(owner, Window.GetWindow(ball));
            Assert.Same(owner, Window.GetWindow(surface));
            Assert.Equal(baseHeight + surface.StageHeightDip, owner.DesiredHeight, 6);
            Assert.Equal(unified ? 280 : 306, owner.LayoutWidth);

            foreach (var dpi in new[] { 96, 120, 144, 192 })
            {
                var scale = dpi / 96d;
                var root = (FrameworkElement)owner.Content;
                VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
                var physical = new ScreenRect(-800, 100,
                    -800 + (int)Math.Ceiling(owner.LayoutWidth * scale),
                    100 + (int)Math.Ceiling(owner.DesiredHeight * scale));
                SetField(owner, "physicalBounds", physical);
                Arrange(owner);
                var bounds = owner.StatusBallBoundsDip;
                Assert.Equal(52, bounds.Width, 3);
                Assert.Equal(52, bounds.Height, 3);
                AssertInside(bounds, owner.LayoutWidth, owner.DesiredHeight);
                var ballPhysical = new Rect(physical.Left + bounds.Left * scale, physical.Top + bounds.Top * scale,
                    bounds.Width * scale, bounds.Height * scale);
                var model = owner.BongoModelBounds;
                AssertNonOverlapping(ballPhysical, new Rect(model.Left, model.Top, model.Width, model.Height));
                AssertProtectedControlsClear(owner, bounds, unified);
            }
            AssertNoNativeWindow(owner);
        }
        finally { owner.Close(); }
    });

    [Theory]
    [InlineData(160)] [InlineData(192)] [InlineData(240)]
    public void Preview_dock_switch_reuses_the_same_ball_model_and_card_and_keeps_quota_and_tasks_clear(int size) => Sta(() =>
    {
        var owner = CreateOwner(true);
        try
        {
            owner.ConfigureBongoSurface(true, size);
            owner.PresentBongoFrame(Frame());
            var ball = Ball(owner);
            var surface = Named<Border>(owner, "GlassBorder").Child;
            var height = owner.DesiredHeight;
            var width = owner.LayoutWidth;
            Rect? quotaCorner = null;
            foreach (var dock in new[] { CompanionStatusDock.QuotaCorner, CompanionStatusDock.StageCorner, CompanionStatusDock.QuotaCorner })
            {
                owner.SetCompanionStatusDock(dock);
                Arrange(owner);
                var bounds = owner.StatusBallBoundsDip;
                AssertInside(bounds, width, height);
                AssertProtectedControlsClear(owner, bounds, true);
                Assert.Equal(height, owner.DesiredHeight);
                Assert.Equal(width, owner.LayoutWidth);
                Assert.Same(ball, Ball(owner));
                Assert.Same(surface, Named<Border>(owner, "GlassBorder").Child);
                if (dock == CompanionStatusDock.QuotaCorner)
                {
                    if (quotaCorner is { } previous) Assert.Equal(previous, bounds);
                    quotaCorner = bounds;
                }
                else Assert.True(bounds.Top < quotaCorner!.Value.Top);
            }
            // Stage-corner cat-ink overlap is checked using the real model in the
            // native preview. This synthetic frame test claims only layout bounds.
            AssertNoNativeWindow(owner);
        }
        finally { owner.Close(); }
    });

    [Fact]
    public void Disabling_the_status_display_does_not_stop_the_cat_and_disabling_the_cat_does_not_remove_the_ball() => Sta(() =>
    {
        var owner = CreateOwner(true);
        try
        {
            owner.ConfigureBongoSurface(true, 192);
            owner.PresentBongoFrame(Frame());
            var height = owner.DesiredHeight;
            var surface = Named<Border>(owner, "GlassBorder").Child;
            var bitmap = Field<object>(owner, "bongoBitmap");
            owner.SetCompanion(false, false);
            Assert.True(owner.BongoHasFrame);
            Assert.False(owner.StatusBallVisibleForLayout);
            Assert.True(owner.StatusBallBoundsDip.IsEmpty);
            Assert.Equal(height, owner.DesiredHeight);
            Assert.Same(surface, Named<Border>(owner, "GlassBorder").Child);
            Assert.Same(bitmap, Field<object>(owner, "bongoBitmap"));

            owner.SetCompanion(true, false);
            var ball = Ball(owner);
            ball.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.True(owner.StatusBallVisibleForLayout);
            owner.ConfigureBongoSurface(false, 192);
            Arrange(owner);
            Assert.False(owner.BongoHasFrame);
            Assert.True(owner.StatusBallVisibleForLayout);
            Assert.Same(ball, Ball(owner));
            Assert.Equal(0, owner.StatusBallTopOffsetForLayout);
            Assert.Equal(56, owner.StatusBallBoundsDip.Width, 3);
            Assert.Equal(56, owner.StatusBallBoundsDip.Height, 3);
            Assert.Equal(Visibility.Visible, ball.Visual!.Visibility);
            AssertNoNativeWindow(owner);
        }
        finally { owner.Close(); }
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Incoming_cat_frames_keep_the_same_expression_engine_and_process_status_badge(bool reduced) => Sta(() =>
    {
        var owner = CreateOwner(true, reduced);
        try
        {
            var ball = Ball(owner);
            var visual = ball.Visual!;
            visual.Gaze(.7, -.4); // Synthetic direction, never queries the OS cursor.
            var gaze = visual.GazeSample;
            owner.ConfigureBongoSurface(true, 192);
            for (var index = 0; index < 8; index++) owner.PresentBongoFrame(Frame());
            Assert.Same(visual, ball.Visual);
            Assert.Equal(Visibility.Visible, visual.Visibility);
            Assert.Equal(gaze.TargetX, visual.GazeSample.TargetX);
            Assert.Equal(gaze.TargetY, visual.GazeSample.TargetY);
            Assert.Contains(visual.Emotion, CompanionEmotionDirector.EmotionIds);
            Assert.Equal(32, CompanionEmotionDirector.EmotionIds.Distinct().Count());
            Assert.Equal("2", Field<TextBlock>(ball, "number").Text);
            Assert.Equal(Visibility.Visible, Field<Border>(ball, "countBadge").Visibility);
            Assert.Contains("进行中 1", AutomationProperties.GetName(ball), StringComparison.Ordinal);
            Assert.Contains("等待处理 1", AutomationProperties.GetName(ball), StringComparison.Ordinal);
            Assert.Contains("已完成 1", AutomationProperties.GetName(ball), StringComparison.Ordinal);
            AssertNoNativeWindow(owner);
        }
        finally { owner.Close(); }
    });

    [Fact]
    public void Coexisting_status_ball_does_not_draw_a_second_typing_keyboard_and_restores_its_preference_when_cat_stops() => Sta(() =>
    {
        var owner = CreateOwner(true);
        try
        {
            owner.SetCompanionTyping(true);
            owner.ConfigureBongoSurface(true, 192);
            owner.PresentBongoFrame(Frame());
            var ball = Ball(owner);
            Assert.False(Field<bool>(ball, "typingEnabled"));
            owner.KeyboardActivity(new(CompanionInputKind.Key, 30, true));
            Assert.Equal(CompanionTypingFrame.Rest, ball.Visual!.TypingFrame);
            owner.ClearBongoFrame();
            Assert.True(Field<bool>(ball, "typingEnabled"));
            Assert.Same(ball, Ball(owner));
            AssertNoNativeWindow(owner);
        }
        finally { owner.Close(); }
    });

    [Fact]
    public void Invalid_preview_dock_is_rejected_without_changing_geometry_or_creating_a_window() => Sta(() =>
    {
        var owner = CreateOwner(true);
        try
        {
            owner.ConfigureBongoSurface(true, 192); owner.PresentBongoFrame(Frame()); Arrange(owner);
            var bounds = owner.StatusBallBoundsDip; var height = owner.DesiredHeight;
            Assert.Throws<ArgumentOutOfRangeException>(() => owner.SetCompanionStatusDock((CompanionStatusDock)99));
            Arrange(owner);
            Assert.Equal(bounds, owner.StatusBallBoundsDip);
            Assert.Equal(height, owner.DesiredHeight);
            AssertNoNativeWindow(owner);
        }
        finally { owner.Close(); }
    });

    [Fact]
    public void Never_shown_coexisting_controls_leave_pointer_and_animation_clocks_stopped() => Sta(() =>
    {
        var owner = CreateOwner(true);
        var ball = Ball(owner);
        try
        {
            owner.ConfigureBongoSurface(true, 192); owner.PresentBongoFrame(Frame()); Arrange(owner);
            Assert.False(Field<bool>(ball, "observePointer"));
            Assert.False(Field<DispatcherTimer>(ball, "timer").IsEnabled);
            Assert.False(ball.Visual!.AnimationRunning);
            AssertNoNativeWindow(owner);
        }
        finally { owner.Close(); }
        Assert.False(Field<DispatcherTimer>(ball, "timer").IsEnabled);
        Assert.False(ball.Visual!.AnimationRunning);
    });

    private static QuotaCapsuleWindow CreateOwner(bool unified, bool reduced = false)
    {
        var owner = new QuotaCapsuleWindow("synthetic-coexist", unified);
        owner.SetPointerObservationAllowed(false);
        owner.SetCompanion(true, reduced);
        owner.ApplySessions(new([
            new("synthetic-running", "Synthetic running task", CodexSessionState.Running, DateTimeOffset.UnixEpoch),
            new("synthetic-waiting", "Synthetic waiting task", CodexSessionState.Waiting, DateTimeOffset.UnixEpoch),
            new("synthetic-done", "Synthetic completed task", CodexSessionState.Review, DateTimeOffset.UnixEpoch),
        ], DateTimeOffset.UnixEpoch));
        Ball(owner).RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        return owner;
    }
    private static void AssertProtectedControlsClear(QuotaCapsuleWindow owner, Rect ball, bool unified)
    {
        var root = (Visual)owner.Content;
        foreach (var name in new[] { "FirstLabel", "FirstValue", "FirstBar", "SecondLabel", "SecondValue", "SecondBar", "SessionToggle" })
        {
            var control = Named<FrameworkElement>(owner, name);
            if (control.Visibility == Visibility.Visible && control.RenderSize.Width > 0 && control.RenderSize.Height > 0)
                AssertNonOverlapping(ball, control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize)));
        }
        if (unified)
        {
            // The region also contains its separator and empty padding. Protect
            // actual task click areas, rather than treating that padding as text.
            var tasks = Named<StackPanel>(owner, "PanelSessionsList");
            foreach (var button in tasks.Children.OfType<Button>())
                if (button.Visibility == Visibility.Visible && button.ActualHeight > 0)
                    AssertNonOverlapping(ball, button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize)));
        }
    }
    private static void AssertNonOverlapping(Rect first, Rect second)
    {
        var overlap = Rect.Intersect(first, second);
        Assert.True(overlap.IsEmpty || overlap.Width <= .01 || overlap.Height <= .01,
            $"Synthetic protected regions overlap: {first} / {second}.");
    }
    private static void AssertInside(Rect bounds, double width, double height)
    {
        Assert.False(bounds.IsEmpty);
        Assert.InRange(bounds.Left, -.01, width + .01); Assert.InRange(bounds.Right, -.01, width + .01);
        Assert.InRange(bounds.Top, -.01, height + .01); Assert.InRange(bounds.Bottom, -.01, height + .01);
    }
    private static void Arrange(QuotaCapsuleWindow owner)
    {
        var content = (FrameworkElement)owner.Content;
        content.Measure(new Size(owner.LayoutWidth, owner.DesiredHeight));
        content.Arrange(new Rect(0, 0, owner.LayoutWidth, owner.DesiredHeight)); content.UpdateLayout();
    }
    private static void AssertNoNativeWindow(Window owner)
    {
        Assert.False(owner.IsVisible); Assert.Equal(nint.Zero, new WindowInteropHelper(owner).Handle);
    }
    private static BongoFrame Frame() => new(1, 1, [255, 255, 255, 255], 4);
    private static CompanionBallControl Ball(QuotaCapsuleWindow owner) => Field<CompanionBallControl>(owner, "companion");
    private static T Named<T>(QuotaCapsuleWindow owner, string name) where T : FrameworkElement => (T)owner.FindName(name);
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void SetField(object owner, string name, object value) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Synthetic coexistence control test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
