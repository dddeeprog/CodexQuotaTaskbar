using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Interop;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Host.Tests.TestSupport;
using CodexQuotaTaskbar.Host.UI;
using CodexQuotaTaskbar.Host.Platform;
using Color = System.Windows.Media.Color;
using Panel = System.Windows.Controls.Panel;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using FlowDirection = System.Windows.FlowDirection;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class CompanionTypingPreviewTests
{
    [Fact]
    public void Mapped_input_preserves_all_32_expressions_and_existing_gaze_targets() => Sta(() =>
    {
        double now = 0;
        using var visual = new CompanionBallVisual(() => now) { Width = 56, Height = 56 };
        visual.SetTypingInteraction(true);
        foreach (var id in CompanionEmotionDirector.EmotionIds)
        {
            now += 1000;
            visual.SetState(id, true);
            visual.Gaze(.8, -.4);
            var before = visual.GazeSample;
            var effective = visual.EffectiveEmotion;
            visual.Input(Key(30, true));
            Assert.Equal((ushort)30, visual.TypingFrame.Key);
            Assert.Contains(Bytes(Render(visual, 112, 192)), value => value > 0);
            Assert.Equal(id, visual.Emotion);
            Assert.Equal(effective, visual.EffectiveEmotion);
            Assert.Equal(before, visual.GazeSample);
            visual.Input(Key(30, false));
            Assert.False(visual.AnimationRunning);
        }
    });

    [Fact]
    public void A_space_and_mouse_button_have_distinct_rendered_targets_and_release_returns_to_idle() => Sta(() =>
    {
        double now = 0;
        using var visual = new CompanionBallVisual(() => now) { Width = 56, Height = 56 };
        visual.SetState("02", true);
        visual.SetTypingInteraction(true);
        var idle = Bytes(Render(visual, 336, 576));
        visual.Input(Key(30, true));
        var a = Bytes(Render(visual, 336, 576));
        visual.Input(Key(57, true));
        var space = Bytes(Render(visual, 336, 576));
        Assert.False(idle.SequenceEqual(a));
        Assert.False(a.SequenceEqual(space));
        visual.Input(Key(57, false));
        Assert.Equal(a, Bytes(Render(visual, 336, 576)));
        now = 60_000;
        Assert.Equal((ushort)30, visual.TypingFrame.Key);
        Assert.Equal(a, Bytes(Render(visual, 336, 576)));
        visual.Input(Key(30, false));
        Assert.Equal(idle, Bytes(Render(visual, 336, 576)));
        visual.Input(Mouse(CompanionMouseButton.Left, true));
        var left = Bytes(Render(visual, 336, 576));
        visual.Input(Mouse(CompanionMouseButton.Left, false));
        visual.Input(Mouse(CompanionMouseButton.Right, true));
        var right = Bytes(Render(visual, 336, 576));
        Assert.False(left.SequenceEqual(right));
        visual.Input(new(CompanionInputKind.Reset));
        Assert.Equal(idle, Bytes(Render(visual, 336, 576)));
        Assert.False(visual.AnimationRunning);
        Assert.False(visual.IsLoaded);
    });

    [Fact]
    public void Every_mapped_key_keeps_a_visible_cyan_cap_in_the_native_render() => Sta(() =>
    {
        double now = 0;
        using var visual = new CompanionBallVisual(() => now) { Width = 56, Height = 56 };
        visual.SetState("02", true); visual.SetTypingInteraction(true);
        foreach (var cap in CompanionTypingKeyboard.Keys)
        {
            visual.Input(Key(cap.Scan, true));
            foreach (var direction in new[] { -1d, 0d, 1d })
            {
                visual.Gaze(direction, 0); now += 1000;
                var pixels = Bytes(Render(visual, 336, 576));
                var visible = false;
                for (var index = 0; index < pixels.Length; index += 4)
                    if (pixels[index + 3] > 200 && pixels[index] > 175 && pixels[index + 1] > 175 && pixels[index + 2] < 130)
                    { visible = true; break; }
                Assert.True(visible, $"Physical cap {cap.Scan:x} is hidden at mouse direction {direction}.");
            }
            visual.Input(Key(cap.Scan, false));
        }
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Pebble_mouse_ink_outline_stays_visible_beneath_the_ball_hand(int value) => Sta(() =>
    {
        var button = (CompanionMouseButton)value;
        using var visual = new CompanionBallVisual(() => 0) { Width = 56, Height = 56 };
        visual.SetState("02", true); visual.SetTypingInteraction(true);
        if (button != CompanionMouseButton.None) visual.Input(Mouse(button, true));
        var pixels = Bytes(Render(visual, 336, 576));
        var ink = 0;
        for (var y = 35 * 6; y < 54 * 6; y++)
            for (var x = 0; x < 24 * 6; x++)
            {
                var offset = (y * 336 + x) * 4;
                if (pixels[offset + 3] > 200 && pixels[offset] < 70 && pixels[offset + 1] < 70 && pixels[offset + 2] < 70) ink++;
            }
        Assert.True(ink > 25, "The mouse's dark outline must remain exposed beyond the white ball hand.");
        var seam = 0;
        for (var y = 279; y < 288; y++)
            for (var x = 54; x < 78; x++)
            {
                var offset = (y * 336 + x) * 4;
                if (pixels[offset + 3] > 200 && pixels[offset] < 70 && pixels[offset + 1] < 70 && pixels[offset + 2] < 70) seam++;
            }
        Assert.True(seam > 4, "The mouse's short interior seam must remain visible in front of the ball.");
    });

    [Fact]
    public void Disable_reduced_hidden_unloaded_and_dispose_discard_held_state() => Sta(() =>
    {
        using var visual = new CompanionBallVisual(() => 0) { Width = 56, Height = 56 };
        visual.SetState("02", true);
        visual.SetTypingInteraction(true);
        void Active() { visual.Input(Key(30, true)); visual.Input(Mouse(CompanionMouseButton.Left, true)); Assert.True(visual.TypingFrame.Active); }
        Active();
        visual.SetTypingInteraction(false);
        Assert.Equal(CompanionTypingFrame.Rest, visual.TypingFrame);
        visual.SetTypingInteraction(true);
        Assert.Equal(CompanionTypingFrame.Idle, visual.TypingFrame);
        Active();
        visual.SetState("02", false);
        Assert.Equal(CompanionTypingFrame.Rest, visual.TypingFrame);
        visual.SetState("02", true);
        Assert.Equal(CompanionTypingFrame.Idle, visual.TypingFrame);
        Active();
        visual.Visibility = Visibility.Hidden;
        visual.Input(Key(57, true));
        visual.Input(new(CompanionInputKind.Reset));
        Assert.Equal(CompanionTypingFrame.Rest, visual.TypingFrame);
        visual.Visibility = Visibility.Visible;
        Assert.Equal(CompanionTypingFrame.Idle, visual.TypingFrame);
        Active();
        visual.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Assert.Equal(CompanionTypingFrame.Idle, visual.TypingFrame);
        Active();
        visual.Dispose();
        visual.Input(Key(30, true));
        Assert.Equal(CompanionTypingFrame.Rest, visual.TypingFrame);
        Assert.False(visual.AnimationRunning);
    });

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void Every_key_and_mouse_direction_fit_inside_existing_56_dip_surface(int dpi) => Sta(() =>
    {
        double now = 0;
        using var visual = new CompanionBallVisual(() => now) { Width = 56, Height = 56 };
        visual.SetState("02", true);
        visual.SetTypingInteraction(true);
        foreach (var cap in CompanionTypingKeyboard.Keys)
        {
            visual.Input(Key(cap.Scan, true));
            foreach (var direction in new[] { (-1d,-1d), (1d,1d) })
            {
                visual.Gaze(direction.Item1, direction.Item2);
                now += 1000;
                var pixels = Render(visual, (int)(56 * dpi / 96d), dpi);
                var bytes = Bytes(pixels);
                for (var x = 0; x < pixels.PixelWidth; x++)
                {
                    Assert.Equal(0, bytes[x * 4 + 3]);
                    Assert.Equal(0, bytes[((pixels.PixelHeight - 1) * pixels.PixelWidth + x) * 4 + 3]);
                }
                for (var y = 0; y < pixels.PixelHeight; y++)
                {
                    Assert.Equal(0, bytes[(y * pixels.PixelWidth) * 4 + 3]);
                    Assert.Equal(0, bytes[(y * pixels.PixelWidth + pixels.PixelWidth - 1) * 4 + 3]);
                }
            }
            visual.Input(Key(cap.Scan, false));
        }
    });

    [Fact]
    public void Optional_mapped_preview_uses_only_synthetic_state_and_never_shows_a_window() => Sta(() =>
    {
        var configured = Environment.GetEnvironmentVariable("CQT_TYPING_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(configured)) return;
        var directory = Path.GetFullPath(configured);
        Assert.Equal(Path.GetFullPath(Path.Combine(RepositoryPaths.Root, "..", "typing-mouse-style-preview")), directory, ignoreCase: true);
        Directory.CreateDirectory(directory);
        var frames = Path.Combine(directory, "frames");
        Directory.CreateDirectory(frames);
        double now = 0;
        using var visual = new CompanionBallVisual(() => now) { Width = 56, Height = 56 };
        visual.SetState("02", true);
        visual.SetTypingInteraction(true);
        var label = "无按键 · 两颗小手恢复圆形";
        for (var index = 0; index < 108; index++)
        {
            now = index * 50;
            switch (index)
            {
                case 6: visual.Input(Key(30, true)); label = "A 按下并保持 · 对应键帽亮起"; break;
                case 20: visual.Input(Key(57, true)); label = "保持 A，再按空格 · 按键球移到空格并轻微压扁"; break;
                case 30: visual.Input(Key(30, true)); label = "A 自动重复 · 仍保持最后按下的空格"; break;
                case 36: visual.Input(Key(57, false)); label = "松开空格 · 回到仍按住的 A"; break;
                case 46: visual.Input(Key(30, false)); label = "全部松开 · 按键球恢复圆形"; break;
                case 52: visual.Input(Key(57, true)); label = "空格按下并保持 · 对应长键帽亮起"; break;
                case 66: visual.Input(Key(57, false)); label = "松开空格 · 恢复圆形"; break;
                case 72: visual.Input(Mouse(CompanionMouseButton.Left, true)); label = "鼠标左键按住 · 左键独立亮起"; break;
                case 82: visual.Input(Mouse(CompanionMouseButton.Right, true)); label = "鼠标左右键同时按住 · 状态独立"; break;
                case 90: visual.Input(Mouse(CompanionMouseButton.Left, false)); label = "只松开左键 · 右键仍保持"; break;
                case 98: visual.Input(Mouse(CompanionMouseButton.Right, false)); label = "鼠标全部松开 · 回到握鼠标姿态"; break;
            }
            if (index >= 68) visual.Gaze(Math.Sin((index - 68) * .15) * .8, Math.Cos((index - 68) * .15) * .5);
            // Exercise the same pose sampler as CompositionTarget.Rendering, offscreen.
            var pose = (CompanionPoseEngine)typeof(CompanionBallVisual).GetField("engine", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(visual)!;
            pose.Sample(now, true);
            var rendered = Render(visual, 336, 576);
            var tile = new DrawingVisual();
            using (var draw = tile.RenderOpen())
            {
                draw.DrawRectangle(new SolidColorBrush(Color.FromRgb(42,43,47)), null, new Rect(0,0,680,450));
                draw.DrawText(Text("点岛 · 鼠标与鼠标垫原生预览", 21, Colors.White), new Point(24,18));
                draw.DrawText(Text("白色鹅卵石鼠标 · 灰色透视垫 · 合成输入预览", 11, Color.FromRgb(182,185,193)), new Point(24,50));
                draw.DrawImage(rendered, new Rect(24,70,336,336));
                draw.DrawText(Text("原尺寸 56 DIP", 13, Colors.White), new Point(454,145));
                draw.DrawImage(rendered, new Rect(476,178,56,56));
                draw.DrawText(Text("画面左：鼠标球", 12, Colors.White), new Point(425,270));
                draw.DrawText(Text("画面右：按键球", 12, Colors.White), new Point(425,295));
                draw.DrawText(Text("按住轻压 · 松开恢复", 12, Colors.White), new Point(425,320));
                draw.DrawText(Text(label, 14, Color.FromRgb(115,234,233)), new Point(24,420));
            }
            var output = new RenderTargetBitmap(680,450,96,96,PixelFormats.Pbgra32);
            output.Render(tile);
            SavePng(Path.Combine(frames, $"mapped-{index:D3}.png"), output);
            if (index == 58) SavePng(Path.Combine(directory,"mapped-space-contact.png"), output);
        }
        File.WriteAllText(Path.Combine(frames,"timing.json"), "{\"frame_count\":108,\"duration_ms\":50,\"loop\":0,\"pattern\":\"mapped-%03d.png\"}");
        SaveContext(directory, false, false);
        SaveContext(directory, true, false);
        SaveContext(directory, true, true);
        Assert.False(visual.AnimationRunning);
        Assert.False(visual.IsLoaded);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Native_context_keeps_quota_text_and_task_interactions_clear_of_the_typing_region(bool unified, bool collapsed) =>
        Sta(() => SaveContext(null, unified, collapsed));

    [Fact]
    public void Extra_classic_spacing_is_reserved_only_for_effective_typing_and_unified_spacing_stays_fixed()
    {
        Assert.Equal(new Thickness(8, 5, 32, 5), QuotaCapsuleWindow.CompanionQuotaMargin(false, true, true, false, true));
        Assert.Equal(new Thickness(8, 5, 14, 5), QuotaCapsuleWindow.CompanionQuotaMargin(false, true, false, false, true));
        Assert.Equal(new Thickness(8, 5, 14, 5), QuotaCapsuleWindow.CompanionQuotaMargin(false, true, true, true, true));
        Assert.Equal(new Thickness(8, 5, 14, 5), QuotaCapsuleWindow.CompanionQuotaMargin(false, false, true, false, true));
        Assert.Equal(new Thickness(8, 5, 14, 5), QuotaCapsuleWindow.CompanionQuotaMargin(false, true, true, false, false));
        Assert.Equal(new Thickness(14, 0, 46, 0), QuotaCapsuleWindow.CompanionQuotaMargin(true, true, true, false, true));
        Assert.Equal(new Thickness(14, 0, 46, 0), QuotaCapsuleWindow.CompanionQuotaMargin(true, true, false, false, true));
    }

    private static void SaveContext(string? directory, bool unified, bool collapsed)
    {
        var now = DateTimeOffset.Parse("2026-10-24T08:00:00+08:00", System.Globalization.CultureInfo.InvariantCulture);
        var sessions = CodexSessionsSnapshot.Create([
            new("typing-preview-a", "示例任务 · 等待确认", CodexSessionState.Waiting, now),
            new("typing-preview-b", "示例任务 · 正在构建", CodexSessionState.Running, now.AddSeconds(-1)),
            new("typing-preview-c", "示例任务 · 完成检查", CodexSessionState.Review, now.AddSeconds(-2))], now);
        var window = new QuotaCapsuleWindow("typing-context-preview", unified);
        SessionStackWindow? stack = null;
        double frameTime = 0;
        using var ball = new CompanionBallVisual(() => frameTime)
        {
            Width = 56, Height = 56, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
        };
        try
        {
            window.Apply(QuotaSnapshot.Available([
                new(QuotaWindowKind.Primary, 72, 300, now.AddHours(3)),
                new(QuotaWindowKind.Secondary, 41, 10080, now.AddDays(4))], now));
            window.ApplySessions(sessions);
            if (unified && collapsed) window.TogglePanelSessions();
            var shell = (Grid)window.Content;
            var badge = Assert.IsType<Border>(window.FindName("SessionBadge"));
            badge.Visibility = Visibility.Collapsed;
            Assert.IsType<Grid>(window.FindName("QuotaHeader")).Margin =
                QuotaCapsuleWindow.CompanionQuotaMargin(unified, true, true, false, true);
            Panel.SetZIndex(ball, 5);
            shell.Children.Add(ball);
            ball.SetState("02", true);
            ball.SetTypingInteraction(true);
            ball.Input(Key(57, true));
            frameTime = 110;
            Layout(shell, window.LayoutWidth, window.DesiredHeight);
            var expected = new Rect(window.LayoutWidth - 56, 0, 56, 56);
            var actual = ball.TransformToAncestor(shell).TransformBounds(new Rect(0, 0, 56, 56));
            Assert.Equal(expected, actual);
            var decoration = new Rect(actual.Left + 6, actual.Top + 36, 46, 17);
            foreach (var name in new[] { "FirstValue", "SecondValue" })
            {
                var text = Assert.IsType<TextBlock>(window.FindName(name));
                Assert.False(string.IsNullOrWhiteSpace(text.Text));
                var textBounds = text.TransformToAncestor(shell).TransformBounds(new Rect(text.RenderSize));
                Assert.True(textBounds.Width > 0 && textBounds.Height > 0);
                Assert.False(textBounds.IntersectsWith(decoration), $"{name} {textBounds} intersects typing area {decoration}");
            }
            if (unified)
            {
                Assert.Equal(!collapsed, window.PanelExpanded);
                var taskList = Assert.IsType<StackPanel>(window.FindName("PanelSessionsList"));
                var buttons = taskList.Children.OfType<System.Windows.Controls.Button>().ToArray();
                Assert.Equal(3, buttons.Length);
                Assert.All(buttons, button => Assert.True(button.IsEnabled && button.IsHitTestVisible));
                var region = Assert.IsType<Grid>(window.FindName("PanelSessionsRegion"));
                var regionBounds = region.TransformToAncestor(shell).TransformBounds(new Rect(region.RenderSize));
                Assert.True(regionBounds.Height > 0);
                Assert.False(regionBounds.IntersectsWith(decoration));
            }
            var capsule = Snapshot(shell, window.LayoutWidth, window.DesiredHeight);
            BitmapSource? cards = null;
            if (!unified)
            {
                stack = new SessionStackWindow("typing-context-preview");
                stack.Apply(sessions);
                var stackContent = (FrameworkElement)stack.Content;
                Layout(stackContent, SessionStackWindow.StackWidth, stack.DesiredHeight);
                cards = Snapshot(stackContent, SessionStackWindow.StackWidth, stack.DesiredHeight);
            }
            var width = Math.Max(capsule.PixelWidth, cards?.PixelWidth ?? 0) / 2d;
            var height = capsule.PixelHeight / 2d + (cards is null ? 0 : cards.PixelHeight / 2d + 8);
            if (directory is null)
            {
                Assert.False(window.IsVisible);
                Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Handle);
                return;
            }
            var sheet = new DrawingVisual();
            using (var dc = sheet.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(35, 37, 42)), null, new Rect(0, 0, width + 32, height + 65));
                dc.DrawText(Text(unified ? collapsed ? "一体折叠 · 离屏示例" : "一体面板 · 离屏示例" : "经典气泡 · 离屏示例", 13, Colors.White), new Point(16, 8));
                dc.DrawText(Text("原生额度/任务控件 + 固定56 DIP合成动画；非现场", 9, Color.FromRgb(177, 181, 190)), new Point(16, 27));
                dc.DrawImage(capsule, new Rect(16 + (width - capsule.PixelWidth / 2d) / 2, 48, capsule.PixelWidth / 2d, capsule.PixelHeight / 2d));
                if (cards is not null) dc.DrawImage(cards, new Rect(16 + (width - cards.PixelWidth / 2d) / 2, 56 + capsule.PixelHeight / 2d, cards.PixelWidth / 2d, cards.PixelHeight / 2d));
            }
            var output = new RenderTargetBitmap((int)Math.Ceiling((width + 32) * 2), (int)Math.Ceiling((height + 65) * 2), 192, 192, PixelFormats.Pbgra32);
            output.Render(sheet);
            SavePng(Path.Combine(directory, unified ? collapsed ? "typing-context-unified-collapsed.png" : "typing-context-unified.png" : "typing-context-classic.png"), output);
            Assert.False(window.IsVisible);
            Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Handle);
            if (stack is not null)
            {
                Assert.False(stack.IsVisible);
                Assert.Equal(IntPtr.Zero, new WindowInteropHelper(stack).Handle);
            }
        }
        finally { stack?.Close(); window.Close(); }
    }

    private static CompanionInputEvent Key(ushort scan, bool down) => new(CompanionInputKind.Key, scan, down);
    private static CompanionInputEvent Mouse(CompanionMouseButton button, bool down) => new(CompanionInputKind.MouseButton, 0, down, button);

    private static void Layout(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static RenderTargetBitmap Snapshot(Visual visual, double width, double height)
    {
        var output = new RenderTargetBitmap((int)Math.Ceiling(width * 2), (int)Math.Ceiling(height * 2), 192, 192, PixelFormats.Pbgra32);
        output.Render(visual);
        return output;
    }

    private static FormattedText Text(string text, double size, Color color) => new(text,
        System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
        new Typeface(OverlayTypography.Interface, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
        size, new SolidColorBrush(color), 1);

    private static void SavePng(string path, BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static RenderTargetBitmap Render(CompanionBallVisual visual, int pixels, int dpi)
    {
        visual.InvalidateVisual();
        visual.Measure(new Size(56, 56));
        visual.Arrange(new Rect(0, 0, 56, 56));
        visual.UpdateLayout();
        var output = new RenderTargetBitmap(pixels, pixels, dpi, dpi, PixelFormats.Pbgra32);
        output.Render(visual);
        return output;
    }

    private static byte[] Bytes(BitmapSource bitmap)
    {
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        return bytes;
    }

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Native typing preview timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}


