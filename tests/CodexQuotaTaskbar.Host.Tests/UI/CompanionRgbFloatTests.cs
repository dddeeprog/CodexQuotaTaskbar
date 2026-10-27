using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.Resets;
using CodexQuotaTaskbar.Host.Platform;
using CodexQuotaTaskbar.Host.UI;
using Xunit.Abstractions;

namespace CodexQuotaTaskbar.Host.Tests.UI;

[Collection("Native UI isolation")]
public sealed class CompanionRgbFloatTests(ITestOutputHelper output)
{
    private static readonly MethodInfo FrameMethod = typeof(CompanionBallVisual).GetMethod("OnFrame", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo RenderMethod = typeof(CompanionBallVisual).GetMethod("OnRender", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Fact]
    public void Ordinary_float_has_visible_default_size_excursion_and_breathes_in_phase_without_allocating()
    {
        var bottom = CompanionBallVisual.SampleResetFloat(ResetVisualState.None, 0, true);
        var top = CompanionBallVisual.SampleResetFloat(ResetVisualState.None, 3000, true);
        Assert.Equal(0, bottom.BodyY); Assert.Equal(-28, top.BodyY);
        foreach (var size in new[] { 52d, 56d }) Assert.InRange(-top.BodyY * size * .64 / 259, 3.59, 3.88);
        Assert.Equal(bottom, CompanionBallVisual.SampleResetFloat(ResetVisualState.None, 6000, true));
        Assert.Equal(top, CompanionBallVisual.SampleResetFloat(ResetVisualState.Checking, 3000, true));
        Assert.True(top.GlowScaleX > bottom.GlowScaleX); Assert.True(top.GlowOpacity < bottom.GlowOpacity);
        Assert.True(CompanionBallVisual.SampleNormalRing(3000, true).Opacity < CompanionBallVisual.SampleNormalRing(0, true).Opacity);
        for (var ms = 1; ms <= 6000; ms++)
            Assert.InRange(Math.Abs(CompanionBallVisual.SampleResetFloat(ResetVisualState.None, ms, true).BodyY
                - CompanionBallVisual.SampleResetFloat(ResetVisualState.None, ms - 1, true).BodyY), 0, .015);
        var before = GC.GetAllocatedBytesForCurrentThread(); var sum = 0d;
        for (var i = 0; i < 10000; i++) sum += CompanionBallVisual.SampleResetFloat(ResetVisualState.None, i, true).BodyY;
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before); Assert.True(sum < 0);
    }

    [Fact]
    public void Reflection_is_cached_lower_arc_same_rgb_and_fades_with_height_without_changing_white_body() => Sta(() =>
    {
        var ms = 0d; using var v = new CompanionBallVisual(() => ms); v.SetState("35", true); Frame(v);
        var reflection = (GeometryDrawing)v.NormalReflectionDrawing;
        var brush = Assert.IsType<RadialGradientBrush>(reflection.Brush);
        var stops = brush.GradientStops.ToArray(); var geometry = reflection.Geometry;
        Assert.True(geometry.IsFrozen); Assert.Null(reflection.Pen);
        Assert.Equal(BrushMappingMode.Absolute, brush.MappingMode);
        Assert.Equal(geometry.Bounds.Height * .34, brush.RadiusY);
        Assert.Equal((byte)0, stops[2].Color.A); Assert.Equal((byte)0, stops[3].Color.A);
        // Nonzero support begins below 1.06 - .9*.34 = .754 of the head bounds.
        Assert.True(brush.Center.Y - stops[2].Offset * brush.RadiusY > geometry.Bounds.Y + geometry.Bounds.Height * .75);
        var close = v.NormalReflectionOpacity;
        ms = 3000; Frame(v); Assert.True(v.NormalReflectionOpacity < close * .4);
        foreach (var stop in stops) Assert.Equal((v.NormalFrame.Color.R, v.NormalFrame.Color.G, v.NormalFrame.Color.B), (stop.Color.R, stop.Color.G, stop.Color.B));
        var update = typeof(CompanionBallVisual).GetMethod("UpdateNormalRing", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Action>(v);
        for (var i = 0; i < 1000; i++) { ms += 33; update(); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) { ms += 33; update(); }
        var allocation = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"RGB + reflection brush updates: {allocation} B / 10000 ({allocation / 10000d:F2} B/sample)");
        Assert.InRange(allocation, 0, 10000L * 1024);
        Assert.Same(reflection, v.NormalReflectionDrawing); Assert.Same(brush, reflection.Brush); Assert.Same(geometry, reflection.Geometry);
        for (var i = 0; i < stops.Length; i++) Assert.Same(stops[i], brush.GradientStops[i]);
        var body = (Brush)typeof(CompanionBallVisual).GetField("Body", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Assert.All(((GradientBrush)body).GradientStops, s => Assert.InRange(Math.Abs(s.Color.R - s.Color.G), 0, 0));
        Assert.False(v.IsHitTestVisible); Assert.False(v.IsLoaded); Assert.Null(PresentationSource.FromVisual(v));
    });

    [Theory]
    [InlineData(0)]
    [InlineData(35)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Bottom_light_stays_world_down_during_tilt_and_inversion(double degrees) => Sta(() =>
    {
        using var v = new CompanionBallVisual(() => 0); v.SetState("35", false);
        var engine = (CompanionPoseEngine)typeof(CompanionBallVisual).GetField("engine", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(v)!;
        engine.Pose[3] = degrees;
        Record(v, 56);
        var reflection = (GeometryDrawing)v.NormalReflectionDrawing; var brush = (RadialGradientBrush)reflection.Brush;
        var body = Matrix.Identity; body.Translate(-114.2705, -114.2705); body.Scale(engine.Pose[2], engine.Pose[2]); body.Rotate(degrees);
        body.Translate(114.2705 + engine.Pose[0], 114.2705 + engine.Pose[1]);
        var actual = brush.Transform.Value; actual.Append(body);
        Assert.Equal(1, actual.M11, 10); Assert.Equal(1, actual.M22, 10);
        Assert.Equal(0, actual.M12, 10); Assert.Equal(0, actual.M21, 10);
        Assert.Equal(0, actual.OffsetX, 9); Assert.Equal(0, actual.OffsetY, 9);
        var bounds = Rect.Transform(reflection.Geometry.Bounds, body);
        Assert.True(brush.Center.Y - .9 * brush.RadiusY > bounds.Top + .75 * bounds.Height);
        Assert.Same(reflection.Geometry, ((GeometryDrawing)v.NormalReflectionDrawing).Geometry);
    });

    [Fact]
    public void Ordinary_special_retarget_keeps_current_pose_and_repeated_poll_does_not_restart_it() => Sta(() =>
    {
        var ms = 0d; using var v = new CompanionBallVisual(() => ms); v.SetState("35", true);
        ms = 3000; Frame(v); var old = v.FloatFrame; Assert.Equal(-28, old.BodyY);
        v.SetResetState(ResetVisualState.Confirmed); Assert.Equal(old, v.FloatFrame);
        ms += 40; Frame(v); Assert.InRange(v.FloatFrame.BodyY, -28, -12);
        v.SetResetState(ResetVisualState.Confirmed);
        ms = 3300; Frame(v); Assert.Equal(CompanionBallVisual.SampleResetFloat(ResetVisualState.Confirmed, 300, true), v.FloatFrame);
        var special = v.FloatFrame; v.SetResetState(ResetVisualState.None); Assert.Equal(special, v.FloatFrame);
        ms = 3340; Frame(v); var reversing = v.FloatFrame;
        v.SetResetState(ResetVisualState.Failed); Assert.Equal(reversing, v.FloatFrame);
        ms = 3640; Frame(v); Assert.Equal(CompanionBallVisual.SampleResetFloat(ResetVisualState.Failed, 300, true), v.FloatFrame);
        v.SetState("35", false); Assert.Equal(CompanionBallVisual.ResetFloatFrame.Rest, v.FloatFrame);
        var count = v.FrameCount; ms += 1000; Frame(v); Assert.Equal(count, v.FrameCount);
    });

    [Fact]
    public void All_32_poses_with_typing_and_peak_float_keep_head_inside_fixed_default_canvas() => Sta(() =>
    {
        var bodyBrush = (Brush)typeof(CompanionBallVisual).GetField("Body", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        foreach (var id in CompanionEmotionDirector.EmotionIds)
        foreach (var size in new[] { 52d, 56d })
        {
            var ms = 0d; using var v = new CompanionBallVisual(() => ms); v.SetState(id, true); v.SetTypingInteraction(true);
            v.Input(new CompanionInputEvent(CompanionInputKind.Key, 30, true));
            v.Input(new CompanionInputEvent(CompanionInputKind.MouseButton, 0, true, CompanionMouseButton.Left));
            foreach (var phase in new[] { 0, 1500, 3000, 4500 })
            {
                ms = phase; Frame(v); var drawing = Record(v, size);
                Check(drawing, Matrix.Identity);
                Assert.Equal(new Size(size, size), v.RenderSize); Assert.True(v.RenderTransform.Value.IsIdentity);
                Assert.False(v.IsHitTestVisible); Assert.Equal(id, v.Emotion);
            }
            void Check(Drawing drawing, Matrix parent)
            {
                if (drawing is GeometryDrawing g && ReferenceEquals(g.Brush, bodyBrush))
                    Assert.True(new Rect(0, 0, size, size).Contains(Rect.Transform(g.Bounds, parent)), $"Typing+float clipped: {id}/{size}/{ms}");
                if (drawing is DrawingGroup group)
                {
                    var transform = group.Transform?.Value ?? Matrix.Identity; transform.Append(parent);
                    foreach (var child in group.Children) Check(child, transform);
                }
            }
        }
    });

    [Fact]
    public void Export_ordinary_float_and_reflection_only_when_explicitly_requested() => Sta(() =>
    {
        var dir = Environment.GetEnvironmentVariable("CQT_RGB_FLOAT_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;
        var ffmpeg = Environment.GetEnvironmentVariable("CQT_PREVIEW_FFMPEG"); Assert.True(File.Exists(ffmpeg));
        Directory.CreateDirectory(dir); var target = Path.Combine(dir, "diandao-rgb-float-reflection-preview.mp4");
        Assert.False(File.Exists(target), "Frozen preview must not be overwritten.");
        const int width = 1280, height = 720, fps = 30, frames = 780;
        var ms = 0d; var sizes = new[] { 52d, 56d, 56d };
        var visuals = sizes.Select(_ => new CompanionBallVisual(() => ms)).ToArray();
        var start = new ProcessStartInfo(ffmpeg!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-n", "-f", "rawvideo", "-pixel_format", "bgra", "-video_size", "1280x720", "-framerate", "30", "-i", "pipe:0", "-an", "-c:v", "libx264", "-preset", "fast", "-crf", "18", "-pix_fmt", "yuv420p", "-movflags", "+faststart", target }) start.ArgumentList.Add(arg);
        using var encoder = Process.Start(start)!; var errors = encoder.StandardError.ReadToEndAsync();
        var canvas = new DrawingVisual(); var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); var pixels = new byte[width * height * 4];
        var background = new SolidColorBrush(Color.FromRgb(23, 25, 28)); background.Freeze();
        var guide = new Pen(Brushes.Gray, 1); guide.Freeze();
        var proofs = new HashSet<int> { 0, 45, 90, 180, 270, 360, 450, 540, 630, 720, 750 };
        try
        {
            for (var frame = 0; frame < frames; frame++)
            {
                var reduced = frame >= 720; ms = Math.Min(frame / (double)fps, 24) * 1000;
                for (var i = 0; i < visuals.Length; i++) { visuals[i].SetState("35", i < 2 && !reduced); Frame(visuals[i]); }
                using (var dc = canvas.RenderOpen())
                {
                    dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
                    Text(dc, "点岛 · 普通彩色呼吸 / 上下漂浮 / 球底柔光", 27, 40, 26);
                    Text(dc, "未安装候选 · 合成演示 · 实际 WPF 控件绘制 / 非现场录屏", 15, 42, 75, Brushes.Silver);
                    Text(dc, reduced ? "24–26 秒：减少动态，位置与光效保持静止" : "6 秒轻起落 · RGB 36 秒缓变 · 反光靠近更亮、升高渐淡", 18, 42, 115);
                    for (var i = 0; i < visuals.Length; i++)
                    {
                        var x = 48 + i * 410;
                        Text(dc, i == 2 ? "静止对照（减少动态）" : $"默认 {sizes[i]:0} DIP", 18, x + 50, 167);
                        var drawing = Record(visuals[i], sizes[i]);
                        dc.PushTransform(new TranslateTransform(x + 40, 207)); dc.PushTransform(new ScaleTransform(4, 4)); dc.DrawDrawing(drawing); dc.Pop(); dc.Pop();
                        var baseY = 207 + (sizes[i] / 2 + (244 - 114.2705) * sizes[i] * .64 / 259) * 4;
                        dc.DrawLine(guide, new Point(x + 22, baseY), new Point(x + 40, baseY));
                        dc.DrawLine(guide, new Point(x + 40 + sizes[i] * 4, baseY), new Point(x + 58 + sizes[i] * 4, baseY));
                        Text(dc, "4× 放大 · 灰色短线为固定底座参考", 12, x + 12, 449, Brushes.Silver);
                        dc.PushTransform(new TranslateTransform(x + 117, 490)); dc.DrawDrawing(drawing); dc.Pop();
                        Text(dc, $"原尺寸 {sizes[i]:0} DIP（未放大）", 13, x + 63, 560);
                        Text(dc, $"球体新增位移 {visuals[i].FloatFrame.BodyY * sizes[i] * .64 / 259:F2} DIP", 13, x + 54, 593);
                    }
                    Text(dc, "仅底部弧面接受同色柔光，脸与主体仍白色；实体环、布局与点击区域固定。", 14, 42, 645, Brushes.Silver);
                    Text(dc, "保留 32 种表情与既有特殊态 · 无 HWND / 真实账号 / 鼠标操作 · 当前安装未变", 13, 42, 677, Brushes.Silver);
                }
                bitmap.Clear(); bitmap.Render(canvas); bitmap.CopyPixels(pixels, width * 4, 0); encoder.StandardInput.BaseStream.Write(pixels);
                if (proofs.Contains(frame)) { var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(dir, $"proof-{frame:D4}.png")); png.Save(stream); }
            }
            Assert.All(visuals, v => { Assert.False(v.IsLoaded); Assert.False(v.AnimationRunning); Assert.Null(PresentationSource.FromVisual(v)); });
            using (var dc = canvas.RenderOpen())
            {
                dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
                Text(dc, "底部光方向核验 · 合成姿态 / 实际 WPF 渲染", 25, 40, 36);
                for (var i = 0; i < 4; i++)
                {
                    using var v = new CompanionBallVisual(() => 0); v.SetState("35", false);
                    var engine = (CompanionPoseEngine)typeof(CompanionBallVisual).GetField("engine", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(v)!;
                    var degrees = new[] { 0, 35, 90, 180 }[i]; engine.Pose[3] = degrees;
                    var shot = new DrawingVisual(); using (var s = shot.RenderOpen()) s.DrawDrawing(Record(v, 224));
                    var raster = new RenderTargetBitmap(224, 224, 96, 96, PixelFormats.Pbgra32); raster.Render(shot);
                    dc.DrawImage(raster, new Rect(40 + i * 310, 180, 224, 224));
                    Text(dc, $"旋转 {degrees}° · 光始终来自下方", 15, 40 + i * 310, 440);
                }
                Text(dc, "反光以世界方向定位，原Head轮廓裁剪；眼睛在反光后绘制。没有改变表情数据或输入几何。", 15, 40, 560, Brushes.Silver);
            }
            bitmap.Clear(); bitmap.Render(canvas);
            var tiltPng = new PngBitmapEncoder(); tiltPng.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(dir, "tilt-reflection-check.png"))) tiltPng.Save(stream);
        }
        finally { foreach (var v in visuals) v.Dispose(); encoder.StandardInput.Close(); }
        Assert.True(encoder.WaitForExit(30000)); Assert.Equal(0, encoder.ExitCode); Assert.True(string.IsNullOrWhiteSpace(errors.GetAwaiter().GetResult()));
    }, 180);

    private static void Frame(CompanionBallVisual v) => FrameMethod.Invoke(v, [null, EventArgs.Empty]);
    private static DrawingGroup Record(CompanionBallVisual v, double size)
    {
        v.Measure(new Size(size, size)); v.Arrange(new Rect(0, 0, size, size));
        var drawing = new DrawingGroup(); using var dc = drawing.Open(); RenderMethod.Invoke(v, [dc]); return drawing;
    }
    private static void Text(DrawingContext dc, string text, double size, double x, double y, Brush? brush = null) => dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, brush ?? Brushes.WhiteSmoke, 1), new Point(x, y));
    private static void Sta(Action action, int seconds = 30)
    {
        Exception? failure = null; var thread = new Thread(() => { try { action(); } catch (Exception e) { failure = e; } finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(seconds))); if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
