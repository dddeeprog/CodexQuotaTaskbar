using System.Globalization;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Host.UI;
using CodexQuotaTaskbar.Host.Resets;

namespace CodexQuotaTaskbar.Host.Tests.UI;

// No windows are shown or assigned native handles. Drawings are recorded into
// an in-memory DrawingGroup; actual owner/footer transitions are proved in the
// separate alpha-zero native proof, not by a simulated clock here.
[Collection("Native UI isolation")]
public sealed class ResetNoticeBallColorTests
{
    [Fact]
    public void Query_notifications_preserve_schedule_context_and_ignore_old_revisions() => Sta(() =>
    {
        var window = new QuotaCapsuleWindow("synthetic-query-revision");
        window.SetPointerObservationAllowed(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var snapshot = new ResetScheduleSnapshot(new("synthetic-notice", ResetKind.Regular, now.AddMinutes(-1),
                now.AddHours(1), CodexResetFeedClient.SiteUri), now, false, null);
            window.ApplyResetSchedule(snapshot, now);
            window.ApplyResetQueryState(new(1, ResetQueryPhase.Checking, now));
            Assert.Same(snapshot, Field<ResetScheduleSnapshot>(window, "resetSchedule"));
            window.ApplyResetQueryState(new(2, ResetQueryPhase.Failed));
            window.ApplyResetQueryState(new(1, ResetQueryPhase.Checking, now));
            Assert.Equal(ResetQueryPhase.Failed, Field<ResetQueryState>(window, "resetQueryState").Phase);
            Assert.Same(snapshot, Field<ResetScheduleSnapshot>(window, "resetSchedule"));
            window.ApplyResetQueryState(new(3, ResetQueryPhase.Idle));
            Assert.Equal(ResetQueryPhase.Idle, Field<ResetQueryState>(window, "resetQueryState").Phase);
            Assert.Equal(IntPtr.Zero, new System.Windows.Interop.WindowInteropHelper(window).Handle);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Query_rings_preserve_all_32_white_poses_and_reuse_frozen_surfaces() => Sta(() =>
    {
        var definitions = CompanionCatalog.Shared.Value.Emotions.ToArray();
        Assert.Equal(32, definitions.Length);
        foreach (var definition in definitions)
        {
            using var visual = new CompanionBallVisual(() => 700);
            visual.SetState(definition.Id, false);
            var normal = RecordDrawing(visual);
            foreach (var state in new[] { ResetVisualState.Checking, ResetVisualState.Failed })
            {
                visual.SetResetState(state);
                var rendered = RecordDrawing(visual);
                var ring = state == ResetVisualState.Checking ? visual.NormalRingDrawing : QueryRing(state);
                Assert.Equal(state != ResetVisualState.Checking, ring.IsFrozen); Assert.True(ContainsDrawing(rendered, ring));
                Assert.Equal(Pixels(Raster(WithoutReminderLayers(normal)!, 192)), Pixels(Raster(WithoutReminderLayers(rendered)!, 192)));
                if (state == ResetVisualState.Failed) Assert.Same(ring, QueryRing(state));
                else Assert.Equal(ResetVisualState.None, visual.ResetState);
                AssertNoNativeClock(visual);
            }
        }
    });

    [Fact]
    public void Query_motion_is_slow_bounded_reduced_motion_static_and_allocation_free()
    {
        Assert.Equal(0, CompanionBallVisual.QueryRingMotion(ResetVisualState.Checking, 0, true).Angle);
        Assert.Equal(0, CompanionBallVisual.QueryRingMotion(ResetVisualState.Checking, 3000, true).Angle);
        Assert.Equal(1, CompanionBallVisual.QueryRingMotion(ResetVisualState.Failed, 0, true).Opacity);
        Assert.InRange(CompanionBallVisual.QueryRingMotion(ResetVisualState.Failed, 1400, true).Opacity, .639, .641);
        foreach (var state in new[] { ResetVisualState.Checking, ResetVisualState.Failed })
            Assert.Equal((0d, 1d), CompanionBallVisual.QueryRingMotion(state, 1400, false));
        var sum = 0d;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) sum += CompanionBallVisual.QueryRingMotion(ResetVisualState.Failed, i, true).Opacity;
        var delta = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal(0, delta); Assert.True(sum > 0);
    }

    // Opt-in offline export: actual pose engine/OnFrame/OnRender and actual quota
    // XAML, no App.Startup, Show, account client, WebView, input hook or real data.
    [Fact]
    public void Export_isolated_motion_video_when_explicitly_requested() => Sta(() =>
    {
        var directory = Environment.GetEnvironmentVariable("CQT_MOTION_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        var ffmpeg = Environment.GetEnvironmentVariable("CQT_PREVIEW_FFMPEG");
        Assert.True(File.Exists(ffmpeg), "An explicit existing encoder is required.");
        Directory.CreateDirectory(directory);
        const int width = 960, height = 540, fps = 30, frames = 510;
        var target = Path.Combine(directory, "diandao-recovery-preview.mp4");
        var start = new ProcessStartInfo(ffmpeg!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pixel_format", "bgra",
            "-video_size", "960x540", "-framerate", "30", "-i", "pipe:0", "-an", "-c:v", "libx264", "-preset", "medium",
            "-crf", "19", "-pix_fmt", "yuv420p", "-movflags", "+faststart", target }) start.ArgumentList.Add(argument);
        using var encoder = Process.Start(start)!;
        var encoderErrors = encoder.StandardError.ReadToEndAsync();
        var clockMs = 0d;
        using var ball = new CompanionBallVisual(() => clockMs);
        var onFrame = typeof(CompanionBallVisual).GetMethod("OnFrame", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var window = new QuotaCapsuleWindow("synthetic-offline-preview");
        var root = (FrameworkElement)window.Content;
        // Detach only this never-shown fixture's content for offscreen WPF layout.
        window.Content = null;
        var tracker = new QuotaResetTracker(); // No store: cannot touch real reminder state.
        window.SetResetTracker(tracker);
        var now = DateTimeOffset.UtcNow;
        var scope = QuotaResetTracker.ScopeForSource("SYNTHETIC-ANIMATION-FIXTURE")!;
        var notice = new ResetSchedule("synthetic-preview-notice", ResetKind.Regular, now.AddMinutes(-5), now.AddHours(3), CodexResetFeedClient.SiteUri);
        QuotaSnapshot Sample(DateTimeOffset time, double remaining) => QuotaSnapshot.Available([
            new(QuotaWindowKind.Primary, remaining, 300, now.AddHours(5)),
            new(QuotaWindowKind.Secondary, 40, 10080, now.AddDays(7))], time, "演示计划");
        tracker.Observe(Sample(now, 20), scope, now);
        window.Apply(Sample(now, 20));
        DateTimeOffset? until = null;
        var sheet = new DrawingVisual();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var pixels = new byte[width * height * 4];
        var font = new Typeface("Microsoft YaHei UI");
        var background = new SolidColorBrush(Color.FromRgb(24, 26, 29)); background.Freeze();
        var emotions = CompanionCatalog.Shared.Value.Emotions.Select(e => e.Id).ToHashSet();
        var emotionCycle = new[] { "35", "01", "18", "30", "36", "35" }.Where(emotions.Contains).ToArray();
        var lastEmotion = -1;
        var proofFrames = new HashSet<int> { 0, 30, 90, 150, 209, 210, 240, 389, 390, 450, 509 };
        try
        {
            for (var frame = 0; frame < frames; frame++)
            {
                var seconds = frame / (double)fps;
                clockMs = seconds * 1000;
                var simulatedNow = now.AddSeconds(seconds);
                if (frame == 90)
                {
                    tracker.SetNotice(notice);
                    window.ApplyResetSchedule(new(notice, simulatedNow, false, null), simulatedNow, TimeZoneInfo.Utc);
                }
                if (frame == 210)
                {
                    var recovered = Sample(simulatedNow, 35);
                    until = tracker.Observe(recovered, scope, simulatedNow);
                    Assert.Equal(simulatedNow.AddHours(1), until);
                    window.Apply(recovered);
                    window.ApplyConfirmedReset(until);
                }
                if (frame >= 390) simulatedNow = until!.Value.AddSeconds(seconds - 13);
                window.RefreshResetSchedule(simulatedNow, TimeZoneInfo.Utc);
                var rowVisible = ((Border)window.FindName("ResetScheduleRow")).IsHitTestVisible;
                if (frame is >= 90 and < 210) Assert.True(rowVisible);
                if (frame >= 210) { Assert.False(rowVisible); Assert.True(tracker.IsNoticeConsumed(notice)); }
                ball.SetResetState(QuotaResetTracker.Select(rowVisible, until, simulatedNow));
                var emotion = Math.Min((int)(seconds / 3), emotionCycle.Length - 1);
                if (lastEmotion != emotion) { ball.SetState(emotionCycle[emotion], true); lastEmotion = emotion; }
                ball.Gaze(Math.Sin(seconds * .85) * .7, Math.Cos(seconds * .6) * .35);
                onFrame.Invoke(ball, [null, EventArgs.Empty]); // Actual engine sampling, not PNG playback.
                var drawing = RecordDrawing(ball);
                var rootHeight = window.DesiredHeight;
                root.Measure(new Size(280, rootHeight)); root.Arrange(new Rect(0, 0, 280, rootHeight)); root.UpdateLayout();
                var quota = new RenderTargetBitmap(280, (int)Math.Ceiling(rootHeight), 96, 96, PixelFormats.Pbgra32);
                quota.Render(root);
                var phase = frame < 90 ? "普通白球" : frame < 210 ? "待重置 · 金黄色底座" : frame < 390 ? "可用额度回升 · 绿色底座" : "演示快进至 1 小时后";
                var caption = frame < 90 ? "实际白球动画引擎：表情与视线持续运动"
                    : frame < 210 ? "旧重置提示显示中 · 公共预告不是个人重置确认"
                    : frame < 390 ? "合成额度 20% → 35% · 旧重置提示已隐藏"
                    : "绿色结束 · 旧提示保持隐藏，不重新弹出";
                using (var dc = sheet.RenderOpen())
                {
                    dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
                    void Text(string text, double size, double x, double y, Brush brush) => dc.DrawText(
                        new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, font, size, brush, 1), new Point(x, y));
                    Text("点岛  /  " + phase, 25, 32, 25, Brushes.WhiteSmoke);
                    Text("0.1.13-recovery-source-preview   ·   演示数据 / 隔离 WPF 渲染", 12, 34, 66, Brushes.Silver);
                    dc.PushTransform(new TranslateTransform(368, 90)); dc.PushTransform(new ScaleTransform(4, 4));
                    dc.DrawDrawing(drawing); dc.Pop(); dc.Pop();
                    Text(caption, 16, 240, 294, Brushes.WhiteSmoke);
                    dc.DrawImage(quota, new Rect(256, 326, 448, rootHeight * 1.6));
                    Text("绿态实际持续 1h；此片压缩时间。未展示完整点击/面板切换，未使用真实账号。", 12, 32, 513, Brushes.Silver);
                }
                bitmap.Clear(); bitmap.Render(sheet);
                bitmap.CopyPixels(pixels, width * 4, 0);
                encoder.StandardInput.BaseStream.Write(pixels);
                if (proofFrames.Contains(frame)) SavePng(Path.Combine(directory, $"proof-{frame:D3}.png"), bitmap);
            }
            Assert.True(ball.FrameCount >= 450);
            AssertNoNativeClock(ball);
            Assert.Equal(IntPtr.Zero, new System.Windows.Interop.WindowInteropHelper(window).Handle);
        }
        finally
        {
            window.Close();
            encoder.StandardInput.Close(); // Only this owned encoder: EOF, never kill.
        }
        Assert.True(encoder.WaitForExit(30000), "Owned video encoder did not finish after EOF.");
        Assert.Equal(0, encoder.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(encoderErrors.GetAwaiter().GetResult()));
    }, 120);

    [Fact]
    public void Export_offline_preview_when_explicitly_requested() => Sta(() =>
    {
        var directory = Environment.GetEnvironmentVariable("CQT_RESET_RING_PROOF_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        foreach (var definition in CompanionCatalog.Shared.Value.Emotions)
        {
            using var visual = new CompanionBallVisual(() => 0);
            visual.SetState(definition.Id, false);
            SavePng(Path.Combine(directory, $"normal-pose-{definition.Id}-96dpi.png"), Raster(RecordDrawing(visual), 96));
        }
        using var preview = new CompanionBallVisual(() => 0);
        preview.SetState("35", false);
        var drawings = new List<Drawing>();
        foreach (var (name, state) in new[] { ("normal", ResetVisualState.None), ("gold", ResetVisualState.Upcoming), ("green", ResetVisualState.Confirmed) })
        {
            preview.SetResetState(state);
            var drawing = RecordDrawing(preview);
            drawings.Add(drawing);
            foreach (var dpi in new[] { 96, 144, 192, 384 })
                SavePng(Path.Combine(directory, $"{name}-56dip-{dpi}dpi.png"), Raster(drawing, dpi));
        }
        foreach (var light in new[] { false, true })
        {
            var sheet = new DrawingVisual();
            using (var dc = sheet.RenderOpen())
            {
                dc.DrawRectangle(light ? Brushes.WhiteSmoke : new SolidColorBrush(Color.FromRgb(27, 29, 31)), null, new Rect(0, 0, 672, 360));
                var titles = new[] { "普通 · 无底座", "预告 · 金色", "额度回升 · 绿色1小时" };
                for (var i = 0; i < 3; i++)
                {
                    dc.DrawText(new FormattedText(titles[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Microsoft YaHei UI"), 12, light ? Brushes.DimGray : Brushes.LightGray, 1), new Point(i * 224 + 16, 12));
                    dc.PushTransform(new TranslateTransform(i * 224 + 84, 36)); dc.DrawDrawing(drawings[i]); dc.Pop();
                    dc.PushTransform(new TranslateTransform(i * 224, 112)); dc.PushTransform(new ScaleTransform(4, 4));
                    dc.DrawDrawing(drawings[i]); dc.Pop(); dc.Pop();
                }
                dc.DrawText(new FormattedText("实际WPF离线渲染 · 上排56 DIP / 下排4倍 · 连续额度回升候选，尚未安装",
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 10,
                    light ? Brushes.DimGray : Brushes.LightGray, 1), new Point(16, 342));
            }
            var bitmap = new RenderTargetBitmap(672, 360, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(sheet);
            SavePng(Path.Combine(directory, light ? "status-light-96dpi.png" : "status-dark-96dpi.png"), bitmap);
        }
        AssertNoNativeClock(preview);
    });

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
    public void Ring_requires_the_valid_allowed_visible_row_real_height_and_visible_owner(
        bool scheduleVisible, bool presentationAllowed, bool rowVisible, double height, bool ownerVisible, bool expected) =>
        Sta(() =>
        {
            var active = QuotaCapsuleWindow.ShouldUseResetNoticeColor(
                scheduleVisible, presentationAllowed, rowVisible, height, ownerVisible);
            Assert.Equal(expected, active);
            using var visual = new CompanionBallVisual(() => 0);
            visual.SetResetNoticeActive(active);
            Assert.Equal(expected, ContainsDrawing(RecordDrawing(visual), GoldRing));
            Assert.Equal(expected, ContainsDrawing(RecordDrawing(visual), GoldReflection));
            AssertNoNativeClock(visual);
        });

    [Fact]
    public void Only_the_base_and_bounce_light_are_added_without_changing_body_pose_eyes_or_gaze() => Sta(() =>
    {
        using var visual = new CompanionBallVisual(() => 0);
        visual.SetState("01", animate: true);
        visual.Gaze(.4, -.2); // Synthetic direction; no pointer is sampled.
        var pose = Field<CompanionPoseEngine>(visual, "engine").Pose.ToArray();
        var state = (visual.Emotion, visual.EffectiveEmotion, visual.GazeSample, visual.TypingFrame);
        var whiteBrush = StaticBrush("Body");
        var white = RecordDrawing(visual);

        visual.SetResetState(ResetVisualState.Confirmed);
        var green = RecordDrawing(visual);

        Assert.True(visual.ResetNoticeActive);
        Assert.Equal(state, (visual.Emotion, visual.EffectiveEmotion, visual.GazeSample, visual.TypingFrame));
        Assert.Equal(pose, Field<CompanionPoseEngine>(visual, "engine").Pose);
        AssertOnlyReminderLayersAdded(white, green);
        Assert.True(whiteBrush.IsFrozen); Assert.True(Ring.IsFrozen);

        visual.SetResetNoticeActive(false);
        var restored = RecordDrawing(visual);
        Assert.False(visual.ResetNoticeActive);
        Assert.Same(whiteBrush, GeometryDrawings(WithoutReminderLayers(restored)!).First().Brush);
        Assert.Equal(GeometrySignature(white), GeometrySignature(restored));
        Assert.Equal(Pixels(Raster(white, 96)), Pixels(Raster(restored, 96)));
        Assert.Equal(state, (visual.Emotion, visual.EffectiveEmotion, visual.GazeSample, visual.TypingFrame));
        AssertNoNativeClock(visual);
    });

    [Fact]
    public void Reduced_sleeping_pose_adds_ring_immediately_without_changing_shape_or_starting_frames() => Sta(() =>
    {
        using var visual = new CompanionBallVisual(() => 0);
        visual.SetState("06", animate: false);
        var state = (visual.Emotion, visual.EffectiveEmotion, visual.GazeSample, visual.TypingFrame);
        var pose = Field<CompanionPoseEngine>(visual, "engine").Pose.ToArray();
        var white = RecordDrawing(visual);
        visual.SetResetState(ResetVisualState.Confirmed);
        Assert.True(visual.ResetNoticeActive);
        var green = RecordDrawing(visual);

        AssertOnlyReminderLayersAdded(white, green);
        Assert.Equal(state, (visual.Emotion, visual.EffectiveEmotion, visual.GazeSample, visual.TypingFrame));
        Assert.Equal(pose, Field<CompanionPoseEngine>(visual, "engine").Pose);
        Assert.Equal(0, visual.FrameCount);
        visual.SetResetNoticeActive(false);
        Assert.Same(StaticBrush("Body"), GeometryDrawings(WithoutReminderLayers(RecordDrawing(visual))!).First().Brush);
        AssertNoNativeClock(visual);
    });

    [Fact]
    public void Control_hands_pending_ring_to_its_lazy_visual_preserves_badge_and_hit_contract() => Sta(() =>
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
        Assert.Equal(56, control.Width); Assert.Equal(56, control.Height);
        control.Measure(new Size(56, 56)); control.Arrange(new Rect(0, 0, 56, 56));
        // No HWND is created: check the hit-test contract, not native pointer delivery.
        var hitContract = (control.RenderSize, control.Background, control.IsHitTestVisible, visual.IsHitTestVisible);
        Assert.Same(Brushes.Transparent, control.Background);
        Assert.True(control.IsHitTestVisible);

        control.SetResetNoticeActive(false);
        Assert.False(control.ResetNoticeActive); Assert.False(visual.ResetNoticeActive);
        control.SetResetNoticeActive(true);
        Assert.True(visual.ResetNoticeActive);
        Assert.Equal(status, (number.Text, badge.Visibility, visual.Emotion, visual.EffectiveEmotion));
        Assert.Equal(hitContract, (control.RenderSize, control.Background, control.IsHitTestVisible, visual.IsHitTestVisible));
        Assert.False(visual.IsHitTestVisible);
        AssertNoNativeClock(visual);
        Assert.Null(PresentationSource.FromVisual(control));

        control.Dispose();
        Assert.False(control.ResetNoticeActive); Assert.False(visual.ResetNoticeActive);
        control.SetResetNoticeActive(true); visual.SetResetState(ResetVisualState.Confirmed);
        Assert.False(control.ResetNoticeActive); Assert.False(visual.ResetNoticeActive);
        using var recreated = new CompanionBallControl();
        recreated.SetPointerObservationAllowed(false);
        Assert.False(recreated.ResetNoticeActive);
        using var recreatedVisual = new CompanionBallVisual(() => 0);
        Assert.False(recreatedVisual.ResetNoticeActive);
        recreatedVisual.SetState("01", animate: false);
        Assert.Same(StaticBrush("Body"), GeometryDrawings(WithoutReminderLayers(RecordDrawing(recreatedVisual))!).First().Brush);
    });

    [Fact]
    public void All_32_poses_keep_their_original_pixels_and_state_when_ring_toggles() => Sta(() =>
    {
        var definitions = CompanionCatalog.Shared.Value.Emotions;
        Assert.Equal(32, definitions.Length);
        var baseline = Environment.GetEnvironmentVariable("CQT_RESET_RING_BASELINE_DIR");
        foreach (var definition in definitions)
        {
            using var visual = new CompanionBallVisual(() => 0);
            visual.SetState(definition.Id, animate: false);
            var normal = RecordDrawing(visual);
            if (!string.IsNullOrWhiteSpace(baseline))
            {
                // Compare encoded output directly: PNG unpremultiplication and decoding can round RGB by one.
                Assert.Equal(File.ReadAllBytes(Path.Combine(baseline, $"normal-pose-{definition.Id}-96dpi.png")), PngBytes(Raster(WithoutReminderLayers(normal)!, 96)));
            }
            var state = (visual.Emotion, visual.EffectiveEmotion, visual.GazeSample, visual.TypingFrame);
            foreach (var stateColor in new[] { ResetVisualState.Upcoming, ResetVisualState.Confirmed })
            {
                visual.SetResetState(stateColor);
                AssertOnlyReminderLayersAdded(normal, RecordDrawing(visual));
            }
            Assert.Equal(state, (visual.Emotion, visual.EffectiveEmotion, visual.GazeSample, visual.TypingFrame));
            visual.SetResetNoticeActive(false);
            Assert.Equal(Pixels(Raster(normal, 96)), Pixels(Raster(RecordDrawing(visual), 96)));
            AssertNoNativeClock(visual);
        }
    });

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void Thick_base_has_an_open_top_dark_center_perspective_and_cached_bounded_surfaces(int dpi) => Sta(() =>
    {
        Assert.True(Ring.IsFrozen);
        var surfaces = GeometryDrawings(Ring).ToArray();
        Assert.Equal(5, surfaces.Length); // Glow, soft center shadow, front side, inner wall, top annulus.
        Assert.All(surfaces, surface => Assert.True(surface.IsFrozen));
        Assert.False(surfaces[^1].Geometry.FillContains(new Point(114.2705, 244)));
        Assert.True(surfaces[^1].Geometry.FillContains(new Point(114.2705, 279)));
        Assert.True(surfaces[2].Geometry.FillContains(new Point(114.2705, 294)));
        Assert.True(surfaces[3].Geometry.FillContains(new Point(114.2705, 216)));
        Assert.InRange(surfaces[^1].Bounds.Height / surfaces[^1].Bounds.Width, .3, .4);
        var scale = 56 * .64 / 259;
        var transform = new Matrix(scale, 0, 0, scale, 28 - 114.2705 * scale, 28 - 114.2705 * scale);
        var drawing = new DrawingGroup { Transform = new MatrixTransform(transform) };
        drawing.Children.Add(Ring);
        Assert.True(new Rect(0, 0, 56, 56).Contains(drawing.Bounds));
        var bitmap = Raster(drawing, dpi);
        var pixels = Pixels(bitmap);
        var centerX = bitmap.PixelWidth / 2;
        var centerY = (int)Math.Round((28 + (244 - 114.2705) * scale) * dpi / 96.0);
        var centerOffset = (centerY * bitmap.PixelWidth + centerX) * 4;
        Assert.InRange(pixels[centerOffset + 3], 1, 254); // Translucent dark opening, not a solid green disk.
        Assert.InRange(pixels[centerOffset + 1], 1, 60);
        var greenPixels = 0;
        for (var y = 0; y < bitmap.PixelHeight; y++)
            for (var x = 0; x < bitmap.PixelWidth; x++)
            {
                var offset = (y * bitmap.PixelWidth + x) * 4;
                if (x == 0 || y == 0 || x == bitmap.PixelWidth - 1 || y == bitmap.PixelHeight - 1)
                    Assert.Equal(0, pixels[offset + 3]);
                if (pixels[offset + 3] > 100 && pixels[offset + 1] > pixels[offset + 2] && pixels[offset + 1] > pixels[offset])
                    greenPixels++;
            }
        Assert.True(greenPixels > 100);
        using var visual = new CompanionBallVisual(() => 0);
        var cached = Ring;
        var cachedReflection = Reflection;
        for (var index = 0; index < 100; index++)
        {
            visual.SetResetState(ResetVisualState.Confirmed);
            Assert.True(ContainsDrawing(RecordDrawing(visual), cached));
            Assert.True(ContainsDrawing(RecordDrawing(visual), cachedReflection));
            visual.SetResetNoticeActive(false);
        }
        Assert.Same(cached, Ring);
        Assert.Same(cachedReflection, Reflection);
        Assert.Equal(0, visual.FrameCount);
        AssertNoNativeClock(visual);
    });

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void Bounce_light_only_tints_the_lower_ball_weakly_and_keeps_the_upper_face_white(int dpi) => Sta(() =>
    {
        Assert.True(Reflection.IsFrozen);
        var reflection = Assert.IsType<GeometryDrawing>(Reflection);
        var gradient = Assert.IsType<RadialGradientBrush>(reflection.Brush);
        Assert.All(gradient.GradientStops, stop => Assert.InRange(stop.Color.A, 0, 60));
        using var visual = new CompanionBallVisual(() => 0);
        visual.SetState("35", false);
        var normal = Raster(RecordDrawing(visual), dpi);
        var normalPixels = Pixels(normal);
        visual.SetResetState(ResetVisualState.Confirmed);
        var reminderPixels = Pixels(Raster(RecordDrawing(visual), dpi));
        for (var y = 0; y < (int)(33 * dpi / 96.0); y++)
            for (var x = 0; x < normal.PixelWidth; x++)
            {
                var offset = (y * normal.PixelWidth + x) * 4;
                if (normalPixels[offset + 3] != 255) continue;
                Assert.Equal(normalPixels.AsSpan(offset, 4).ToArray(), reminderPixels.AsSpan(offset, 4).ToArray());
            }
        var bottom = ((int)(41 * dpi / 96.0) * normal.PixelWidth + normal.PixelWidth / 2) * 4;
        Assert.Equal(255, reminderPixels[bottom + 3]);
        Assert.InRange(reminderPixels[bottom + 1] - reminderPixels[bottom + 2], 5, 40);
        Assert.True(reminderPixels[bottom + 2] > 180 && reminderPixels[bottom] > 180);
        AssertNoNativeClock(visual);
    });

    internal static DrawingGroup RecordDrawing(CompanionBallVisual visual)
    {
        visual.Measure(new Size(56, 56)); visual.Arrange(new Rect(0, 0, 56, 56));
        var drawing = new DrawingGroup();
        using var context = drawing.Open();
        try { typeof(CompanionBallVisual).GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(visual, [context]); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
        return drawing;
    }

    private static RenderTargetBitmap Raster(Drawing drawing, int dpi)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawDrawing(drawing);
        var pixels = (int)Math.Round(56 * dpi / 96.0);
        var bitmap = new RenderTargetBitmap(pixels, pixels, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static void SavePng(string path, BitmapSource bitmap)
        => File.WriteAllBytes(path, PngBytes(bitmap));

    private static byte[] PngBytes(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapSource LoadPng(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return decoder.Frames[0];
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static Drawing GoldRing => ((Lazy<Drawing>)typeof(CompanionBallVisual)
        .GetField("GoldResetRing", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Value;
    private static Drawing GoldReflection => ((Lazy<Drawing>)typeof(CompanionBallVisual)
        .GetField("GoldResetReflection", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Value;

    private static Drawing Ring => ((Lazy<Drawing>)typeof(CompanionBallVisual)
        .GetField("ResetRing", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Value;

    private static Drawing Reflection => ((Lazy<Drawing>)typeof(CompanionBallVisual)
        .GetField("ResetReflection", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Value;

    private static bool ContainsDrawing(Drawing drawing, Drawing target) => ReferenceEquals(drawing, target)
        || drawing is DrawingGroup group && group.Children.Any(child => ContainsDrawing(child, target));

    private static void AssertOnlyReminderLayersAdded(DrawingGroup before, DrawingGroup after)
    {
        // The normal RGB base is now always present; compare the unchanged ball
        // with both normal and special support layers removed.
        before = (DrawingGroup)WithoutReminderLayers(before)!;
        Assert.True(ContainsDrawing(after, Ring) || ContainsDrawing(after, GoldRing));
        Assert.True(ContainsDrawing(after, Reflection) || ContainsDrawing(after, GoldReflection));
        Assert.False(ContainsDrawing(before, Ring));
        Assert.False(ContainsDrawing(before, Reflection));
        var withoutReminder = WithoutReminderLayers(after)!;
        var oldGeometry = GeometryDrawings(before).ToArray();
        var newGeometry = GeometryDrawings(withoutReminder).ToArray();
        Assert.True(oldGeometry.Length > 1);
        Assert.Equal(oldGeometry.Length, newGeometry.Length);
        Assert.Same(StaticBrush("Body"), newGeometry[0].Brush);
        for (var index = 0; index < oldGeometry.Length; index++)
        {
            Assert.Equal(oldGeometry[index].Geometry.ToString(CultureInfo.InvariantCulture), newGeometry[index].Geometry.ToString(CultureInfo.InvariantCulture));
            Assert.Same(oldGeometry[index].Brush, newGeometry[index].Brush);
        }
        // Removing only the two shared additions recovers the original transforms and pixels.
        Assert.Equal(before.Bounds, withoutReminder.Bounds);
        Assert.Equal(Pixels(Raster(before, 192)), Pixels(Raster(withoutReminder, 192)));
    }

    private static Drawing? WithoutReminderLayers(Drawing drawing)
    {
        // Ordinary lower-arc bounce light is now another intentional addition;
        // remove only its shared head geometry + non-body gradient for the old
        // 32-pose baseline. Its own face/geometry/color constraints are tested separately.
        if (drawing is GeometryDrawing reflection && ReferenceEquals(reflection.Geometry, ((GeometryDrawing)Reflection).Geometry)
            && !ReferenceEquals(reflection.Brush, StaticBrush("Body")) && reflection.Brush is RadialGradientBrush) return null;
        if (ReferenceEquals(drawing, QueryRing(ResetVisualState.Failed))) return null;
        if (drawing is DrawingGroup { Children.Count: 5 } candidate)
        {
            var halo = candidate.Children[0] as GeometryDrawing
                ?? (candidate.Children[0] is DrawingGroup { Children.Count: 1, OpacityMask: not null } masked
                    ? masked.Children[0] as GeometryDrawing : null);
            if (halo is not null && ReferenceEquals(halo.Geometry, ((GeometryDrawing)((DrawingGroup)Ring).Children[0]).Geometry)) return null;
        }
        if (ReferenceEquals(drawing, Ring) || ReferenceEquals(drawing, Reflection)
            || ReferenceEquals(drawing, GoldRing) || ReferenceEquals(drawing, GoldReflection)) return null;
        if (drawing is not DrawingGroup group) return drawing;
        // Clone preserves WPF text guidelines as well as transforms and opacity.
        var result = group.Clone();
        for (var index = group.Children.Count - 1; index >= 0; index--)
            if (WithoutReminderLayers(group.Children[index]) is { } remaining) result.Children[index] = remaining;
            else result.Children.RemoveAt(index);
        return result;
    }

    private static Drawing QueryRing(ResetVisualState state) => ((Lazy<Drawing>)typeof(CompanionBallVisual)
        .GetField("RedResetRing", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Value;

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
    private static void Sta(Action action, int timeoutSeconds = 15)
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(timeoutSeconds)), "In-memory reset color check did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
