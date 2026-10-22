using System.Xml.Linq;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Host.Tests.TestSupport;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class SessionStackWindowTests
{
    [Theory]
    [InlineData(108, false)]
    [InlineData(108, true)]
    [InlineData(144, false)]
    [InlineData(144, true)]
    public void Compact_sessions_keep_layers_shadows_and_long_titles_inside_the_window(int dpi, bool expanded)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new SessionStackWindow("compact-session-test");
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    window.Apply(new([
                        new("one", "介绍项目", CodexSessionState.Running, now),
                        new("two", "查找主设备模型", CodexSessionState.Running, now),
                        new("three", "这是一个很长的会话名称用于验证收窄后仍然正确显示省略号而不会撑宽气泡", CodexSessionState.Waiting, now),
                        new("four", "更多任务", CodexSessionState.Running, now)], now));
                    if (expanded)
                    {
                        ((Button)window.FindName("ExpandButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        // Let the existing transition finish before inspecting the settled layout.
                        var frame = new System.Windows.Threading.DispatcherFrame();
                        var timer = new System.Windows.Threading.DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(SessionStackWindow.ExpansionAnimationMilliseconds + 80)
                        };
                        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                        timer.Start();
                        System.Windows.Threading.Dispatcher.PushFrame(frame);
                    }
                    var content = (FrameworkElement)window.Content;
                    VisualTreeHelper.SetRootDpi(content, new DpiScale(dpi / 96d, dpi / 96d));
                    content.Measure(new Size(window.Width, window.DesiredHeight));
                    content.Arrange(new Rect(0, 0, window.Width, window.DesiredHeight));
                    content.UpdateLayout();

                    Assert.Equal(SessionStackWindow.StackWidth, window.Width);
                    var panel = (StackPanel)window.FindName("SessionsPanel");
                    Assert.Equal(expanded ? 4 : 1, panel.Children.Count);
                    foreach (Button pill in panel.Children)
                    {
                        Assert.Equal(SessionStackWindow.CardWidth, pill.ActualWidth);
                        Assert.Equal(58, pill.ActualHeight);
                        var bounds = pill.TransformToAncestor(content).TransformBounds(new Rect(pill.RenderSize));
                        Assert.Equal(SessionStackWindow.ShadowHorizontalInset, bounds.Left, 2);
                        var labels = ((Grid)pill.Content).Children.OfType<TextBlock>().ToArray();
                        Assert.All(labels, label => Assert.Equal(TextTrimming.CharacterEllipsis, label.TextTrimming));
                        Assert.All(labels, label => Assert.InRange(label.ActualWidth, 1, SessionStackWindow.CardWidth - 40));
                    }
                    Assert.Equal(SessionStackWindow.CollapsedMiddleWidth, ((Border)window.FindName("CollapsedMiddleLayer")).Width);
                    Assert.Equal(SessionStackWindow.CollapsedBackWidth, ((Border)window.FindName("CollapsedBackLayer")).Width);
                    Assert.All(((StackPanel)window.FindName("ShadowSlots")).Children.OfType<Border>(),
                        shadow => Assert.Equal(SessionStackWindow.CardWidth, shadow.Width));
                    if (expanded)
                        Assert.True(((ScrollViewer)window.FindName("SessionsScroller")).ScrollableHeight > 0);

                    if (Environment.GetEnvironmentVariable("CODEX_QUOTA_UI_PROOFS") == "1")
                    {
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.Width * dpi / 96d),
                            (int)Math.Ceiling(window.DesiredHeight * dpi / 96d), dpi, dpi, PixelFormats.Pbgra32);
                        bitmap.Render(content);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        var directory = Path.Combine(RepositoryPaths.Root, "artifacts", "material-transparency-proof");
                        Directory.CreateDirectory(directory);
                        using var stream = File.Create(Path.Combine(directory, $"compact-sessions-{dpi}-{expanded}.png"));
                        encoder.Save(stream);
                    }
                }
                finally { window.Close(); }
            }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Compact session layout verification timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(5, false, 1)]
    [InlineData(2, true, 2)]
    [InlineData(3, true, 3)]
    [InlineData(8, true, 3)]
    public void Expanded_stack_never_grows_beyond_three_visible_sessions(int total, bool expanded, int expected)
    {
        Assert.Equal(expected, SessionStackWindow.VisibleSessionCount(total, expanded));
    }

    [Fact]
    public void Expanded_stack_height_is_capped_at_three_sessions()
    {
        Assert.Equal(SessionStackWindow.CalculateHeight(3, true), SessionStackWindow.CalculateHeight(20, true));
        Assert.Equal(256, SessionStackWindow.CalculateHeight(20, true));
    }

    [Fact]
    public void Collapsed_stack_keeps_extra_depth_to_reveal_layered_sessions()
    {
        Assert.Equal(130, SessionStackWindow.CalculateHeight(4, false));
        Assert.Equal(98, SessionStackWindow.CalculateHeight(1, false));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(8, 2)]
    public void Collapsed_stack_depth_matches_the_number_of_hidden_sessions(int total, int expected)
    {
        Assert.Equal(expected, SessionStackWindow.CollapsedLayerCount(total));
    }

    [Fact]
    public void Shadow_safety_area_contains_the_full_blur_and_depth()
    {
        Assert.Equal(320, SessionStackWindow.StackWidth);
        Assert.Equal(272, SessionStackWindow.CardWidth);
        Assert.Equal(SessionStackWindow.CardWidth + 2 * SessionStackWindow.ShadowHorizontalInset, SessionStackWindow.StackWidth);
        Assert.True(SessionStackWindow.ShadowHorizontalInset >= 18);
        Assert.True(SessionStackWindow.ShadowTopInset >= 18 - 5);
        Assert.True(SessionStackWindow.ShadowBottomInset >= 18 + 5);
    }

    [Theory]
    [InlineData(0, -120, 180, 30)]
    [InlineData(160, -120, 180, 180)]
    [InlineData(40, 120, 180, 10)]
    public void Mouse_wheel_scroll_is_clamped_to_the_session_list(double current, int delta, double maximum, double expected)
    {
        Assert.Equal(expected, SessionStackWindow.CalculateScrollOffset(current, delta, maximum));
    }

    [Theory]
    [InlineData(true, 0, 60, true)]
    [InlineData(false, 0, 60, false)]
    [InlineData(true, 60, 60, false)]
    public void Smooth_scroll_respects_windows_animation_preferences(bool animationsEnabled, double current, double target, bool expected)
    {
        Assert.Equal(expected, SessionStackWindow.ShouldAnimateScroll(animationsEnabled, current, target));
        Assert.InRange(SessionStackWindow.ScrollAnimationMilliseconds, 150, 300);
    }

    [Theory]
    [InlineData(true, 1, true)]
    [InlineData(true, 0, false)]
    [InlineData(false, 1, false)]
    public void New_sessions_animate_only_when_windows_animations_are_enabled(
        bool animationsEnabled,
        int addedSessionCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            SessionStackWindow.ShouldAnimateSessionAddition(animationsEnabled, addedSessionCount));
        Assert.InRange(SessionStackWindow.SessionAdditionAnimationMilliseconds, 150, 300);
    }

    [Theory]
    [InlineData(true, 1, 0, 0, true)]
    [InlineData(true, 0, 2, 1, true)]
    [InlineData(true, 0, 1, 1, false)]
    [InlineData(false, 1, 2, 1, false)]
    public void Removed_sessions_animate_only_when_the_visible_stack_changes(
        bool animationsEnabled,
        int visibleRemovedCount,
        int previousLayerCount,
        int currentLayerCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            SessionStackWindow.ShouldAnimateSessionRemoval(
                animationsEnabled,
                visibleRemovedCount,
                previousLayerCount,
                currentLayerCount));
        Assert.InRange(SessionStackWindow.SessionRemovalAnimationMilliseconds, 120, 200);
        Assert.Equal(-6, SessionStackWindow.SessionRemovalOffset);
        Assert.Equal(-4, SessionStackWindow.CollapsedLayerRemovalOffset);
    }

    [Theory]
    [InlineData(2, 0, true, 126)]
    [InlineData(1, 0, true, 63)]
    [InlineData(1, 0, false, 6)]
    [InlineData(0, 0, true, 0)]
    public void Retained_sessions_start_from_their_previous_visual_position(
        int previousIndex,
        int currentIndex,
        bool expanded,
        double expected)
    {
        Assert.Equal(expected, SessionStackWindow.SessionReflowOffset(previousIndex, currentIndex, expanded));
    }

    [Fact]
    public void Session_animation_indices_tolerate_duplicate_ids_and_keep_the_first_visual_position()
    {
        var now = DateTimeOffset.UtcNow;
        CodexQuotaTaskbar.Core.Sessions.CodexSessionSnapshot[] sessions = [
            new("same", "第一段", CodexQuotaTaskbar.Core.Sessions.CodexSessionState.Running, now),
            new("same", "续接段", CodexQuotaTaskbar.Core.Sessions.CodexSessionState.Running, now),
            new("other", "其他任务", CodexQuotaTaskbar.Core.Sessions.CodexSessionState.Waiting, now)];

        var indices = SessionStackWindow.BuildFirstSessionIndices(sessions);

        Assert.Equal(2, indices.Count);
        Assert.Equal(0, indices["same"]);
        Assert.Equal(2, indices["other"]);
    }

    [Theory]
    [InlineData(0, 0, 184, true)]
    [InlineData(2, 0, 184, true)]
    [InlineData(3, 0, 184, false)]
    [InlineData(3, 10, 184, false)]
    [InlineData(0, 60, 184, false)]
    [InlineData(1, 60, 184, true)]
    [InlineData(3, 63, 184, true)]
    public void Only_fully_visible_cards_draw_shadows_inside_the_scrolling_viewport(int index, double offset, double viewport, bool expected)
    {
        Assert.Equal(expected, SessionStackWindow.IsShadowSlotVisible(index, offset, viewport));
    }

    [Fact]
    public void Expansion_and_collapse_use_short_opposing_transitions()
    {
        Assert.Equal(-10, SessionStackWindow.ExpansionTransitionOffset(true));
        Assert.Equal(10, SessionStackWindow.ExpansionTransitionOffset(false));
        Assert.InRange(SessionStackWindow.ExpansionAnimationMilliseconds, 150, 300);
        Assert.True(SessionStackWindow.ExpansionChangesOpacity);
        Assert.Equal(1, SessionStackWindow.SessionGlassCaptureTargetCount);
        Assert.InRange(SessionStackWindow.SessionGlassBlurRadius, 18, 24);
        Assert.InRange(SessionStackWindow.SessionGlassMerging, 0.88, 0.95);
        Assert.Equal(74, SessionStackWindow.CollapsedOcclusionTop);
    }

    [Theory]
    [InlineData(0, false, 16)]
    [InlineData(0, true, 16)]
    [InlineData(1, false, 22)]
    [InlineData(1, true, 79)]
    [InlineData(2, false, 28)]
    [InlineData(2, true, 142)]
    public void Glass_mask_uses_absolute_card_positions(int index, bool expanded, double expected)
    {
        Assert.Equal(expected, SessionStackWindow.GlassMaskCardTop(index, expanded));
    }

    [Fact]
    public void Glass_mask_toggle_positions_match_the_two_stack_heights()
    {
        Assert.Equal(106, SessionStackWindow.GlassMaskToggleTop(3, false));
        Assert.Equal(232, SessionStackWindow.GlassMaskToggleTop(3, true));
    }

    [Fact]
    public void Session_glass_surface_is_a_readable_neutral_vertical_gradient()
    {
        var brush = SessionStackWindow.CreateSessionSurfaceBrush();

        Assert.Equal(0, brush.StartPoint.X);
        Assert.Equal(0, brush.StartPoint.Y);
        Assert.Equal(0, brush.EndPoint.X);
        Assert.Equal(1, brush.EndPoint.Y);
        Assert.Equal(2, brush.GradientStops.Count);
        Assert.Equal((255, 58, 58, 60), Components(brush.GradientStops[0].Color));
        Assert.Equal((255, 48, 48, 50), Components(brush.GradientStops[1].Color));
        Assert.Equal(OverlayGlassMaterial.CreateSurfaceBrush().GradientStops.Select(stop => (stop.Color, stop.Offset)),
            brush.GradientStops.Select(stop => (stop.Color, stop.Offset)));
        Assert.Equal(OverlayGlassMaterial.BlurRadius, SessionStackWindow.SessionGlassBlurRadius);
        Assert.Equal(OverlayGlassMaterial.Merging, SessionStackWindow.SessionGlassMerging);
    }

    [Fact]
    public void Session_shadow_slot_does_not_add_an_opaque_black_underlay()
    {
        Assert.Equal(0, SessionStackWindow.ShadowUnderlayAlpha);
    }

    [Fact]
    public void Collapsed_glass_layers_are_neutral_and_step_darker_with_depth()
    {
        var middle = SessionStackWindow.CreateCollapsedLayerSurfaceBrush(1);
        var back = SessionStackWindow.CreateCollapsedLayerSurfaceBrush(2);

        Assert.Equal((255, 58, 58, 60), Components(middle.GradientStops[0].Color));
        Assert.Equal((255, 44, 44, 46), Components(middle.GradientStops[1].Color));
        Assert.Equal((255, 52, 52, 54), Components(back.GradientStops[0].Color));
        Assert.Equal((255, 40, 40, 42), Components(back.GradientStops[1].Color));
    }

    [Fact]
    public void Session_xaml_defaults_to_opaque_layers_and_uses_the_current_pressed_material()
    {
        var xaml = XDocument.Load(Path.Combine(RepositoryPaths.Root, "src", "CodexQuotaTaskbar.Host", "UI", "SessionStackWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var middle = Assert.Single(xaml.Descendants(), element => (string?)element.Attribute(x + "Key") == "CollapsedMiddleGlassSurface");
        var back = Assert.Single(xaml.Descendants(), element => (string?)element.Attribute(x + "Key") == "CollapsedBackGlassSurface");
        Assert.Equal(new[] { "#FF3A3A3C", "#FF2C2C2E" },
            middle.Elements().Select(element => (string?)element.Attribute("Color")));
        Assert.Equal(new[] { "#FF343436", "#FF28282A" },
            back.Elements().Select(element => (string?)element.Attribute("Color")));

        var pressed = xaml.Descendants().Where(element => element.Name.LocalName == "Trigger" &&
            (string?)element.Attribute("Property") == "IsPressed").ToArray();
        Assert.Equal(2, pressed.Length);
        foreach (var trigger in pressed)
        {
            var setter = Assert.Single(trigger.Elements());
            Assert.Equal("Chrome", (string?)setter.Attribute("TargetName"));
            Assert.Equal("Background", (string?)setter.Attribute("Property"));
            Assert.Equal("{DynamicResource SessionPressedSurface}", (string?)setter.Attribute("Value"));
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void Glass_capture_starts_only_after_the_border_is_loaded(bool loaded, bool opaque, bool expected)
    {
        Assert.Equal(expected, SessionStackWindow.ShouldEnableGlass(loaded, opaque));
    }

    private static (byte A, byte R, byte G, byte B) Components(System.Windows.Media.Color color) =>
        (color.A, color.R, color.G, color.B);
}
