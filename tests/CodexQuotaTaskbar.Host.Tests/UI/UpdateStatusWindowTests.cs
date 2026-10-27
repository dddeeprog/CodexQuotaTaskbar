using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.UI;
using CodexQuotaTaskbar.Host.Update;
using Button = System.Windows.Controls.Button;

namespace CodexQuotaTaskbar.Host.Tests.UI;

[Collection("Native UI isolation")]
public sealed class UpdateStatusWindowTests
{
    [Fact]
    public void Actual_status_controls_support_progress_retry_and_safe_offscreen_preview() => Sta(() =>
    {
        UpdateStatus[] states = [new(UpdateStage.Checking, "正在检查更新", "连接 GitHub 正式发行版…"),
            new(UpdateStage.Current, "已是最新版本", "当前为 v0.1.13，暂无更高版本的正式更新。"),
            new(UpdateStage.Downloading, "正在下载更新", "版本 v99.0.0（演示）", 48),
            new(UpdateStage.Verifying, "正在校验更新", "确认文件完整性后再启动安装。"),
            new(UpdateStage.Installing, "正在安装更新", "即将重启到新版本（演示）。"),
            UpdateStatus.Failure(new InvalidDataException("更新包 SHA-256 校验失败。"))];
        var windows = new List<UpdateStatusWindow>(); var canvas = new DrawingVisual();
        try
        {
            using (var dc = canvas.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 1120, 680));
                dc.DrawText(new FormattedText("点岛 · 更新提示 / 未安装候选 / 合成数据 / 未 Show、未 Loaded", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 18, Brushes.White, 1), new System.Windows.Point(20, 20));
                for (var i = 0; i < states.Length; i++)
                {
                    var window = new UpdateStatusWindow(); windows.Add(window); window.Apply(states[i]);
                    var retry = (Button)window.FindName("RetryButton");
                    Assert.Equal(states[i].CanRetry ? Visibility.Visible : Visibility.Collapsed, retry.Visibility);
                    var calls = 0; window.RetryRequested += (_, _) => calls++;
                    retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(states[i].CanRetry ? 1 : 0, calls);
                    Assert.Equal(OverlayTypography.Interface, window.FontFamily);
                    var root = (FrameworkElement)window.Content;
                    root.Measure(new System.Windows.Size(360, double.PositiveInfinity)); root.Arrange(new Rect(0, 0, 360, root.DesiredSize.Height)); root.UpdateLayout();
                    Assert.InRange(root.ActualHeight, 100, 290);
                    Assert.False(window.IsLoaded); Assert.False(root.IsLoaded); Assert.False(window.IsVisible);
                    Assert.Equal(nint.Zero, new WindowInteropHelper(window).Handle);
                    var shot = new RenderTargetBitmap(360, (int)Math.Ceiling(root.ActualHeight + root.Margin.Top + root.Margin.Bottom), 96, 96, PixelFormats.Pbgra32);
                    shot.Render(root); shot.Freeze();
                    dc.DrawImage(shot, new Rect(10 + i % 3 * 370, 70 + i / 3 * 300, shot.PixelWidth, shot.PixelHeight));
                }
            }
            if (Environment.GetEnvironmentVariable("CQT_UPDATE_UI_DIR") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory); var path = Path.Combine(directory, "diandao-update-states.png");
                Assert.False(File.Exists(path), "Do not overwrite delivered media.");
                var bitmap = new RenderTargetBitmap(1120, 680, 96, 96, PixelFormats.Pbgra32); bitmap.Render(canvas);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
            }
        }
        finally { foreach (var window in windows) window.Close(); }
    });
    private static void Sta(Action action)
    {
        Exception? error = null; var thread = new Thread(() => { try { action(); } catch (Exception e) { error = e; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
