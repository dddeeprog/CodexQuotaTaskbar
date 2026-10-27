using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Host.Provider;
using CodexQuotaTaskbar.Host.Resets;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

[Collection("Native UI isolation")]
public sealed class AdaptiveQuotaLayoutTests
{
    [Theory]
    [InlineData(false, false, 96)] [InlineData(false, true, 96)] [InlineData(true, false, 144)] [InlineData(true, true, 144)]
    [InlineData(false, false, 192)] [InlineData(false, true, 192)] [InlineData(true, false, 192)] [InlineData(true, true, 192)]
    public void Dynamic_quota_layout_preserves_ball_cat_marker_and_single_tap_routes(bool unified, bool cat, int dpi) => Sta(() =>
    {
        var window = new QuotaCapsuleWindow("offline-anchors", unified);
        try
        {
            var root = (FrameworkElement)window.Content;
            VisualTreeHelper.SetRootDpi(window, new DpiScale(dpi / 96d, dpi / 96d));
            VisualTreeHelper.SetRootDpi(root, new DpiScale(dpi / 96d, dpi / 96d));
            window.SetPointerObservationAllowed(false); window.SetCompanion(true, true);
            var ball = (CompanionBallControl)typeof(QuotaCapsuleWindow).GetField("companion", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            // Only initializes the managed vector ball, never Window/root Loaded,
            // blur capture, OS input, a model runtime or a native window.
            ball.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            if (cat) { window.ConfigureBongoSurface(true, 192); window.PresentBongoFrame(new CodexQuotaTaskbar.Host.Bongo.BongoFrame(1, 1, [255, 255, 255, 255], 4)); }
            window.SetDotMarker(2, canAcknowledge: true);
            var taps = 0; window.CompanionInvoked += (_, _) => taps++;
            for (var i = 0; i < 3; i++)
            {
                window.Apply(Sample(i == 1 ? 2 : 1)); Layout(window);
                var bounds = window.StatusBallBoundsDip; Assert.False(bounds.IsEmpty);
                var point = new System.Windows.Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                Assert.Equal(IslandHitRegion.Ball, window.PointerRegion(point));
                Assert.True(window.BeginPointerGesture(System.Windows.Input.MouseButton.Left, IslandHitRegion.Ball, 100 + i * 20, 0, 0, () => true));
                Assert.True(window.EndPointerGesture(System.Windows.Input.MouseButton.Left, IslandHitRegion.Ball, 101 + i * 20));
                Assert.False(window.EndPointerGesture(System.Windows.Input.MouseButton.Left, IslandHitRegion.Ball, 101 + i * 20));
                Assert.Equal(i + 1, taps);
                var marker = window.DotMarkerBoundsDip;
                Assert.False(marker.IsEmpty); Assert.False(marker.IntersectsWith(bounds));
                Assert.InRange(marker.Bottom, 0, window.DesiredHeight);
                if (cat) Assert.True(window.BongoHasFrame);
                AssertUnloaded(window);
            }
        }
        finally { window.Close(); }
    });
    [Theory]
    [InlineData(false, 96)] [InlineData(false, 144)] [InlineData(false, 192)]
    [InlineData(true, 96)] [InlineData(true, 144)] [InlineData(true, 192)]
    public void One_two_one_windows_reflow_real_controls_without_an_empty_slot(bool unified, int dpi) => Sta(() =>
    {
        var window = new QuotaCapsuleWindow("offline-layout", unified);
        try
        {
            VisualTreeHelper.SetRootDpi(window, new DpiScale(dpi / 96d, dpi / 96d));
            var root = (FrameworkElement)window.Content;
            VisualTreeHelper.SetRootDpi(root, new DpiScale(dpi / 96d, dpi / 96d));
            var headers = new List<double>(); var changed = 0;
            window.PresentationChanged += (_, _) => changed++;
            foreach (var count in new[] { 1, 2, 1 })
            {
                window.Apply(Sample(count)); Layout(window);
                var second = (Grid)window.FindName("SecondRow"); var first = (Grid)window.FindName("FirstRow");
                Assert.Equal(count == 2 ? Visibility.Visible : Visibility.Collapsed, second.Visibility);
                Assert.Equal(count == 2 ? "5 小时" : "7 天", ((TextBlock)window.FindName("FirstLabel")).Text);
                Assert.InRange(first.ActualHeight, 15, 30);
                if (unified) Assert.Equal(count == 2 ? GridUnitType.Star : GridUnitType.Pixel,
                    ((Grid)window.FindName("QuotaHeader")).ColumnDefinitions[1].Width.GridUnitType);
                else Assert.Equal(count == 2 ? 19 : 0, ((Grid)second.Parent).RowDefinitions[1].Height.Value);
                headers.Add(window.LayoutBaseHeight);
                var header = (Grid)window.FindName("QuotaHeader");
                var point = header.TranslatePoint(new System.Windows.Point(4, header.ActualHeight / 2), root);
                Assert.Equal(IslandHitRegion.Quota, window.PointerRegion(point));
                AssertUnloaded(window);
            }
            Assert.Equal(headers[0], headers[2]); Assert.Equal(unified ? 0 : 12, headers[1] - headers[0]);
            Assert.Equal(3, changed);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(30, "30 分钟")] [InlineData(90, "90 分钟")] [InlineData(1500, "25 小时")]
    public void Detail_title_preserves_actual_duration_and_unknown_bucket_identity(int minutes, string label)
    {
        var title = QuotaPopoverWindow.FormatWindowTitle(new(QuotaWindowKind.Primary, 0, minutes, null, "future", "新额度"));
        Assert.Equal($"新额度 (future) · {label}额度", title);
        Assert.Contains("时长未知", QuotaPopoverWindow.FormatWindowTitle(new(QuotaWindowKind.Primary, 25, null, null)));
    }

    [Fact]
    public void Account_context_change_is_rejected_and_unknown_or_reappearing_windows_do_not_turn_green()
    {
        static JsonElement Account(string email) => JsonDocument.Parse($"{{\"account\":{{\"type\":\"chatgpt\",\"email\":\"{email}\"}}}}").RootElement.Clone();
        Assert.True(CodexRateLimitProvider.IsStableAccountContext(Account("a@example.invalid"), Account("a@example.invalid")));
        Assert.False(CodexRateLimitProvider.IsStableAccountContext(Account("a@example.invalid"), Account("b@example.invalid")));
        var tracker = new QuotaResetTracker(); var now = DateTimeOffset.UtcNow; var scope = QuotaResetTracker.ScopeForSource("synthetic-a");
        QuotaSnapshot S(int second, double value, int? duration = 300) => QuotaSnapshot.Available(
            [new(QuotaWindowKind.Primary, value, duration, now.AddHours(1))], now.AddSeconds(second));
        Assert.Null(tracker.Observe(S(0, 10), scope, now));
        Assert.Null(tracker.Observe(S(1, 90, null), scope, now.AddSeconds(1)));
        Assert.Null(tracker.Observe(S(2, 90), scope, now.AddSeconds(2)));
        Assert.Null(tracker.Observe(QuotaSnapshot.Available([], now.AddSeconds(3)), scope, now.AddSeconds(3)));
        Assert.Null(tracker.Observe(S(4, 95), scope, now.AddSeconds(4)));
        tracker.InvalidateSample();
        Assert.Null(tracker.Observe(S(5, 99), scope, now.AddSeconds(5)));
        Assert.Null(tracker.Observe(S(6, 100), QuotaResetTracker.ScopeForSource("synthetic-b"), now.AddSeconds(6)));
        Assert.Equal(TimeSpan.FromMinutes(3), CodexRateLimitProvider.AutomaticRefreshInterval);
        Assert.Equal(TimeSpan.FromSeconds(2), CodexRateLimitProvider.SessionsRefreshInterval);
    }

    [Fact]
    public void Export_actual_adaptive_controls_only_on_request() => Sta(() =>
    {
        var directory = Environment.GetEnvironmentVariable("CQT_ADAPTIVE_UI_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "diandao-adaptive-quota.png");
        Assert.False(File.Exists(path), "Never overwrite delivered media.");
        var windows = new List<Window>(); var canvas = new DrawingVisual();
        try
        {
            using (var dc = canvas.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 1100, 780));
                Text(dc, "点岛 · 按实际窗口自适应 / 未安装候选 / 合成数据 / 未 Show、未 Loaded", 20, 20, 18);
                string[] labels = ["周额度（不代填 5 小时）", "接口新增 5 小时", "时长、重置未知", "明确空窗口", "失败保留缓存", "读取未知"];
                var now = DateTimeOffset.UnixEpoch;
                QuotaSnapshot[] samples = [Sample(1), Sample(2), QuotaSnapshot.Available([new(QuotaWindowKind.Primary, 58, null, null)], now),
                    QuotaSnapshot.Available([], now), QuotaSnapshot.Stale(Sample(1).Windows, now, "连接中断"), QuotaSnapshot.Unavailable("读取失败")];
                for (var i = 0; i < samples.Length; i++)
                {
                    var x = 20 + i % 3 * 360; var y = 70 + i / 3 * 280;
                    Text(dc, labels[i], x, y, 14);
                    foreach (var unified in new[] { true, false })
                    {
                        var window = new QuotaCapsuleWindow("offline-preview", unified); windows.Add(window); window.Apply(samples[i]); Layout(window);
                        var root = (FrameworkElement)window.Content;
                        var shot = new RenderTargetBitmap((int)window.LayoutWidth, (int)Math.Ceiling(window.DesiredHeight), 96, 96, PixelFormats.Pbgra32);
                        shot.Render(root); shot.Freeze();
                        dc.DrawImage(shot, new Rect(x, y + (unified ? 32 : 120), shot.PixelWidth, shot.PixelHeight));
                    }
                }
                Text(dc, "上：一体面板；下：经典气泡。计划标签不决定窗口；0% 仍是有效额度，未知不等于无限。", 20, 710, 14);
            }
            var bitmap = new RenderTargetBitmap(1100, 780, 96, 96, PixelFormats.Pbgra32); bitmap.Render(canvas);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
            Assert.All(windows, AssertUnloaded);
        }
        finally { foreach (var window in windows) window.Close(); }
    });
    private static QuotaSnapshot Sample(int count)
    {
        var now = DateTimeOffset.UnixEpoch;
        QuotaWindowSnapshot[] windows = count == 1 ? [new(QuotaWindowKind.Secondary, 41, 10080, now)]
            : [new(QuotaWindowKind.Primary, 72, 300, now), new(QuotaWindowKind.Secondary, 41, 10080, now)];
        return QuotaSnapshot.Available(windows, now, "PRO 20X");
    }
    private static void Layout(QuotaCapsuleWindow window)
    {
        var root = (FrameworkElement)window.Content;
        for (var i = 0; i < 3; i++) { root.Measure(new System.Windows.Size(window.LayoutWidth, window.DesiredHeight));
            root.Arrange(new Rect(0, 0, window.LayoutWidth, window.DesiredHeight)); root.UpdateLayout(); }
    }
    private static void AssertUnloaded(Window window)
    {
        Assert.False(window.IsLoaded); Assert.False(window.IsVisible); Assert.False(((FrameworkElement)window.Content).IsLoaded);
        Assert.Equal(nint.Zero, new WindowInteropHelper(window).Handle);
    }
    private static void Text(DrawingContext dc, string text, double x, double y, double size) => dc.DrawText(
        new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, Brushes.WhiteSmoke, 1), new System.Windows.Point(x, y));
    private static void Sta(Action action)
    {
        Exception? error = null; var thread = new Thread(() => { try { action(); } catch (Exception e) { error = e; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
