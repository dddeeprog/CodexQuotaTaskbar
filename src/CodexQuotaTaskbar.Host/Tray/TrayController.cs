using System.Drawing;
using System.IO;
using System.Windows.Forms;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tray;

internal sealed class TrayController : IDisposable
{
    private readonly NotifyIcon icon;
    private readonly TrayMenuWindow menu;
    private readonly Icon trayIcon;
    private readonly Stream trayIconStream;
    private AppSettings settings;
    private readonly Action<string, string, ToolTipIcon> hiddenUpdateResult;

    internal TrayController(AppSettings settings, Action<string, string, ToolTipIcon>? hiddenUpdateResult = null)
    {
        this.settings = settings;
        this.hiddenUpdateResult = hiddenUpdateResult ?? ShowHiddenUpdateResult;
        menu = new TrayMenuWindow(settings);
        menu.RefreshRequested += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        menu.CheckUpdatesRequested += (_, _) => CheckUpdatesRequested?.Invoke(this, EventArgs.Empty);
        menu.OpenCodexRequested += (_, _) => OpenCodexRequested?.Invoke(this, EventArgs.Empty);
        menu.OpenDotChatRequested += (_, _) => OpenDotChatRequested?.Invoke(this, EventArgs.Empty);
        menu.OpenSubscriptionLoginRequested += (_, _) => OpenSubscriptionLoginRequested?.Invoke(this, EventArgs.Empty);
        menu.SettingsChangeRequested += (_, value) => ChangeRequested(value);
        menu.MaterialTransparencyPreviewRequested += (_, value) => MaterialTransparencyPreviewRequested?.Invoke(this, value);
        menu.CompanionPreviewRequested += (_, _) => CompanionPreviewRequested?.Invoke(this, EventArgs.Empty);
        menu.ExitRequested += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        trayIconStream = typeof(TrayController).Assembly.GetManifestResourceStream("CodexQuotaTaskbar.Host.Assets.QuotaIslandIcon.ico")
            ?? throw new InvalidOperationException("无法加载点岛应用图标。");
        trayIcon = new Icon(trayIconStream, SystemInformation.SmallIconSize);
        icon = new NotifyIcon
        {
            Text = "点岛",
            Icon = trayIcon,
            Visible = settings.TrayIconEnabled,
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
    internal event EventHandler? OpenDotChatRequested;
    internal event EventHandler? OpenSubscriptionLoginRequested;
    internal event EventHandler? CheckUpdatesRequested;
    internal event EventHandler<AppSettings>? SettingsChanged;
    internal event EventHandler<int>? MaterialTransparencyPreviewRequested;
    internal event EventHandler? CompanionPreviewRequested;
    internal event EventHandler? ExitRequested;

    internal AppSettings Settings => settings;

    internal void FlushPendingSettings() => menu.FlushMaterialTransparencyChange();

    internal void EnableSubscriptionDetails()
    {
        if (!settings.SubscriptionDetailsEnabled) Change(settings with { SubscriptionDetails = true });
    }

    internal void EnableDotRealtime()
    {
        if (!settings.DotRealtime) Change(settings with { DotRealtime = true });
    }

    internal void ShowContextMenu(QuotaCapsuleWindow? island = null)
    {
        var bounds = island?.AlignmentBounds;
        menu.ShowAt(Cursor.Position, 8, bounds is { } body
            ? new Rectangle(body.Left, body.Top, body.Width, body.Height) : null);
    }

    internal void ShowLowQuotaNotification()
    {
        if (!icon.Visible) return;
        icon.BalloonTipTitle = "点岛 · 额度较低";
        icon.BalloonTipText = "至少一个额度窗口的剩余量已低于提醒阈值。";
        icon.BalloonTipIcon = ToolTipIcon.Warning;
        icon.ShowBalloonTip(5000);
    }

    internal void ShowUpdateNotification(string title, string message, ToolTipIcon kind = ToolTipIcon.Info, bool showWhenTrayHidden = false)
    {
        if (!icon.Visible)
        {
            if (showWhenTrayHidden) hiddenUpdateResult(title, message, kind);
            return;
        }
        icon.BalloonTipTitle = title;
        icon.BalloonTipText = message;
        icon.BalloonTipIcon = kind;
        icon.ShowBalloonTip(5000);
    }

    private static void ShowHiddenUpdateResult(string title, string message, ToolTipIcon kind)
    {
        var image = kind switch
        {
            ToolTipIcon.Error => System.Windows.MessageBoxImage.Error,
            ToolTipIcon.Warning => System.Windows.MessageBoxImage.Warning,
            _ => System.Windows.MessageBoxImage.Information,
        };
        System.Windows.MessageBox.Show(message, title + " · 点岛", System.Windows.MessageBoxButton.OK, image);
    }

    private void Change(AppSettings value)
    {
        settings = value;
        icon.Visible = value.TrayIconEnabled;
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
        trayIconStream.Dispose();
        menu.Close();
    }
}
