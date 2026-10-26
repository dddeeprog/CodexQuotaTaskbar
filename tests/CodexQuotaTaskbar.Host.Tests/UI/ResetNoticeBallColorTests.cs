using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

// No windows are shown or assigned native handles. Drawings are recorded into
// an in-memory DrawingGroup; actual owner/footer transitions are proved in the
// separate alpha-zero native proof, not by a simulated clock here.
[Collection("Native UI isolation")]
public sealed class ResetNoticeBallColorTests
{
    [Theory]
    [InlineData(true, true, true, .0011, true, true)]
    [InlineData(false, true, true, 20, true, false)]
    [InlineData(true, false, true, 20, true, false)]
    [InlineData(true, true, false, 20, true, false)]
    [InlineData(true, true, true, 20, false, false)]
    [InlineData(true, true, true, 0, true, false)]
    [InlineData(true, true, true, .001, true, false)]
    [InlineData(true, true, true, double.NaN, true, false)]
    [InlineData(true, true, true, double.PositiveInfinity, true, false)]
    public void Green_requires_the_valid_allowed_visible_row_real_height_and_visible_owner(
        bool scheduleVisible, bool presentationAllowed, bool rowVisible, double height, bool ownerVisible, bool expected) =>
        Assert.Equal(expected, QuotaCapsuleWindow.ShouldUseResetNoticeColor(
            scheduleVisible, presentationAllowed, rowVisible, height, ownerVisible));

    [Fact]
    public void Only_the_head_fill_changes_and_white_is_restored_without_pose_eye_or_gaze_changes() => Sta(() =>
    {
        using var visual = new CompanionBallVisual(() => 0);
        visual.SetState("01", animate: true);
        visual.Gaze(.4, -.2); // Synthetic direction; no pointer is sampled.
        var pose = Field<CompanionPoseEngine>(visual, "engine").Pose.ToArray();
        var state = (visual.Emotion, visual.EffectiveEmotion, visual.GazeSample, visual.TypingFrame);
        var whiteBrush = StaticBrush("Body"); var greenBrush = StaticBrush("GreenBody");
        var white = RecordDrawing(visual);

        visual.SetResetNoticeActive(true);
        var green = RecordDrawing(visual);

        Assert.True(visual.ResetNoticeActive);
        Assert.Equal(state, (visual.Emotion, visual.EffectiveEmotion, visual.GazeSample, visual.TypingFrame));
        Assert.Equal(pose, Field<CompanionPoseEngine>(visual, "engine").Pose);
        AssertOnlyHeadBrushChanged(white, green, whiteBrush, greenBrush);
        Assert.True(whiteBrush.IsFrozen); Assert.True(greenBrush.IsFrozen);
        var palette = Assert.IsType<RadialGradientBrush>(greenBrush);
        Assert.All(palette.GradientStops, stop => Assert.True(stop.Color.G > stop.Color.R && stop.Color.G > stop.Color.B));

        visual.SetResetNoticeActive(false);
        var restored = RecordDrawing(visual);
        Assert.False(visual.ResetNoticeActive);
        Assert.Same(whiteBrush, GeometryDrawings(restored).First().Brush);
        Assert.Equal(GeometrySignature(white), GeometrySignature(restored));
        Assert.Equal(state, (visual.Emotion, visual.EffectiveEmotion, visual.GazeSample, visual.TypingFrame));
        AssertNoNativeClock(visual);
    });

    [Fact]
    public void Reduced_sleeping_pose_changes_color_immediately_without_changing_shape_or_starting_frames() => Sta(() =>
    {
        using var visual = new CompanionBallVisual(() => 0);
        visual.SetState("06", animate: false);
        var state = (visual.Emotion, visual.EffectiveEmotion, visual.GazeSample, visual.TypingFrame);
        var pose = Field<CompanionPoseEngine>(visual, "engine").Pose.ToArray();
        var white = RecordDrawing(visual);
        visual.SetResetNoticeActive(true);
        Assert.True(visual.ResetNoticeActive);
        var green = RecordDrawing(visual);

        AssertOnlyHeadBrushChanged(white, green, StaticBrush("Body"), StaticBrush("GreenBody"));
        Assert.Equal(state, (visual.Emotion, visual.EffectiveEmotion, visual.GazeSample, visual.TypingFrame));
        Assert.Equal(pose, Field<CompanionPoseEngine>(visual, "engine").Pose);
        Assert.Equal(0, visual.FrameCount);
        visual.SetResetNoticeActive(false);
        Assert.Same(StaticBrush("Body"), GeometryDrawings(RecordDrawing(visual)).First().Brush);
        AssertNoNativeClock(visual);
    });

    [Fact]
    public void Control_hands_pending_color_to_its_lazy_visual_preserves_badge_and_recreates_white() => Sta(() =>
    {
        using var control = new CompanionBallControl();
        control.SetPointerObservationAllowed(false);
        control.SetReducedMotion(true);
        Assert.False(control.ResetNoticeActive);
        Assert.Null(control.Visual);
        control.SetResetNoticeActive(true);
        Assert.True(control.ResetNoticeActive);
        // This routed event exercises only lazy state handoff. It does not
        // attach the control to a window or claim actual loaded visibility.
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        var visual = Assert.IsType<CompanionBallVisual>(control.Visual);
        Assert.True(visual.ResetNoticeActive);
        var now = DateTimeOffset.UtcNow;
        control.Apply(CodexSessionsSnapshot.Create([new("synthetic-session", "Synthetic task", CodexSessionState.Running, now)], now));
        var number = Field<TextBlock>(control, "number");
        var badge = Field<Border>(control, "countBadge");
        var status = (number.Text, badge.Visibility, visual.Emotion, visual.EffectiveEmotion);

        control.SetResetNoticeActive(false);
        Assert.False(control.ResetNoticeActive); Assert.False(visual.ResetNoticeActive);
        control.SetResetNoticeActive(true);
        Assert.True(visual.ResetNoticeActive);
        Assert.Equal(status, (number.Text, badge.Visibility, visual.Emotion, visual.EffectiveEmotion));
        AssertNoNativeClock(visual);
        Assert.Null(PresentationSource.FromVisual(control));

        control.Dispose();
        Assert.False(control.ResetNoticeActive); Assert.False(visual.ResetNoticeActive);
        control.SetResetNoticeActive(true); visual.SetResetNoticeActive(true);
        Assert.False(control.ResetNoticeActive); Assert.False(visual.ResetNoticeActive);
        using var recreated = new CompanionBallControl();
        recreated.SetPointerObservationAllowed(false);
        Assert.False(recreated.ResetNoticeActive);
        using var recreatedVisual = new CompanionBallVisual(() => 0);
        Assert.False(recreatedVisual.ResetNoticeActive);
        recreatedVisual.SetState("01", animate: false);
        Assert.Same(StaticBrush("Body"), GeometryDrawings(RecordDrawing(recreatedVisual)).First().Brush);
    });

    private static DrawingGroup RecordDrawing(CompanionBallVisual visual)
    {
        visual.Measure(new Size(56, 56)); visual.Arrange(new Rect(0, 0, 56, 56));
        var drawing = new DrawingGroup();
        using var context = drawing.Open();
        try { typeof(CompanionBallVisual).GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(visual, [context]); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
        return drawing;
    }

    private static void AssertOnlyHeadBrushChanged(DrawingGroup before, DrawingGroup after, Brush white, Brush green)
    {
        var oldGeometry = GeometryDrawings(before).ToArray(); var newGeometry = GeometryDrawings(after).ToArray();
        Assert.True(oldGeometry.Length > 1);
        Assert.Equal(GeometrySignature(before), GeometrySignature(after));
        Assert.Equal(before.Bounds, after.Bounds);
        Assert.Equal(oldGeometry.Length, newGeometry.Length);
        Assert.Same(white, oldGeometry[0].Brush); Assert.Same(green, newGeometry[0].Brush);
        for (var index = 1; index < oldGeometry.Length; index++)
            Assert.Same(oldGeometry[index].Brush, newGeometry[index].Brush);
    }

    private static string[] GeometrySignature(Drawing drawing) => GeometryDrawings(drawing)
        .Select(item => item.Geometry.ToString(CultureInfo.InvariantCulture)).ToArray();
    private static IEnumerable<GeometryDrawing> GeometryDrawings(Drawing drawing)
    {
        if (drawing is GeometryDrawing geometry) yield return geometry;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var item in GeometryDrawings(child)) yield return item;
    }
    private static Brush StaticBrush(string name) => Assert.IsAssignableFrom<Brush>(typeof(CompanionBallVisual)
        .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
    private static T Field<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void AssertNoNativeClock(CompanionBallVisual visual)
    {
        Assert.False(visual.AnimationRunning);
        Assert.False(visual.IsVisible);
        Assert.Null(PresentationSource.FromVisual(visual));
    }
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try { action(); }
            catch (Exception error) { failure = error; }
            finally { dispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "In-memory reset color check did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
