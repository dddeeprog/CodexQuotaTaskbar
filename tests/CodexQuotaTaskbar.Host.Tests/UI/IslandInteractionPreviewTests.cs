using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Host.Bongo;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.Overlay;
using CodexQuotaTaskbar.Host.Provider;
using CodexQuotaTaskbar.Host.Resets;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

// Explicit offline media fixture. No App, Show, input injection, profile or network.
[Collection("Native UI isolation")]
public sealed class IslandInteractionPreviewTests
{
    [Fact]
    public void Shared_timelines_preserve_production_parameters() => Sta(() =>
    {
        var enter = TrayMenuWindow.CreatePopupAnimation(0, 1, 220);
        Assert.Equal(FillBehavior.Stop, enter.FillBehavior);
        Assert.Equal(EasingMode.EaseOut, Assert.IsType<CubicEase>(enter.EasingFunction).EasingMode);
        Assert.Equal(TimeSpan.FromMilliseconds(220), enter.Duration.TimeSpan);
        Assert.Equal(TimeSpan.FromMilliseconds(130), QuotaPopoverWindow.CreateSurfaceAnimation(1, 0, false).Duration.TimeSpan);
        Assert.Equal(FillBehavior.HoldEnd, QuotaPopoverWindow.CreateSurfaceAnimation(0, 1, true).FillBehavior);
        var section = QuotaPopoverWindow.CreateSectionEntranceAnimation(.7, 1, 2);
        Assert.Equal(TimeSpan.FromMilliseconds(50), section.KeyFrames[1].KeyTime.TimeSpan);
        Assert.Equal(TimeSpan.FromMilliseconds(200), section.KeyFrames[2].KeyTime.TimeSpan);
        using var sample = new Sampler(enter, 220, 0, 1);
        Assert.InRange(sample.At(110), .87, .88);
        Assert.Equal(1, sample.At(220));
    });

    [Fact]
    public void Export_interaction_mp4_only_when_explicitly_requested() => Sta(() =>
    {
        var directory = Environment.GetEnvironmentVariable("CQT_INTERACTION_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        var ffmpeg = Environment.GetEnvironmentVariable("CQT_PREVIEW_FFMPEG");
        Assert.True(File.Exists(ffmpeg));
        Directory.CreateDirectory(directory);
        const int width = 1280, height = 720, fps = 30;
        var includeQuery = Environment.GetEnvironmentVariable("CQT_QUERY_RING_PREVIEW") == "1";
        var frames = includeQuery ? 1020 : 600;
        var queryClock = 0d;
        using var queryBall = new CompanionBallVisual(() => queryClock);
        queryBall.SetState("35", true);
        var sampleFrame = typeof(CompanionBallVisual).GetMethod("OnFrame", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var now = DateTimeOffset.Parse("2026-10-27T12:00:00+08:00");
        var owner = new QuotaCapsuleWindow("synthetic-interaction");
        owner.SetPointerObservationAllowed(false); owner.SetCompanion(true, true);
        var ball = Field<CompanionBallControl>(owner, "companion");
        ball.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        ball.Visual!.SetState("35", false);
        var root = (FrameworkElement)owner.Content; owner.Content = null;
        var quota = QuotaSnapshot.Available([new(QuotaWindowKind.Primary, 62, 300, now.AddHours(5)),
            new(QuotaWindowKind.Secondary, 48, 10080, now.AddDays(7))], now, "演示计划");
        owner.Apply(quota);
        var details = new QuotaPopoverWindow(); details.Apply(quota);
        var detailRoot = (FrameworkElement)details.Content; details.Content = null;
        var menu = new TrayMenuWindow(AppSettings.Default with { ShowTrayIcon = false }, activateOnShow: false);
        var menuRoot = (FrameworkElement)menu.Content; menu.Content = null;
        var reply = new DotInlineView();
        reply.ApplyState(State(1, "这是离线合成消息，没有连接 Dot 网页。") with { DraftText = "演示回复（不会发送）" });
        var dotCount = 0; var primaryCount = 0; var contextCount = 0;
        owner.CompanionInvoked += (_, _) => { dotCount++; owner.ShowInlineDot(reply); };
        owner.PrimaryInvoked += (_, _) => primaryCount++;
        owner.ContextInvoked += (_, _) => contextCount++;

        using var coordinator = new OverlayCoordinator(false, unifiedPanel: true, observePointer: false);
        Field<DispatcherTimer>(coordinator, "timer").Stop();
        SetField(coordinator, "bongoSelected", true);
        var markerOwner = new QuotaCapsuleWindow("synthetic-marker-local");
        markerOwner.SetPointerObservationAllowed(false);
        markerOwner.ConfigureBongoSurface(true, 192);
        markerOwner.PresentBongoFrame(new BongoFrame(1, 1, [0, 0, 0, 0], 4));
        Field<Dictionary<string, QuotaCapsuleWindow>>(coordinator, "windows").Add(markerOwner.MonitorId, markerOwner);
        markerOwner.DotMarkerInvoked += (_, _) => coordinator.HandleDotMarkerInvoked(markerOwner);
        coordinator.SetInlineDotMode(true); coordinator.SetDotPresentationEnabled(true);
        var inbox = new DotUnreadInbox();
        var acknowledgements = new DotUnreadAcknowledgements(inbox);
        var ackCount = 0;
        coordinator.InlineDotActionRequested += action =>
        {
            Assert.Equal("ack-notice", action.Kind);
            Assert.True(acknowledgements.Acknowledge(action.ReadToken!, action.ReadSequence)); ackCount++;
        };
        var notice = new DotTopNoticeView();
        void SyntheticMessage(string id, long sequence)
        {
            inbox.ObserveNotification(new(id, "assistant", "新的合成提醒", null));
            coordinator.ApplyInlineDotState(State(sequence, "新的合成提醒") with
            { ReadToken = acknowledgements.Issue(sequence), ReadSequence = sequence, UnreadCount = inbox.UnreadCount });
            notice.Display(new(id, "新的合成提醒 · 点击感叹号清除", null, true), inbox.UnreadCount);
            Layout((FrameworkElement)markerOwner.Content, 280, markerOwner.DesiredHeight);
        }
        using var detailIn = new PopupSamples(true, true);
        using var detailOut = new PopupSamples(true, false);
        using var menuIn = new PopupSamples(false, true);
        using var menuOut = new PopupSamples(false, false);
        using var collapse = new Sampler(QuotaCapsuleWindow.CreateExtensionHeightAnimation(26, 0), 160, 26, 0);
        var resetProperty = (DependencyProperty)typeof(QuotaCapsuleWindow).GetField("ResetExtensionHeightProperty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var sectionNames = new[] { "AccountSection", "QuotaSection", "AnnouncementsSection" };
        var sectionOpacity = Enumerable.Range(0, 3).Select(i => new Sampler(QuotaPopoverWindow.CreateSectionEntranceAnimation(.7, 1, i), 150 + i * 25, .7, 1)).ToArray();
        var sectionOffset = Enumerable.Range(0, 3).Select(i => new Sampler(QuotaPopoverWindow.CreateSectionEntranceAnimation(3, 0, i), 150 + i * 25, 3, 0)).ToArray();
        var target = Path.Combine(directory, includeQuery ? "diandao-query-ring-preview.mp4" : "diandao-interaction-preview.mp4");
        if (!includeQuery) Assert.False(File.Exists(target), "Keep the previously exported interaction-only clip intact.");
        var start = new ProcessStartInfo(ffmpeg!) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pixel_format", "bgra",
            "-video_size", "1280x720", "-framerate", "30", "-i", "pipe:0", "-an", "-c:v", "libx264", "-preset", "medium",
            "-crf", "19", "-pix_fmt", "yuv420p", "-movflags", "+faststart", target }) start.ArgumentList.Add(arg);
        using var encoder = Process.Start(start)!;
        var errors = encoder.StandardError.ReadToEndAsync();
        var canvas = new DrawingVisual();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var pixels = new byte[width * height * 4];
        var intermediatePopup = new HashSet<double>(); var intermediateHeight = new HashSet<double>();
        var proofs = new HashSet<int> { 0, 61, 119, 121, 123, 126, 150, 212, 216, 240, 302, 307, 330, 435, 450, 465, 510, 541, 543, 545, 599 };
        proofs.UnionWith([600, 609, 630, 675, 720, 779, 780, 801, 822, 864, 900, 959, 960, 1019]);
        try
        {
            for (var frame = 0; frame < frames; frame++)
            {
                var seconds = frame / (double)fps;
                if (frame == 60) Gesture(owner, IslandHitRegion.Ball, MouseButton.Left, frame);
                if (frame == 120) { owner.HideInlineDot(); Gesture(owner, IslandHitRegion.Quota, MouseButton.Left, frame); }
                if (frame == 210) Gesture(owner, IslandHitRegion.Quota, MouseButton.Right, frame);
                if (frame == 300) Gesture(owner, IslandHitRegion.Quota, MouseButton.Left, frame);
                if (frame == 420) SyntheticMessage("synthetic-first", 1);
                if (frame == 450)
                {
                    var bounds = markerOwner.DotMarkerBoundsDip;
                    var region = markerOwner.PreparePointerDown(new Point(bounds.Left + 12, bounds.Top + 12), MouseButton.Left);
                    Assert.Equal(IslandHitRegion.None, region);
                    Assert.False(markerOwner.HasPointerGesture);
                    markerOwner.DotMarkerButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(0, inbox.UnreadCount);
                    coordinator.ApplyInlineDotState(State(2, "提醒已清除") with { UnreadCount = 0 });
                }
                if (frame == 465) SyntheticMessage("synthetic-later", 3);
                if (frame == 510) owner.ApplyResetSchedule(new(new ResetSchedule("synthetic-reset", ResetKind.Regular,
                    now.AddMinutes(-1), now.AddHours(3), CodexResetFeedClient.SiteUri), now, false, null), now, TimeZoneInfo.Utc);
                if (frame == 540) owner.ApplyResetSchedule(new(null, now, false, null), now, TimeZoneInfo.Utc);
                if (frame >= 540)
                {
                    var h = collapse.At((seconds - 18) * 1000); owner.SetValue(resetProperty, h);
                    if (h > 0 && h < 26) intermediateHeight.Add(Math.Round(h, 4));
                }
                Layout(root, 280, owner.DesiredHeight);
                var query = seconds < 26 ? new ResetQueryState(1, ResetQueryPhase.Checking, now.AddSeconds(20))
                    : seconds < 32 ? new ResetQueryState(2, ResetQueryPhase.Failed) : new ResetQueryState(3, ResetQueryPhase.Idle);
                queryClock = Math.Max(0, seconds - 20) * 1000;
                queryBall.SetResetState(query.Select(false, null, now.AddSeconds(seconds)));
                sampleFrame.Invoke(queryBall, [null, EventArgs.Empty]);
                var phase = seconds < 2 ? "准备 / 实际控件" : seconds < 4 ? "白球 → Dot 消息控件"
                    : seconds < 7 ? "额度左键 → 详情" : seconds < 10 ? "右键 → 菜单" : seconds < 13 ? "左键 → 返回详情"
                    : seconds < 14 ? "面板收起" : seconds < 15 ? "感叹号局部 / 合成提醒"
                    : seconds < 15.5 ? "点击后清除该批提醒" : seconds < 17 ? "新消息重新累计"
                    : seconds < 18 ? "重置预告小行" : seconds < 20 ? "实际收起高度 / 160 ms"
                    : seconds < 20.3 ? "公共重置查询开始 / 延迟显蓝" : seconds < 26 ? "检测中 / 蓝色光点慢转"
                    : seconds < 32 ? "检测异常 / 红色柔闪" : "成功但无重置安排 / 恢复普通状态";
                using (var dc = canvas.RenderOpen())
                {
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(23, 25, 28)), null, new Rect(0, 0, width, height));
                    Text(dc, includeQuery ? "点岛 · 交互与蓝红候选预览（未安装）" : "点岛 · 交互补充预览", 28, 42, 28);
                    Text(dc, phase, 19, 44, 76);
                    Text(dc, "演示点击 / 合成内容 / 实际控件与生产时间线离屏采样", 14, 44, 110, Brushes.Silver);
                    DrawElement(dc, root, new Rect(130, 240, 420, owner.DesiredHeight * 1.5));
                    if (seconds >= 4 && seconds < 7.13) DrawPopup(dc, detailRoot, details, detailIn, detailOut,
                        seconds < 7 ? (seconds - 4) * 1000 : (seconds - 7) * 1000, seconds < 7, true);
                    if (seconds >= 7.13 && seconds < 10.13) DrawPopup(dc, menuRoot, menu, menuIn, menuOut,
                        seconds < 10 ? (seconds - 7.13) * 1000 : (seconds - 10) * 1000, seconds < 10, false);
                    if (seconds >= 10.13 && seconds < 13.13) DrawPopup(dc, detailRoot, details, detailIn, detailOut,
                        seconds < 13 ? (seconds - 10.13) * 1000 : (seconds - 13) * 1000, seconds < 13, true);
                    if (seconds is >= 14 and < 17)
                    {
                        Text(dc, "实际感叹号按钮（局部放大）", 17, 700, 190);
                        if (markerOwner.DotMarkerVisible) DrawElement(dc, markerOwner.DotMarkerButton!, new Rect(815, 245, 96, 96));
                        Text(dc, "当前合成未读：" + inbox.UnreadCount, 22, 770, 370);
                        if (inbox.UnreadCount > 0) { Layout(notice, 340, 36); DrawElement(dc, notice, new Rect(680, 420, 408, 43.2)); }
                        Text(dc, "原 Button → ack-notice → 内存接收器", 14, 700, 490, Brushes.Silver);
                    }
                    if (seconds is >= 17 and < 20) Text(dc, $"预告行实际高度：{owner.ResetScheduleHeightForLayout:F2} DIP", 23, 680, 310);
                    if (seconds >= 20)
                    {
                        dc.PushTransform(new TranslateTransform(735, 210)); dc.PushTransform(new ScaleTransform(4, 4));
                        dc.DrawDrawing(ResetNoticeBallColorTests.RecordDrawing(queryBall)); dc.Pop(); dc.Pop();
                        Text(dc, "真实白球渲染器 · 合成查询状态", 18, 690, 460);
                        Text(dc, seconds < 26 ? "蓝：6 秒一圈；请求结束即结束蓝态" : seconds < 32 ? "红：2.8 秒周期，柔和明暗，不灭灯" : "无安排不是错误，也不是绿色恢复", 15, 690, 500, Brushes.Silver);
                        Text(dc, "绿态 1h 优先；32 表情保留；不增加新计时器", 14, 690, 540, Brushes.Silver);
                    }
                    var clickAt = seconds < 4 ? 2d : seconds < 7 ? 4d : seconds < 10 ? 7d : seconds < 14 ? 10d : 15d;
                    if (seconds - clickAt is >= 0 and < .45)
                    {
                        var point = seconds < 4 ? new Point(500, 275) : seconds < 14 ? new Point(330, 295) : new Point(863, 293);
                        dc.DrawEllipse(null, new Pen(Brushes.Gold, 2), point, 18, 18);
                        Text(dc, seconds is >= 7 and < 10 ? "合成右键" : "合成左键", 13, point.X - 30, point.Y - 45, Brushes.Gold);
                    }
                    Text(dc, "无 Show / 无 HWND / 无系统鼠标录制 · Dot 仅控件布局，未打开网页或连接真实账号", 13, 42, 648, Brushes.Silver);
                    Text(dc, "猫模型未启动；猫区使用既有同类路由测试。此片不证明原生窗口生命周期或现场双弹已修复。", 13, 42, 677, Brushes.Silver);
                }
                bitmap.Clear(); bitmap.Render(canvas); bitmap.CopyPixels(pixels, width * 4, 0);
                encoder.StandardInput.BaseStream.Write(pixels);
                if (proofs.Contains(frame)) { var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(directory, $"proof-{frame:D3}.png")); png.Save(file); }
            }
            Assert.Equal(1, dotCount); Assert.Equal(2, primaryCount); Assert.Equal(1, contextCount);
            Assert.Equal(1, ackCount); Assert.Equal(1, inbox.UnreadCount);
            Assert.True(intermediatePopup.Count >= 8); Assert.True(intermediateHeight.Count >= 4);
            Assert.False(ball.Visual.AnimationRunning);
            Assert.False(queryBall.AnimationRunning);
            foreach (var window in new Window[] { owner, details, menu, markerOwner })
            { Assert.False(window.IsVisible); Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Handle); }
        }
        finally
        {
            owner.Close(); details.Close(); menu.Close();
            foreach (var sampler in sectionOpacity.Concat(sectionOffset)) sampler.Dispose();
            encoder.StandardInput.Close();
        }
        Assert.True(encoder.WaitForExit(30000)); Assert.Equal(0, encoder.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(errors.GetAwaiter().GetResult()));

        void DrawPopup(DrawingContext dc, FrameworkElement view, Window window, PopupSamples entering, PopupSamples exiting,
            double milliseconds, bool opening, bool detail)
        {
            var sample = opening ? entering : exiting;
            var opacity = sample.Opacity.At(milliseconds); var scale = sample.Scale.At(milliseconds); var shift = sample.Offset.At(milliseconds);
            if (opacity is > 0 and < 1) intermediatePopup.Add(Math.Round(opacity, 5));
            if (detail)
                for (var i = 0; i < sectionNames.Length; i++)
                {
                    var section = (FrameworkElement)window.FindName(sectionNames[i]);
                    section.Opacity = opening ? sectionOpacity[i].At(milliseconds) : 1;
                    section.RenderTransform = new TranslateTransform(0, opening ? sectionOffset[i].At(milliseconds) : 0);
                }
            var w = detail ? 360d : 272d;
            Layout(view, w, 490);
            dc.PushOpacity(opacity); dc.PushTransform(new TranslateTransform(695 + w * (1 - scale) / 2, 146 + shift));
            dc.PushTransform(new ScaleTransform(scale, scale)); DrawElement(dc, view, new Rect(0, 0, w, 490)); dc.Pop(); dc.Pop(); dc.Pop();
            Text(dc, $"Opacity {opacity:F3}   Scale {scale:F3}   Offset {shift:F2}", 13, 690, 620, Brushes.Silver);
        }
    }, 120);

    private sealed class PopupSamples : IDisposable
    {
        internal readonly Sampler Opacity, Scale, Offset;
        internal PopupSamples(bool detail, bool opening)
        {
            var duration = opening ? 220 : 130;
            DoubleAnimation Animation(double from, double to) => detail ? QuotaPopoverWindow.CreateSurfaceAnimation(from, to, opening)
                : TrayMenuWindow.CreatePopupAnimation(from, to, duration);
            Opacity = new(Animation(opening ? 0 : 1, opening ? 1 : 0), duration, opening ? 0 : 1, opening ? 1 : 0);
            Scale = new(Animation(opening ? .97 : 1, opening ? 1 : .98), duration, opening ? .97 : 1, opening ? 1 : .98);
            Offset = new(Animation(opening ? 10 : 0, opening ? 0 : 7), duration, opening ? 10 : 0, opening ? 0 : 7);
        }
        public void Dispose() { Opacity.Dispose(); Scale.Dispose(); Offset.Dispose(); }
    }
    private sealed class Sampler : IDisposable
    {
        private readonly DoubleAnimationBase timeline; private readonly AnimationClock clock;
        private readonly double duration, from, to;
        internal Sampler(DoubleAnimationBase timeline, double duration, double from, double to)
        {
            this.timeline = timeline; this.duration = duration; this.from = from; this.to = to;
            clock = (AnimationClock)timeline.CreateClock(true); clock.Controller!.Begin(); clock.Controller.Pause();
        }
        internal double At(double milliseconds)
        {
            if (milliseconds <= 0) return from; if (milliseconds >= duration) return to;
            clock.Controller!.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(milliseconds), TimeSeekOrigin.BeginTime);
            return timeline.GetCurrentValue(from, to, clock);
        }
        public void Dispose() => clock.Controller!.Remove();
    }
    private static DotInlineState State(long sequence, string text) => new("11111111111111111111111111111111", sequence, 0, "",
        [new("synthetic-message", text, null, true)], 1, 0, "synthetic-message", sequence, true, false, false, false, true, false, "离线合成状态");
    private static void Gesture(QuotaCapsuleWindow owner, IslandHitRegion region, MouseButton button, int stamp)
    { Assert.True(owner.BeginPointerGesture(button, region, stamp, 0, 0, () => true)); Assert.True(owner.EndPointerGesture(button, region, stamp + 1)); }
    private static void Layout(FrameworkElement root, double width, double height)
    { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
    private static void DrawElement(DrawingContext dc, FrameworkElement element, Rect bounds) =>
        dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.Fill }, null, bounds);
    private static void Text(DrawingContext dc, string text, double size, double x, double y, Brush? brush = null) =>
        dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), size, brush ?? Brushes.WhiteSmoke, 1), new Point(x, y));
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void SetField(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Sta(Action action, int seconds = 30)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(seconds)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
