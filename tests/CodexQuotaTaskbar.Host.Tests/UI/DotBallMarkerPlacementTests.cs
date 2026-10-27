using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Host.Bongo;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.Platform;
using CodexQuotaTaskbar.Host.UI;
using Size = System.Windows.Size;

namespace CodexQuotaTaskbar.Host.Tests.UI;

// No Show, native HWND, OS input, pointer observation or real message. Loaded is
// raised only on this private hidden control to construct its local vector visual.
[Collection("Native UI isolation")]
public sealed class DotBallMarkerPlacementTests
{
    [Theory]
    [InlineData(true, 0, 27d)] [InlineData(true, 1, 27d)] [InlineData(true, 3, 27d)]
    [InlineData(false, 0, 64d)] [InlineData(false, 3, 64d)]
    public void Ready_white_ball_marker_is_clear_of_the_complete_control_and_inside_the_same_window(
        bool unified, int taskCount, double expectedTop) => Sta(() =>
    {
        var owner = CreateOwner(unified, taskCount);
        try
        {
            var marker = Field<Button>(owner, "dotMarker");
            var ball = Field<CompanionBallControl>(owner, "companion");
            Assert.False(ball.Ready);
            var legacyTop = marker.Margin.Top;
            var legacyAlignment = marker.HorizontalAlignment;
            ball.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.True(ball.Ready);
            Layout(owner);
            if (unified) { Assert.Equal(System.Windows.HorizontalAlignment.Right, legacyAlignment); Assert.Equal(System.Windows.HorizontalAlignment.Left, marker.HorizontalAlignment); }
            else Assert.NotEqual(legacyTop, marker.Margin.Top);
            Assert.Equal(expectedTop, marker.Margin.Top);
            var shell = (FrameworkElement)owner.Content;
            var markerBounds = Bounds(marker, shell);
            var ballBounds = Bounds(ball, shell);
            Assert.Equal(56, ballBounds.Height, 5);
            Assert.Equal(24, markerBounds.Height, 5);
            Assert.Equal(unified ? 0 : owner.LayoutWidth - 24, markerBounds.Left, 5);
            AssertNonOverlapping(markerBounds, ballBounds);
            if (!unified) Assert.True(markerBounds.Top >= ballBounds.Bottom);
            Assert.True(markerBounds.Bottom <= owner.DesiredHeight + .01);
            foreach (var name in new[] { "FirstLabel", "FirstValue", "FirstBar", "SecondLabel", "SecondValue", "SecondBar", "SessionToggle" })
            {
                var control = Named<FrameworkElement>(owner, name);
                if (control.Visibility == Visibility.Visible)
                {
                    var bounds = Bounds(control, shell);
                    AssertNonOverlapping(markerBounds, bounds);
                    if (unified && name == "FirstLabel") Assert.True(bounds.Left - markerBounds.Right >= 6 - .01);
                }
            }
            if (unified && taskCount > 0)
            {
                var tasks = Bounds(Named<Grid>(owner, "PanelSessionsRegion"), shell);
                AssertNonOverlapping(markerBounds, tasks);
                Assert.True(tasks.Left - markerBounds.Right >= 4 - .01);
            }
            if (!unified)
            {
                var header = Named<Grid>(owner, "QuotaHeader");
                foreach (var logo in header.Children.OfType<Border>()) AssertNonOverlapping(markerBounds, Bounds(logo, shell));
            }
            Assert.Equal(nint.Zero, new WindowInteropHelper(owner).Handle);
            Assert.False(owner.IsVisible);
        }
        finally { owner.Close(); }
    });

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Notice_reply_and_draft_changes_keep_the_same_ball_clearance_and_marker_action(bool unified) => Sta(() =>
    {
        var owner = CreateOwner(unified, 3);
        try
        {
            var ball = Field<CompanionBallControl>(owner, "companion");
            ball.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var marker = Field<Button>(owner, "dotMarker");
            var baseOffset = marker.Margin.Top;
            var invoked = 0; owner.DotMarkerInvoked += (_, _) => invoked++;
            var notice = new DotTopNoticeView();
            notice.Display(new("synthetic", "Synthetic summary", null, true), 4);
            owner.ShowDotTopExtension(notice, notice.PreferredHeightDip);
            Assert.Equal(baseOffset + 37, marker.Margin.Top, 5);
            Assert.Equal(37, ball.Margin.Top, 5);
            owner.HideDotTopExtension();
            Assert.Equal(baseOffset, marker.Margin.Top, 5);
            var reply = new DotInlineView();
            reply.InputControl.Text = "Synthetic unsent draft\nsecond line";
            reply.InputControl.Select(2, 5);
            owner.ShowInlineDot(reply);
            Assert.Equal(baseOffset + owner.DotTopExtensionHeight, marker.Margin.Top, 5);
            Assert.Equal(owner.DotTopExtensionHeight, ball.Margin.Top, 5);
            marker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, invoked);
            Assert.Equal("Synthetic unsent draft\nsecond line", reply.DraftText);
            Assert.Equal(2, reply.InputControl.SelectionStart);
            Assert.Equal(5, reply.InputControl.SelectionLength);
            owner.HideDotTopExtension();
            Assert.Equal(baseOffset, marker.Margin.Top, 5);
            owner.SetDotMarker(0, hasDraftOrPending: true);
            Assert.False(owner.DotMarkerVisible); // a retained draft is not a notification or reply entry
            Assert.Equal(baseOffset, marker.Margin.Top, 5);
            owner.SetDotMarker(0);
            Assert.False(owner.DotMarkerVisible);
            Assert.Equal(nint.Zero, new WindowInteropHelper(owner).Handle);
        }
        finally { owner.Close(); }
    });

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Actual_Live2D_coexists_with_a_clear_status_ball_and_no_ball_view_keeps_the_original_marker_formula(bool unified) => Sta(() =>
    {
        var owner = CreateOwner(unified, 3);
        try
        {
            var glass = Named<Border>(owner, "GlassBorder");
            var marker = Field<Button>(owner, "dotMarker");
            owner.SetCompanion(false, false);
            Assert.Equal(glass.Margin.Top + 11, marker.Margin.Top, 5);
            Assert.Equal(System.Windows.HorizontalAlignment.Right, marker.HorizontalAlignment);
            owner.SetCompanion(true, false);
            Field<CompanionBallControl>(owner, "companion").RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var ballOffset = marker.Margin.Top;
            owner.ConfigureBongoSurface(true, 192);
            owner.PresentBongoFrame(new BongoFrame(1, 1, [255, 255, 255, 255], 4));
            Layout(owner);
            Assert.True(owner.BongoHasFrame);
            Assert.True(owner.StatusBallVisibleForLayout);
            Assert.True(owner.DotMarkerIsCatHead);
            Assert.Equal(System.Windows.HorizontalAlignment.Left, marker.HorizontalAlignment);
            var model = owner.BongoModelBoundsDip;
            // The approved cat-head anchor uses the real standard-model canvas
            // (61% x, 16% y), not the obsolete centre above the whole model.
            var scale = Math.Min(model.Width / 480, model.Height / 278);
            var expected = Math.Max(0, model.Top + (model.Height - 278 * scale) / 2 + .16 * 278 * scale - 27);
            Assert.Equal(expected, marker.Margin.Top, 5);
            Assert.Equal(model.Left + (model.Width - 480 * scale) / 2 + .61 * 480 * scale - 12, marker.Margin.Left, 5);
            Assert.Equal("!", Assert.IsType<TextBlock>(marker.Content).Text);
            AssertNonOverlapping(Bounds(marker, (Visual)owner.Content), owner.StatusBallBoundsDip);
            Assert.True(marker.Margin.Top + 24 <= owner.DesiredHeight + .01);
            var notice = new DotTopNoticeView(); owner.ShowDotTopExtension(notice, 36);
            Assert.Equal(expected + 37, marker.Margin.Top, 5);
            owner.HideDotTopExtension(); owner.ClearBongoFrame();
            Assert.Equal(ballOffset, marker.Margin.Top, 5);
            Assert.Equal(unified ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right, marker.HorizontalAlignment);
            Assert.Equal(nint.Zero, new WindowInteropHelper(owner).Handle);
        }
        finally { owner.Close(); }
    });

    [Fact]
    public void Unified_task_add_remove_and_collapse_never_move_the_left_edge_marker() => Sta(() =>
    {
        var owner = CreateOwner(true, 3);
        try
        {
            Field<CompanionBallControl>(owner, "companion").RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var marker = Field<Button>(owner, "dotMarker");
            foreach (var count in new[] { 3, 0, 1, 3, 0 })
            {
                owner.ApplySessions(new(Enumerable.Range(0, count).Select(i => new CodexSessionSnapshot(
                    "synthetic-task-" + i, "Synthetic task", CodexSessionState.Running, DateTimeOffset.UnixEpoch)).ToArray(), DateTimeOffset.UnixEpoch));
                owner.TogglePanelSessions(); Layout(owner);
                var bounds = Bounds(marker, (Visual)owner.Content);
                Assert.Equal(0, bounds.Left, 5); Assert.Equal(27, bounds.Top, 5);
                Assert.True(bounds.Bottom < owner.DesiredHeight);
                Assert.Equal(System.Windows.HorizontalAlignment.Left, marker.HorizontalAlignment);
            }
            Assert.Equal(nint.Zero, new WindowInteropHelper(owner).Handle);
        }
        finally { owner.Close(); }
    });

    [Theory]
    [InlineData(1d)] [InlineData(1.25d)] [InlineData(1.5d)] [InlineData(2d)]
    public void Logical_clearance_scales_without_changing_the_cat_or_window_size(double scale)
    {
        foreach (var top in new[] { 0d, 8.5, 37, 129, 233 })
        {
            var empty = QuotaCapsuleWindow.DotMarkerTopOffset(top, 16, 0, 56, 80, true);
            var full = QuotaCapsuleWindow.DotMarkerTopOffset(top, 16, 0, 56, 200, true);
            Assert.Equal((top + 27) * scale, empty * scale, 5);
            Assert.Equal(empty, full);
            Assert.True((empty + 24) * scale < (top + 80) * scale);
            Assert.Equal(top + 64, QuotaCapsuleWindow.DotMarkerTopOffset(top, 28, 0, 56, 112, false));
            Assert.Equal(16 + top + 119.05882352941177 + 11,
                QuotaCapsuleWindow.DotMarkerTopOffset(top, 16, 119.05882352941177, null, 319.05882352941177, true));
        }
    }

    [Fact]
    public void Synthetic_vector_typing_and_expression_ink_does_not_paint_either_marker_hitbox() => Sta(() =>
    {
        double now = 0;
        using var visual = new CompanionBallVisual(() => now) { Width = 56, Height = 56 };
        typeof(CompanionBallVisual).GetField("random", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(visual, new Random(42));
        var frame = typeof(CompanionBallVisual).GetMethod("OnFrame", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<object?, EventArgs>>(visual);
        visual.SetTypingInteraction(true);
        visual.Measure(new Size(56, 56)); visual.Arrange(new Rect(0, 0, 56, 56));
        foreach (var id in CompanionEmotionDirector.EmotionIds)
        {
            now += 2000; visual.SetState(id, true);
            foreach (var key in new ushort[] { 0, 30, 57 })
            {
                if (key != 0) visual.Input(new(CompanionInputKind.Key, key, true));
                visual.Input(new(CompanionInputKind.MouseButton, MouseButton: CompanionMouseButton.Left, IsDown: true));
                foreach (var step in new[] { 0, 120, 400 })
                {
                    now += step; frame(null, EventArgs.Empty);
                    var source = new RenderTargetBitmap(80, 96, 96, 96, PixelFormats.Pbgra32);
                    source.Render(visual);
                    var drawing = VisualTreeHelper.GetDrawing(visual);
                    Assert.NotNull(drawing);
                    foreach (var (width, markerX, markerY) in new[] { (280, 0, 27), (306, 282, 64) })
                    {
                        var scene = new DrawingVisual();
                        using (var dc = scene.RenderOpen())
                        {
                            dc.PushTransform(new TranslateTransform(width - 56, 0));
                            dc.DrawDrawing(drawing); dc.Pop();
                        }
                        var bitmap = new RenderTargetBitmap(width, 112, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(scene);
                        var pixels = new byte[width * 112 * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
                        for (var y = markerY; y < markerY + 24; y++)
                            for (var x = markerX; x < markerX + 24; x++)
                                Assert.True(pixels[(y * width + x) * 4 + 3] <= 8,
                                    $"Expression {id} paints the marker hitbox in {width} DIP layout at {x},{y}.");
                    }
                }
                if (key != 0) visual.Input(new(CompanionInputKind.Key, key, false));
                visual.Input(new(CompanionInputKind.Reset));
            }
        }
        Assert.False(visual.AnimationRunning);
        Assert.False(visual.IsLoaded);
    });

    private static QuotaCapsuleWindow CreateOwner(bool unified, int count)
    {
        var owner = new QuotaCapsuleWindow("synthetic-marker", unified);
        owner.SetPointerObservationAllowed(false);
        owner.SetCompanion(true, false);
        owner.ApplySessions(new(Enumerable.Range(0, count).Select(i => new CodexSessionSnapshot(
            "synthetic-task-" + i, "Synthetic task", CodexSessionState.Running, DateTimeOffset.UnixEpoch)).ToArray(), DateTimeOffset.UnixEpoch));
        owner.SetDotMarker(4);
        return owner;
    }

    private static void Layout(QuotaCapsuleWindow owner)
    {
        var root = (FrameworkElement)owner.Content;
        root.Measure(new Size(owner.LayoutWidth, owner.DesiredHeight));
        root.Arrange(new Rect(0, 0, owner.LayoutWidth, owner.DesiredHeight)); root.UpdateLayout();
    }
    private static Rect Bounds(FrameworkElement view, Visual root) => view.TransformToAncestor(root).TransformBounds(new Rect(view.RenderSize));
    private static void AssertNonOverlapping(Rect first, Rect second)
    {
        var overlap = Rect.Intersect(first, second);
        Assert.True(overlap.IsEmpty || overlap.Width <= .001 || overlap.Height <= .001);
    }
    private static T Named<T>(Window owner, string name) where T : FrameworkElement => (T)owner.FindName(name);
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
