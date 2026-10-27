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
using Xunit.Abstractions;

namespace CodexQuotaTaskbar.Host.Tests.UI;

// Separate opt-in RGB export; never overwrite the approved floating/blue-ring media.
[Collection("Native UI isolation")]
public sealed class CompanionRgbRingTests(ITestOutputHelper output)
{
    private static readonly MethodInfo FrameMethod = typeof(CompanionBallVisual).GetMethod("OnFrame", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo RenderMethod = typeof(CompanionBallVisual).GetMethod("OnRender", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Fact]
    public void Rgb_is_continuous_periodic_soft_reduced_static_and_pure_sampling_allocates_nothing()
    {
        var previous = CompanionBallVisual.SampleNormalRing(0, true);
        var colors = new HashSet<Color>();
        for (var ms = 1; ms <= 36001; ms++)
        {
            var frame = CompanionBallVisual.SampleNormalRing(ms, true);
            Assert.InRange(Math.Abs(frame.Color.R - previous.Color.R), 0, 1);
            Assert.InRange(Math.Abs(frame.Color.G - previous.Color.G), 0, 1);
            Assert.InRange(Math.Abs(frame.Color.B - previous.Color.B), 0, 1);
            Assert.InRange(Math.Abs(frame.Opacity - previous.Opacity), 0, .0001);
            Assert.InRange(frame.Opacity, .679999, .860001);
            Assert.Equal(CompanionBallVisual.SampleNormalRing(0, false), CompanionBallVisual.SampleNormalRing(ms, false));
            colors.Add(frame.Color); previous = frame;
        }
        Assert.True(colors.Count > 500);
        Assert.Equal(CompanionBallVisual.SampleNormalRing(0, true), CompanionBallVisual.SampleNormalRing(36000, true));
        var allocated = GC.GetAllocatedBytesForCurrentThread(); var total = 0d;
        for (var i = 0; i < 10000; i++) total += CompanionBallVisual.SampleNormalRing(i, true).Opacity;
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocated); Assert.True(total > 0);
    }

    [Fact]
    public void Checking_never_overrides_normal_or_gold_and_green_red_priority_and_exact_hour_survive()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        foreach (var seconds in new[] { 0, .299, .3, 2, 60, 3600 })
        foreach (var upcoming in new[] { false, true })
        {
            var query = new ResetQueryState(1, ResetQueryPhase.Checking, now);
            Assert.Equal(upcoming ? ResetVisualState.Upcoming : ResetVisualState.None, query.Select(upcoming, null, now.AddSeconds(seconds)));
        }
        foreach (var phase in Enum.GetValues<ResetQueryPhase>())
        {
            var query = new ResetQueryState(1, phase, now);
            Assert.Equal(ResetVisualState.Confirmed, query.Select(true, now.AddHours(1), now.AddHours(1).AddTicks(-1)));
            Assert.Equal(phase == ResetQueryPhase.Failed ? ResetVisualState.Failed : ResetVisualState.Upcoming,
                query.Select(true, now.AddHours(1), now.AddHours(1)));
        }
    }

    [Fact]
    public void Repeated_states_do_not_restart_rgb_float_and_specials_return_to_current_phase() => Sta(() =>
    {
        var ms = 0d; using var visual = new CompanionBallVisual(() => ms);
        visual.SetState("35", true);
        var ring = visual.NormalRingDrawing;
        for (var i = 0; i <= 20; i++)
        {
            ms = i * 2000; visual.SetResetState(i % 2 == 0 ? ResetVisualState.None : ResetVisualState.Checking);
            visual.SetState("35", true); Frame(visual);
            Assert.Equal(ResetVisualState.None, visual.ResetState);
            Assert.Equal(CompanionBallVisual.SampleNormalRing(ms, true), visual.NormalFrame);
            Assert.Equal(CompanionBallVisual.SampleResetFloat(ResetVisualState.None, ms, true), visual.FloatFrame);
            Assert.Same(ring, visual.NormalRingDrawing);
        }
        foreach (var special in new[] { ResetVisualState.Failed, ResetVisualState.Upcoming, ResetVisualState.Confirmed })
        {
            visual.SetResetState(special); ms += 1600; Frame(visual);
            Assert.Equal(-12, visual.FloatFrame.BodyY);
            ms += 1600; visual.SetResetState(ResetVisualState.None); ms += 300; Frame(visual);
            Assert.Equal(CompanionBallVisual.SampleResetFloat(ResetVisualState.None, ms, true), visual.FloatFrame);
            Assert.Equal(CompanionBallVisual.SampleNormalRing(ms, true), visual.NormalFrame);
        }
        visual.SetState("35", false); var reduced = visual.NormalFrame; var count = visual.FrameCount;
        ms += 9000; Frame(visual);
        Assert.Equal(reduced, visual.NormalFrame); Assert.Equal(count, visual.FrameCount);
        visual.SetState("35", true); visual.Visibility = Visibility.Hidden;
        var hidden = visual.NormalFrame; count = visual.FrameCount; ms += 9000; Frame(visual);
        Assert.Equal(hidden, visual.NormalFrame); Assert.Equal(count, visual.FrameCount);
        visual.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); Assert.False(visual.AnimationRunning);
        visual.Dispose(); ms += 9000; Frame(visual); Assert.Equal(count, visual.FrameCount);
        Assert.Null(PresentationSource.FromVisual(visual));
    });

    [Fact]
    public void Rgb_reuses_every_gradient_stop_and_frozen_geometry_with_bounded_update_allocation() => Sta(() =>
    {
        var ms = 0d; using var visual = new CompanionBallVisual(() => ms);
        var drawing = (DrawingGroup)visual.NormalRingDrawing;
        var layers = drawing.Children.Select(d => d is DrawingGroup g ? (GeometryDrawing)g.Children[0] : (GeometryDrawing)d).ToArray();
        var geometries = layers.Select(d => d.Geometry).ToArray();
        var brushes = layers.Select(d => d.Brush).ToArray();
        var spectra = brushes.OfType<DrawingBrush>().Select(b => b.Drawing).ToArray();
        Assert.Equal(4, spectra.Length);
        Assert.All(spectra, s => { Assert.True(s.IsFrozen); Assert.Same(spectra[0], s); });
        Assert.All(geometries, g => Assert.True(g.IsFrozen));
        var update = typeof(CompanionBallVisual).GetMethod("UpdateNormalRing", BindingFlags.NonPublic | BindingFlags.Instance)!
            .CreateDelegate<Action>(visual);
        for (var i = 0; i < 1000; i++) { ms += 33; update(); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) { ms += 33; update(); }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"RGB brush update: {allocated} bytes / 10000 samples ({allocated / 10000d:F2} bytes/sample); no new drawing/gradient/geometry.");
        Assert.InRange(allocated, 0, 8192L * 10000);
        Assert.Same(drawing, visual.NormalRingDrawing);
        for (var i = 0; i < geometries.Length; i++)
        {
            Assert.Same(geometries[i], layers[i].Geometry);
            Assert.Same(brushes[i], layers[i].Brush);
        }
        Assert.True(spectra.Zip(brushes.OfType<DrawingBrush>()).All(pair => ReferenceEquals(pair.First, pair.Second.Drawing)));
    });

    [Fact]
    public void Export_rgb_comparison_only_when_explicitly_requested() => Sta(() =>
    {
        var directory = Environment.GetEnvironmentVariable("CQT_RGB_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        var ffmpeg = Environment.GetEnvironmentVariable("CQT_PREVIEW_FFMPEG"); Assert.True(File.Exists(ffmpeg));
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "diandao-rgb-base-preview.mp4");
        Assert.False(File.Exists(target), "Existing previews must not be overwritten.");
        const int width = 1280, height = 720, fps = 30, frames = 1440;
        var ms = 0d; var origin = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var visuals = Enumerable.Range(0, 5).Select(_ => new CompanionBallVisual(() => ms)).ToArray();
        var start = new ProcessStartInfo(ffmpeg!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-n", "-f", "rawvideo", "-pixel_format", "bgra", "-video_size", "1280x720",
            "-framerate", "30", "-i", "pipe:0", "-an", "-c:v", "libx264", "-preset", "fast", "-crf", "18", "-pix_fmt", "yuv420p", "-movflags", "+faststart", target }) start.ArgumentList.Add(arg);
        using var encoder = Process.Start(start)!; var errors = encoder.StandardError.ReadToEndAsync();
        var canvas = new DrawingVisual(); var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var pixels = new byte[width * height * 4];
        var names = new[] { "普通 · RGB 呼吸", "查询 · 不显示蓝灯", "异常 · 红色", "预告 · 金黄", "恢复 · 绿色" };
        var background = new SolidColorBrush(Color.FromRgb(23, 25, 28)); background.Freeze();
        var proofs = new HashSet<int> { 0, 180, 360, 540, 720, 810, 870, 990, 1170, 1350, 1439 };
        try
        {
            for (var frame = 0; frame < frames; frame++)
            {
                var seconds = frame / (double)fps;
                var reduced = seconds is >= 26 and < 30;
                ms = (seconds < 26 ? seconds : seconds < 30 ? 26 : seconds - 4) * 1000;
                var special = seconds is >= 8 and < 20 or >= 26 and < 42;
                var checking = seconds is >= 4 and < 44;
                var now = origin.AddSeconds(seconds);
                for (var i = 0; i < visuals.Length; i++)
                {
                    var query = new ResetQueryState(1, i == 2 && special ? ResetQueryPhase.Failed : checking ? ResetQueryPhase.Checking : ResetQueryPhase.Idle, origin);
                    var state = i == 0 ? ResetVisualState.None : query.Select(i == 3 && special, i == 4 && special ? now.AddHours(1) : null, now);
                    visuals[i].SetResetState(state); visuals[i].SetState("35", !reduced); Frame(visuals[i]);
                }
                using (var dc = canvas.RenderOpen())
                {
                    dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
                    Text(dc, "点岛 · RGB 呼吸底座", 28, 42, 30);
                    Text(dc, "未安装候选 / 演示数据 / 实际 WPF 绘制 + 实际状态投影 / 无真实账号查询", 14, 44, 78, Brushes.Silver);
                    Text(dc, reduced ? "减少动态：固定柔和颜色，浮动与呼吸停止" : special ? "特殊状态覆盖普通 RGB；查询不覆盖金黄或绿色" : "普通态白球不新增浮动；底座缓慢变色，轻轻呼吸", 18, 44, 116);
                    for (var i = 0; i < visuals.Length; i++)
                    {
                        var x = 38 + i * 248; Text(dc, names[i], 17, x + 13, 175);
                        var drawing = Record(visuals[i]);
                        dc.PushTransform(new TranslateTransform(x + 8, 215)); dc.PushTransform(new ScaleTransform(3.5, 3.5));
                        dc.DrawDrawing(drawing); dc.Pop(); dc.Pop();
                        Text(dc, "3.5× 动效对照", 12, x + 50, 425, Brushes.Silver);
                        dc.PushTransform(new TranslateTransform(x + 78, 464)); dc.DrawDrawing(drawing); dc.Pop();
                        Text(dc, "56 DIP 原尺寸", 12, x + 51, 532, Brushes.Silver);
                        Text(dc, i == 1 && checking ? "请求进行中 · RGB不中断" : $"新增浮动 {visuals[i].FloatFrame.BodyY * 56 * .64 / 259:F2} DIP", 12, x + 20, 570);
                    }
                    Text(dc, "36 秒连续色相 / 6 秒柔和呼吸 / 实体底座与点击区域固定 / 白球本体、32 表情与键鼠动作不变", 13, 42, 630, Brushes.Silver);
                    Text(dc, "0–8s 普通 · 8–20s 特殊 · 20–26s 回归RGB · 26–30s 减少动态 · 30–42s 特殊 · 42–48s 回归RGB", 12, 42, 666, Brushes.Silver);
                }
                bitmap.Clear(); bitmap.Render(canvas); bitmap.CopyPixels(pixels, width * 4, 0); encoder.StandardInput.BaseStream.Write(pixels);
                if (proofs.Contains(frame))
                {
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(directory, $"proof-{frame:D4}.png")); png.Save(stream);
                }
            }
            Assert.All(visuals, v => { Assert.False(v.AnimationRunning); Assert.False(v.IsLoaded); Assert.Null(PresentationSource.FromVisual(v)); });
        }
        finally { foreach (var visual in visuals) visual.Dispose(); encoder.StandardInput.Close(); }
        Assert.True(encoder.WaitForExit(30000)); Assert.Equal(0, encoder.ExitCode); Assert.True(string.IsNullOrWhiteSpace(errors.GetAwaiter().GetResult()));
    }, 180);

    private static void Frame(CompanionBallVisual visual) => FrameMethod.Invoke(visual, [null, EventArgs.Empty]);
    private static DrawingGroup Record(CompanionBallVisual visual)
    {
        visual.Measure(new Size(56, 56)); visual.Arrange(new Rect(0, 0, 56, 56));
        var drawing = new DrawingGroup(); using var dc = drawing.Open(); RenderMethod.Invoke(visual, [dc]); return drawing;
    }
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
