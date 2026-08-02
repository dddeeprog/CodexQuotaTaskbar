using System.Drawing;
using System.Windows.Forms;
using CodexQuotaTaskbar.Host.Settings;

namespace CodexQuotaTaskbar.Host.Tray;

internal sealed class TrayController : IDisposable
{
    private readonly NotifyIcon icon;
    private readonly ContextMenuStrip menu;
    private readonly ToolStripMenuItem allTaskbars;
    private readonly ToolStripMenuItem autostart;
    private readonly ToolStripMenuItem notifications;
    private AppSettings settings;

    internal TrayController(AppSettings settings)
    {
        this.settings = settings;
        menu = new ContextMenuStrip();
        var title = new ToolStripMenuItem("Codex 额度") { Enabled = false };
        var refresh = new ToolStripMenuItem("立即刷新");
        refresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        var open = new ToolStripMenuItem("打开 Codex");
        open.Click += (_, _) => OpenCodexRequested?.Invoke(this, EventArgs.Empty);
        allTaskbars = new ToolStripMenuItem("在全部任务栏显示") { Checked = settings.ShowAllTaskbars, CheckOnClick = true };
        allTaskbars.Click += (_, _) => Change(settings with { ShowAllTaskbars = allTaskbars.Checked });
        notifications = new ToolStripMenuItem("低额度提醒") { Checked = settings.LowQuotaNotifications, CheckOnClick = true };
        notifications.Click += (_, _) => Change(settings with { LowQuotaNotifications = notifications.Checked });
        autostart = new ToolStripMenuItem("开机启动") { Checked = settings.StartWithWindows, CheckOnClick = true };
        autostart.Click += (_, _) => ChangeAutostart();
        var exit = new ToolStripMenuItem("退出");
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.AddRange([title, new ToolStripSeparator(), refresh, open, new ToolStripSeparator(), allTaskbars, notifications, autostart, new ToolStripSeparator(), exit]);
        icon = new NotifyIcon
        {
            Text = "Codex 额度",
            Icon = SystemIcons.Information,
            ContextMenuStrip = menu,
            Visible = true,
        };
        icon.DoubleClick += (_, _) => OpenCodexRequested?.Invoke(this, EventArgs.Empty);
    }

    internal event EventHandler? RefreshRequested;
    internal event EventHandler? OpenCodexRequested;
    internal event EventHandler<AppSettings>? SettingsChanged;
    internal event EventHandler? ExitRequested;

    internal AppSettings Settings => settings;

    internal void ShowContextMenu() => menu.Show(Cursor.Position);

    internal void ShowLowQuotaNotification()
    {
        icon.BalloonTipTitle = "Codex 额度较低";
        icon.BalloonTipText = "至少一个额度窗口的剩余量已低于提醒阈值。";
        icon.BalloonTipIcon = ToolTipIcon.Warning;
        icon.ShowBalloonTip(5000);
    }

    private void Change(AppSettings value)
    {
        settings = value;
        SettingsChanged?.Invoke(this, value);
    }

    private void ChangeAutostart()
    {
        try
        {
            AutostartService.SetEnabled(autostart.Checked);
            Change(settings with { StartWithWindows = autostart.Checked });
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException)
        {
            autostart.Checked = settings.StartWithWindows;
            icon.BalloonTipTitle = "无法修改开机启动";
            icon.BalloonTipText = "请检查当前用户的启动项权限。";
            icon.BalloonTipIcon = ToolTipIcon.Error;
            icon.ShowBalloonTip(5000);
        }
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        menu.Dispose();
    }
}
