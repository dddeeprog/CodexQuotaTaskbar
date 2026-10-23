using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.Tray;

namespace CodexQuotaTaskbar.Host.Tests.Update;

public sealed class TrayUpdateNotificationTests
{
    [Fact]
    public void Hidden_tray_keeps_progress_quiet_and_presents_requested_final_results()
    {
        OnSta(() =>
        {
            var results = new List<(string Title, string Message, ToolTipIcon Kind)>();
            using var tray = new TrayController(AppSettings.Default with { ShowTrayIcon = false },
                (title, message, kind) => results.Add((title, message, kind)));

            tray.ShowUpdateNotification("检查更新", "正在连接");
            Assert.Empty(results);
            tray.ShowUpdateNotification("暂无可用更新", "当前版本", showWhenTrayHidden: true);
            Assert.Single(results);
            tray.ShowUpdateNotification("更新失败", "校验失败", ToolTipIcon.Warning, showWhenTrayHidden: true);
            Assert.Equal(2, results.Count);
            Assert.Equal("暂无可用更新", results[0].Title);
            Assert.Equal(ToolTipIcon.Warning, results[1].Kind);
            Assert.False(tray.Settings.TrayIconEnabled);
        });
    }

    [Fact]
    public void Background_result_does_not_open_a_dialog_when_tray_is_hidden()
    {
        OnSta(() =>
        {
            var presented = false;
            using var tray = new TrayController(AppSettings.Default with { ShowTrayIcon = false },
                (_, _, _) => presented = true);
            tray.ShowUpdateNotification("更新失败", "后台检查失败", ToolTipIcon.Warning);
            Assert.False(presented);
            Assert.False(tray.Settings.TrayIconEnabled);
        });
    }

    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Hidden notification test timed out.");
        failure?.Throw();
    }
}
