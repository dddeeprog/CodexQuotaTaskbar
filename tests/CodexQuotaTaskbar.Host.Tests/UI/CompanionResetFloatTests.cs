using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.Resets;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

// Dedicated additive-motion checks/export; the older interaction and recovery
// exporters and their media remain independent. No Window, input or account client.
[Collection("Native UI isolation")]
public sealed class CompanionResetFloatTests
{
    private static readonly ResetVisualState[] States = [ResetVisualState.None, ResetVisualState.Checking,
        ResetVisualState.Failed, ResetVisualState.Upcoming, ResetVisualState.Confirmed];
    private static readonly MethodInfo FrameMethod = typeof(CompanionBallVisual).GetMethod("OnFrame", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo RenderMethod = typeof(CompanionBallVisual).GetMethod("OnRender", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Fact]
    public void Float_is_bounded_smooth_and_reduced_motion_has_no_added_offset()
    {
        foreach (var state in States)
        {
            for (var ms = 0; ms <= 6400; ms += 20)
            {
                var sample = CompanionBallVisual.SampleResetFloat(state, ms, true);
                Assert.Equal(CompanionBallVisual.ResetFloatFrame.Rest, CompanionBallVisual.SampleResetFloat(state, ms, false));
                Assert.InRange(sample.BodyY, state is ResetVisualState.None or ResetVisualState.Checking ? -28 : -12, 0);
                Assert.InRange(sample.GlowScaleX, 1, 1.12);
                Assert.InRange(sample.GlowScaleY, 1, 1.08);
                Assert.InRange(sample.GlowOpacity, .82, 1);
            }
            Assert.Equal(CompanionBallVisual.ResetFloatFrame.Rest, CompanionBallVisual.SampleResetFloat(state, 0, true));
            Assert.Equal(CompanionBallVisual.ResetFloatFrame.Rest, CompanionBallVisual.SampleResetFloat(state,
                state is ResetVisualState.None or ResetVisualState.Checking ? 6000 : 3200, true));
        }
        var peak = CompanionBallVisual.SampleResetFloat(ResetVisualState.Confirmed, 1600, true);
        Assert.Equal(-12, peak.BodyY); Assert.Equal(1.12, peak.GlowScaleX); Assert.Equal(.82, peak.GlowOpacity, 10);
        Assert.InRange(-peak.BodyY * 56 * .64 / 259, 1.65, 1.67);
        var before = GC.GetAllocatedBytesForCurrentThread(); var sum = 0d;
        for (var i = 0; i < 10000; i++) sum += CompanionBallVisual.SampleResetFloat(ResetVisualState.Confirmed, i, true).BodyY;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated); Assert.True(sum < 0);
    }

    [Fact]
    public void Repeated_status_and_color_changes_do_not_restart_phase_and_hidden_reduced_disposed_stop_sampling() => Sta(() =>
    {
        var ms = 0d; using var visual = new CompanionBallVisual(() => ms);
        visual.SetState("35", true); visual.SetResetState(ResetVisualState.Failed);
        ms = 800; Frame(visual);
        var first = visual.FloatFrame;
        Assert.InRange(first.BodyY, -6.01, -5.99);
        visual.SetResetState(ResetVisualState.Failed);
        visual.SetResetState(ResetVisualState.Confirmed);
        ms = 1600; Frame(visual);
        Assert.Equal(-12, visual.FloatFrame.BodyY);
        var count = visual.FrameCount;
        visual.Visibility = Visibility.Hidden;
        Frame(visual); Assert.Equal(count, visual.FrameCount);
        Assert.False(visual.AnimationRunning);
        // The production Stopwatch pauses when hidden. This injected clock is
        // deliberately held at the same value, then advanced on resumed frames.
        visual.Visibility = Visibility.Visible;
        ms = 2400; Frame(visual); Assert.InRange(visual.FloatFrame.BodyY, -6.01, -5.99);
        visual.SetState("35", false);
        Assert.Equal(CompanionBallVisual.ResetFloatFrame.Rest, visual.FloatFrame);
        count = visual.FrameCount; ms = 2600; Frame(visual); Assert.Equal(count, visual.FrameCount);
        visual.SetState("35", true); Frame(visual);
        var beforeReturn = visual.FloatFrame;
        visual.SetResetState(ResetVisualState.None);
        Assert.Equal(beforeReturn, visual.FloatFrame);
        var clock = Field<Stopwatch>(visual, "clock"); clock.Start();
        visual.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Assert.False(clock.IsRunning); Assert.False(visual.AnimationRunning);
        count = visual.FrameCount; visual.Dispose(); ms = 2800; Frame(visual); Assert.Equal(count, visual.FrameCount);
        Assert.Null(PresentationSource.FromVisual(visual)); Assert.False(visual.IsLoaded);
        using var unloaded = new CompanionBallVisual();
        unloaded.SetResetState(ResetVisualState.Confirmed); unloaded.SetState("35", true); Frame(unloaded);
        Assert.Equal(0, unloaded.FrameCount); Assert.Equal(CompanionBallVisual.ResetFloatFrame.Rest, unloaded.FloatFrame);
    });

    [Theory]
    [InlineData((int)ResetVisualState.Checking)]
    [InlineData((int)ResetVisualState.Failed)]
    [InlineData((int)ResetVisualState.Upcoming)]
    [InlineData((int)ResetVisualState.Confirmed)]
    public void All_32_poses_four_phases_sizes_and_dpis_keep_body_and_halo_inside_hit_canvas(int stateValue) => Sta(() =>
    {
        var state = (ResetVisualState)stateValue;
        var bodyBrush = Static<Brush>("Body");
        var ringName = state switch { ResetVisualState.Failed => "RedResetRing",
            ResetVisualState.Upcoming => "GoldResetRing", _ => "ResetRing" };
        foreach (var definition in CompanionCatalog.Shared.Value.Emotions)
        {
            var ms = 0d; using var visual = new CompanionBallVisual(() => ms);
            visual.SetState(definition.Id, true); visual.SetResetState(state);
            var ring = (DrawingGroup)(state == ResetVisualState.Checking ? visual.NormalRingDrawing : Static<Lazy<Drawing>>(ringName).Value);
            Assert.Equal(state != ResetVisualState.Checking, ring.IsFrozen);
            var halo = ring.Children[0] is DrawingGroup masked ? (GeometryDrawing)masked.Children[0] : (GeometryDrawing)ring.Children[0];
            var support = (GeometryDrawing)ring.Children[2];
            foreach (var phase in state == ResetVisualState.Checking ? new[] { 0, 1500, 3000, 4500 } : new[] { 0, 800, 1600, 2400 })
            {
                ms = phase; Frame(visual);
                var pose = Field<CompanionPoseEngine>(visual, "engine").Pose.ToArray();
                foreach (var size in new[] { 52d, 56d })
                {
                    var drawing = Record(visual, size);
                    var entries = Geometries(drawing, Matrix.Identity).ToArray();
                    var head = entries.Single(e => ReferenceEquals(e.Shape.Brush, bodyBrush));
                    var glow = entries.Single(e => ReferenceEquals(e.Shape.Geometry, halo.Geometry));
                    var solid = entries.Single(e => ReferenceEquals(e.Shape.Geometry, support.Geometry));
                    var box = new Rect(0, 0, size, size);
                    Assert.True(box.Contains(Rect.Transform(head.Shape.Bounds, head.Transform)), $"Body clipped: {state}/{definition.Id}/{phase}/{size}");
                    Assert.True(box.Contains(Rect.Transform(glow.Shape.Bounds, glow.Transform)), $"Halo clipped: {state}/{definition.Id}/{phase}/{size}");
                    var scale = size * .64 / 259;
                    Assert.Equal(scale, solid.Transform.M11, 10); Assert.Equal(scale, solid.Transform.M22, 10);
                    Assert.Equal(size / 2 - 114.2705 * scale, solid.Transform.OffsetY, 10);
                    var center = head.Transform.Transform(new Point(114.2705, 114.2705));
                    Assert.Equal(size / 2 + (pose[1] + visual.FloatFrame.BodyY) * scale, center.Y, 8);
                    Assert.Equal(pose, Field<CompanionPoseEngine>(visual, "engine").Pose);
                    foreach (var dpi in new[] { 96, 144, 192 })
                    {
                        var bitmap = Raster(drawing, size, dpi);
                        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                        Assert.Contains(pixels, p => p != 0);
                        // Body/halo bounds above are exact. Other pre-existing
                        // particles/letter decorations retain their own anchors.
                    }
                }
            }
            Assert.False(visual.AnimationRunning); Assert.Null(PresentationSource.FromVisual(visual));
        }
    }, 120);

    [Fact]
    public void Export_floating_comparison_only_when_explicitly_requested() => Sta(() =>
    {
        var directory = Environment.GetEnvironmentVariable("CQT_FLOAT_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        var ffmpeg = Environment.GetEnvironmentVariable("CQT_PREVIEW_FFMPEG"); Assert.True(File.Exists(ffmpeg));
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "diandao-floating-ring-preview.mp4");
        Assert.False(File.Exists(target), "Existing previews must not be overwritten.");
        const int width = 1280, height = 720, fps = 30, frames = 552;
        var ms = 0d;
        var visuals = States.Select(state => { var v = new CompanionBallVisual(() => ms); v.SetState("35", true); v.SetResetState(state); return v; }).ToArray();
        var start = new ProcessStartInfo(ffmpeg!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-n", "-f", "rawvideo", "-pixel_format", "bgra", "-video_size", "1280x720",
            "-framerate", "30", "-i", "pipe:0", "-an", "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p", "-movflags", "+faststart", target }) start.ArgumentList.Add(arg);
        using var encoder = Process.Start(start)!; var errors = encoder.StandardError.ReadToEndAsync();
        var canvas = new DrawingVisual(); var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var pixels = new byte[width * height * 4];
        var names = new[] { "普通白球", "蓝 · 查询中", "红 · 查询异常", "金 · 重置预告", "绿 · 额度恢复" };
        var background = new SolidColorBrush(Color.FromRgb(23, 25, 28)); background.Freeze();
        var proofs = new HashSet<int> { 0, 24, 48, 72, 96, 288, 312, 336, 384, 480, 551 };
        try
        {
            for (var frame = 0; frame < frames; frame++)
            {
                var seconds = frame / (double)fps;
                var reduced = frame is >= 288 and < 336;
                ms = (seconds < 9.6 ? seconds : seconds < 11.2 ? 9.6 : seconds - 1.6) * 1000;
                for (var i = 0; i < visuals.Length; i++)
                {
                    visuals[i].SetResetState(States[i]); // Deliberately repeat state without phase restart.
                    visuals[i].SetState("35", !reduced);
                    Frame(visuals[i]);
                }
                using (var dc = canvas.RenderOpen())
                {
                    dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
                    Text(dc, "点岛 · 白球与光晕联动", 28, 42, 30);
                    Text(dc, "未安装候选 / 演示数据 / 实际 WPF 绘制与动画时钟采样", 14, 44, 78, Brushes.Silver);
                    Text(dc, reduced ? "减少动态效果：浮动、旋转、柔闪静止" : "轻轻起落 · 3.2 秒一周期 · 普通态不增加浮动", 18, 44, 116);
                    for (var i = 0; i < visuals.Length; i++)
                    {
                        var x = 38 + i * 248;
                        Text(dc, names[i], 18, x + 26, 175);
                        var drawing = Record(visuals[i], 56);
                        dc.PushTransform(new TranslateTransform(x + 8, 215)); dc.PushTransform(new ScaleTransform(3.5, 3.5));
                        dc.DrawDrawing(drawing); dc.Pop(); dc.Pop();
                        Text(dc, "3.5× 动效对照", 12, x + 50, 425, Brushes.Silver);
                        dc.PushTransform(new TranslateTransform(x + 78, 464)); dc.DrawDrawing(drawing); dc.Pop();
                        Text(dc, "56 DIP 原尺寸", 12, x + 51, 532, Brushes.Silver);
                        Text(dc, $"新增位移 {visuals[i].FloatFrame.BodyY * 56 * .64 / 259:F2} DIP", 13, x + 35, 570);
                    }
                    Text(dc, "球升高时光晕更散，落下时更集中；实体底座固定。眼睛随球体一起移动，原 32 表情与键鼠动作保留。", 13, 42, 630, Brushes.Silver);
                    Text(dc, "没有移动窗口或点击区域 · 无 HWND / 无鼠标录制 / 无真实账号 · 减少动态演示在 9.6–11.2 秒", 13, 42, 666, Brushes.Silver);
                }
                bitmap.Clear(); bitmap.Render(canvas); bitmap.CopyPixels(pixels, width * 4, 0); encoder.StandardInput.BaseStream.Write(pixels);
                if (proofs.Contains(frame))
                {
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(directory, $"proof-{frame:D3}.png")); png.Save(stream);
                }
            }
            Assert.All(visuals, v => { Assert.False(v.AnimationRunning); Assert.False(v.IsLoaded); Assert.Null(PresentationSource.FromVisual(v)); });
        }
        finally { foreach (var visual in visuals) visual.Dispose(); encoder.StandardInput.Close(); }
        Assert.True(encoder.WaitForExit(30000)); Assert.Equal(0, encoder.ExitCode); Assert.True(string.IsNullOrWhiteSpace(errors.GetAwaiter().GetResult()));
    }, 120);

    private static void Frame(CompanionBallVisual visual) => FrameMethod.Invoke(visual, [null, EventArgs.Empty]);
    private static DrawingGroup Record(CompanionBallVisual visual, double size)
    {
        visual.Measure(new Size(size, size)); visual.Arrange(new Rect(0, 0, size, size));
        var drawing = new DrawingGroup(); using var dc = drawing.Open(); RenderMethod.Invoke(visual, [dc]); return drawing;
    }
    private static RenderTargetBitmap Raster(Drawing drawing, double size, int dpi)
    {
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) dc.DrawDrawing(drawing);
        var pixels = (int)Math.Ceiling(size * dpi / 96);
        var bitmap = new RenderTargetBitmap(pixels, pixels, dpi, dpi, PixelFormats.Pbgra32); bitmap.Render(visual); return bitmap;
    }
    private static IEnumerable<(GeometryDrawing Shape, Matrix Transform)> Geometries(Drawing drawing, Matrix parent)
    {
        if (drawing is GeometryDrawing geometry) yield return (geometry, parent);
        if (drawing is DrawingGroup group)
        {
            var matrix = group.Transform?.Value ?? Matrix.Identity; matrix.Append(parent);
            foreach (var child in group.Children)
                foreach (var item in Geometries(child, matrix)) yield return item;
        }
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static T Static<T>(string name) => (T)typeof(CompanionBallVisual).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    private static void Text(DrawingContext dc, string text, double size, double x, double y, Brush? brush = null) => dc.DrawText(
        new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, brush ?? Brushes.WhiteSmoke, 1), new Point(x, y));
    private static void Sta(Action action, int seconds = 30)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(seconds)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
