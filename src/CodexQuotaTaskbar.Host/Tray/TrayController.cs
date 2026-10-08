using System.Drawing;
using System.Windows.Forms;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tray;

internal sealed class TrayController : IDisposable
{
    private readonly NotifyIcon icon;
    private readonly TrayMenuWindow menu;
    private readonly Icon trayIcon;
    private AppSettings settings;

    internal TrayController(AppSettings settings)
    {
        this.settings = settings;
        menu = new TrayMenuWindow(settings);
        menu.RefreshRequested += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        menu.CheckUpdatesRequested += (_, _) => CheckUpdatesRequested?.Invoke(this, EventArgs.Empty);
        menu.OpenCodexRequested += (_, _) => OpenCodexRequested?.Invoke(this, EventArgs.Empty);
        menu.OpenSubscriptionLoginRequested += (_, _) => OpenSubscriptionLoginRequested?.Invoke(this, EventArgs.Empty);
        menu.SettingsChangeRequested += (_, value) => ChangeRequested(value);
        menu.MaterialTransparencyPreviewRequested += (_, value) => MaterialTransparencyPreviewRequested?.Invoke(this, value);
        menu.ExitRequested += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        trayIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!)
            ?? throw new InvalidOperationException("无法加载额度岛应用图标。");
        icon = new NotifyIcon
        {
            Text = "Codex 额度",
            Icon = trayIcon,
            Visible = true,
        };
        icon.MouseUp += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Right)
            {
                ShowContextMenu();
            }
        };
        icon.DoubleClick += (_, _) => OpenCodexRequested?.Invoke(this, EventArgs.Empty);
    }

    internal event EventHandler? RefreshRequested;
    internal event EventHandler? OpenCodexRequested;
    internal event EventHandler? OpenSubscriptionLoginRequested;
    internal event EventHandler? CheckUpdatesRequested;
    internal event EventHandler<AppSettings>? SettingsChanged;
    internal event EventHandler<int>? MaterialTransparencyPreviewRequested;
    internal event EventHandler? ExitRequested;

    internal AppSettings Settings => settings;

    internal void FlushPendingSettings() => menu.FlushMaterialTransparencyChange();

    internal void EnableSubscriptionDetails()
    {
        if (!settings.SubscriptionDetailsEnabled) Change(settings with { SubscriptionDetails = true });
    }

    internal void ShowContextMenu(bool avoidIsland = false) => menu.ShowAt(Cursor.Position, avoidIsland ? 30 : 8);

    internal void ShowLowQuotaNotification()
    {
        icon.BalloonTipTitle = "Codex 额度较低";
        icon.BalloonTipText = "至少一个额度窗口的剩余量已低于提醒阈值。";
        icon.BalloonTipIcon = ToolTipIcon.Warning;
        icon.ShowBalloonTip(5000);
    }

    internal void ShowUpdateNotification(string title, string message, ToolTipIcon kind = ToolTipIcon.Info)
    {
        icon.BalloonTipTitle = title;
        icon.BalloonTipText = message;
        icon.BalloonTipIcon = kind;
        icon.ShowBalloonTip(5000);
    }

    private void Change(AppSettings value)
    {
        settings = value;
        menu.ApplySettings(value);
        SettingsChanged?.Invoke(this, value);
    }

    private void ChangeRequested(AppSettings requested)
    {
        if (requested.StartWithWindows == settings.StartWithWindows)
        {
            Change(requested);
            return;
        }

        try
        {
            AutostartService.SetEnabled(requested.StartWithWindows);
            Change(requested);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException)
        {
            menu.ApplySettings(settings);
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
        trayIcon.Dispose();
        menu.Close();
    }
}
