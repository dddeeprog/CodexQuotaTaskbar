using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.UI;
using Button = System.Windows.Controls.Button;

namespace CodexQuotaTaskbar.Host.Tests.UI;

// Real controls, never Show/EnsureHandle/LoadedEvent. No App, browser or user data.
[Collection("Native UI isolation")]
public sealed class SimplifiedPanelTests
{
    [Fact]
    public void Removed_entries_are_absent_and_remaining_controls_pack_without_empty_rows() => Sta(() =>
    {
        var menu = new TrayMenuWindow(AppSettings.Default);
        var details = new QuotaPopoverWindow();
        try
        {
            Assert.Null(menu.FindName("DotChatButton")); Assert.Null(details.FindName("DotChatButton"));
            Assert.Null(menu.FindName("CompanionPreviewButton"));
            Assert.Null(typeof(TrayMenuWindow).GetEvent("OpenDotChatRequested", BindingFlags.Instance | BindingFlags.NonPublic));
            Assert.Null(typeof(QuotaPopoverWindow).GetEvent("OpenDotChatRequested", BindingFlags.Instance | BindingFlags.NonPublic));
            var root = Layout(menu);
            var primary = (StackPanel)menu.FindName("MenuItems");
            var actions = primary.Children.OfType<Button>()
                .Where(button => button.Visibility == Visibility.Visible).ToArray();
            Assert.Equal(6, actions.Length); Assert.Equal("OpenCodexButton", actions[0].Name);
            Assert.Equal("RefreshButton", actions[1].Name);
            Assert.Equal(0, actions[0].TranslatePoint(default, primary).Y, 3);
            Assert.Equal(actions[0].TranslatePoint(default, primary).Y + actions[0].ActualHeight,
                actions[1].TranslatePoint(default, primary).Y, 3);
            menu.OpenSection(MenuSection.Appearance); Layout(menu);
            var interactions = (StackPanel)menu.FindName("CompanionInteractionItems");
            var choices = interactions.Children.OfType<Button>().ToArray();
            Assert.Equal(new[] { "CompanionMotionButton", "CompanionTypingButton", "CoreComponentTermsButton" }, choices.Select(c => c.Name));
            for (var i = 1; i < choices.Length; i++)
                Assert.Equal(choices[i - 1].TranslatePoint(default, interactions).Y + choices[i - 1].ActualHeight,
                    choices[i].TranslatePoint(default, interactions).Y, 3);
            menu.OpenSection(MenuSection.Preferences); Layout(menu);
            Assert.NotNull(menu.FindName("DotRealtimeButton"));
            var preferences = (FrameworkElement)menu.FindName("PreferencesRoot");
            Assert.True(preferences.IsAncestorOf((DependencyObject)menu.FindName("DotRealtimeButton")));
            Layout(details);
            var close = (Button)details.FindName("CloseButton");
            Assert.Equal(1, Grid.GetColumn(close)); Assert.IsType<Grid>(close.Parent);
            Assert.Equal(0, close.Margin.Right); Assert.Equal(0, close.Margin.Left);
            AssertUnloaded(menu); AssertUnloaded(details);
        }
        finally { menu.Close(); details.Close(); }
    });

    [Fact]
    public void Export_simplified_panels_only_when_explicitly_requested() => Sta(() =>
    {
        var directory = Environment.GetEnvironmentVariable("CQT_SIMPLIFIED_UI_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "diandao-simplified-panels.png");
        Assert.False(File.Exists(path), "Do not overwrite delivered media.");
        var settings = AppSettings.Default with { CompanionBall = true, DotRealtime = true };
        var details = new QuotaPopoverWindow();
        var menu = new TrayMenuWindow(settings);
        var appearance = new TrayMenuWindow(settings);
        var preferences = new TrayMenuWindow(settings);
        Window[] windows = [details, menu, appearance, preferences];
        try
        {
            var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            details.Apply(QuotaSnapshot.Available([
                new(QuotaWindowKind.Primary, 72, 300, now.AddHours(3)),
                new(QuotaWindowKind.Secondary, 41, 10080, now.AddDays(4)),
            ], now, "PRO 5X"));
            appearance.TrySetPageHeightLimit(700); preferences.TrySetPageHeightLimit(700);
            appearance.OpenSection(MenuSection.Appearance); preferences.OpenSection(MenuSection.Preferences);
            var roots = windows.Select(Layout).ToArray();
            Assert.All(windows, AssertUnloaded);
            const int width = 1320, height = 850;
            var canvas = new DrawingVisual();
            using (var dc = canvas.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(26, 28, 32)), null, new Rect(0, 0, width, height));
                Text(dc, "点岛 · 面板精简预览", 25, 24, 20);
                Text(dc, "未安装候选 / 合成数据 / 实际控件离屏绘制 / 未 Show、未 Loaded、无 HWND", 13, 26, 62);
                string[] titles = ["左键 · 额度详情", "右键 · 主菜单", "外观与互动", "功能设置 · 保留实时接收"];
                var x = 20d;
                for (var i = 0; i < roots.Length; i++)
                {
                    Text(dc, titles[i], 14, x + 8, 105);
                    var root = roots[i];
                    dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.Fill, AutoLayoutContent = false }, null,
                        new Rect(x, 140, root.ActualWidth, root.ActualHeight));
                    x += root.ActualWidth + 24;
                }
                Text(dc, "已移除：详情 Dot 按钮、主菜单 Dot 消息按钮、试玩表情入口。白球通知、清提醒和正常 32 种表情轮换保留。", 13, 26, 785);
                Text(dc, "仅验证控件与布局；没有采集桌面或真实私聊，玻璃背景为离屏效果。", 12, 26, 815);
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(canvas);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path); encoder.Save(stream);
            Assert.All(windows, AssertUnloaded);
        }
        finally { foreach (var window in windows) window.Close(); }
    });

    private static FrameworkElement Layout(Window window)
    {
        var root = (FrameworkElement)window.Content;
        for (var i = 0; i < 3; i++)
        {
            root.Measure(new System.Windows.Size(window.Width, double.PositiveInfinity));
            root.Arrange(new Rect(0, 0, window.Width, root.DesiredSize.Height)); root.UpdateLayout();
        }
        AssertUnloaded(window); return root;
    }
    private static void AssertUnloaded(Window window)
    {
        Assert.False(window.IsLoaded); Assert.False(window.IsVisible);
        Assert.False(((FrameworkElement)window.Content).IsLoaded);
        Assert.Equal(nint.Zero, new WindowInteropHelper(window).Handle);
    }
    private static void Text(DrawingContext dc, string text, double size, double x, double y) => dc.DrawText(
        new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), size, Brushes.WhiteSmoke, 1), new System.Windows.Point(x, y));
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
