using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Windows.Interop;
using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Host.Platform;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.UI;
using CodexQuotaTaskbar.Host.Update;

namespace CodexQuotaTaskbar.Host.Tray;

internal sealed class TrayController : IDisposable
{
    private readonly NotifyIcon icon;
    private readonly TrayMenuWindow menu;
    private readonly Icon trayIcon;
    private readonly Stream trayIconStream;
    private AppSettings settings;
    private readonly Action<string, string, ToolTipIcon> hiddenUpdateResult;
    private readonly bool restrictedDemo;
    private readonly Action<bool> setAutostart;
    private QuotaCapsuleWindow? menuIsland;
    private bool contextRequestInProgress;
    private UpdateStatusWindow? updateWindow;
    private readonly UpdatePresentationState updatePresentation = new();
    private Func<QuotaCapsuleWindow, IReadOnlyList<ScreenRect>>? contextAvoidanceProvider;

    internal TrayController(AppSettings settings, Action<string, string, ToolTipIcon>? hiddenUpdateResult = null,
        bool restrictedDemo = false, Action<bool>? setAutostart = null)
    {
        this.settings = settings;
        this.restrictedDemo = restrictedDemo;
        this.setAutostart = setAutostart ?? AutostartService.SetEnabled;
        this.hiddenUpdateResult = hiddenUpdateResult ?? ShowHiddenUpdateResult;
        menu = new TrayMenuWindow(settings, activateOnShow: !restrictedDemo);
        menu.PlacementBoundsChanged += bounds => MenuBoundsChanged?.Invoke(bounds);
        menu.IsVisibleChanged += (_, _) => { if (!menu.IsVisible) DetachMenuIsland(); };
        menu.RefreshRequested += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        menu.CheckUpdatesRequested += (_, _) => CheckUpdatesRequested?.Invoke(this, EventArgs.Empty);
        menu.OpenCodexRequested += (_, _) => OpenCodexRequested?.Invoke(this, EventArgs.Empty);
        menu.OpenSubscriptionLoginRequested += (_, _) => OpenSubscriptionLoginRequested?.Invoke(this, EventArgs.Empty);
        menu.SettingsChangeRequested += (_, value) => ChangeRequested(value);
        menu.MaterialTransparencyPreviewRequested += (_, value) => MaterialTransparencyPreviewRequested?.Invoke(this, value);
        menu.CoreTermsRequested += (_, _) => CoreTermsRequested?.Invoke(this, EventArgs.Empty);
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
    internal event EventHandler? OpenSubscriptionLoginRequested;
    internal event EventHandler? CheckUpdatesRequested;

    internal void BeginUpdatePresentation() => updatePresentation.Begin();
    internal void RestoreUpdatePresentation()
    {
        ShowUpdateWindow(updatePresentation.Restore(), manual: true);
        updateWindow?.Activate(); // Only an explicit user request may restore focus.
    }

    internal void PresentUpdate(UpdateStatus status, bool manual)
    {
        if (updatePresentation.Receive(status, manual || icon.Visible || updateWindow is not null)) ShowUpdateWindow(status, manual);
    }

    private void ShowUpdateWindow(UpdateStatus status, bool manual)
    {
        if (updateWindow is null)
        {
            updateWindow = new UpdateStatusWindow(settings.EffectiveMaterialTransparencyPercent) { ShowActivated = manual };
            updateWindow.RetryRequested += (_, _) => CheckUpdatesRequested?.Invoke(this, EventArgs.Empty);
            updateWindow.Closed += (_, _) => { updateWindow = null; updatePresentation.Dismiss(); };
        }
        updateWindow.Apply(status);
        if (!updateWindow.IsVisible) updateWindow.Show();
    }
    internal event EventHandler<AppSettings>? SettingsChanged;
    internal event EventHandler<int>? MaterialTransparencyPreviewRequested;
    internal event EventHandler? CoreTermsRequested;
    internal event EventHandler? ExitRequested;
    internal event Action<ScreenRect?>? MenuBoundsChanged;

    internal AppSettings Settings => settings;

    internal void SetCoreConsentStatus(Bongo.CoreConsentState state, bool termsReadable) => menu.ApplyCoreConsentStatus(state, termsReadable);

    internal void EnableBongoAfterCoreConsent()
    {
        // Explicit use of the component does not enable keyboard/mouse observation.
        if (!settings.EffectiveBongoCatVisible) Change(settings with { BongoCatVisible = true });
    }

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
        => RunContextRequest(() => ShowContextMenuCore(island));

    // Guard before detaching/reattaching owner events, not merely inside menu.ShowAt.
    internal bool RunContextRequest(Action request)
    {
        if (contextRequestInProgress || menu.IsPopupRequestInProgress) return false;
        contextRequestInProgress = true;
        try { request(); return true; }
        finally { contextRequestInProgress = false; }
    }

    private void ShowContextMenuCore(QuotaCapsuleWindow? island)
    {
        DetachMenuIsland();
        if ((!menu.IsVisible || menu.IsPopupClosing) && island is not null)
        {
            menuIsland = island;
            island.BoundsChanged += OnMenuIslandBoundsChanged;
            island.IsVisibleChanged += OnMenuIslandVisibilityChanged;
            island.Closed += OnMenuIslandClosed;
        }
        var bounds = island?.VisibleSurfaceBounds;
        var dpi = island is null ? 96u : NativeMethods.GetDpiForWindow(new WindowInteropHelper(island).Handle);
        menu.ShowAt(Cursor.Position, island is null ? 8 : 6, bounds is { } body
            ? new Rectangle(body.Left, body.Top, body.Width, body.Height) : null, dpi,
            island is null ? null : contextAvoidanceProvider?.Invoke(island));
        if (!menu.IsVisible) DetachMenuIsland();
    }

    internal void CloseContextMenu()
    {
        menu.HidePopupImmediately();
        DetachMenuIsland();
    }

    internal void SetContextAvoidanceProvider(Func<QuotaCapsuleWindow, IReadOnlyList<ScreenRect>> provider)
    {
        contextAvoidanceProvider = provider ?? throw new ArgumentNullException(nameof(provider));
        OnMenuIslandBoundsChanged(menuIsland, EventArgs.Empty);
    }

    private void OnMenuIslandBoundsChanged(object? sender, EventArgs args)
    {
        if (menuIsland is not { } island || !menu.IsVisible) return;
        var dpi = NativeMethods.GetDpiForWindow(new WindowInteropHelper(island).Handle);
        menu.UpdateIslandAnchor(island.VisibleSurfaceBounds, dpi, contextAvoidanceProvider?.Invoke(island));
    }

    private void OnMenuIslandVisibilityChanged(object? sender, System.Windows.DependencyPropertyChangedEventArgs args)
    {
        if (menuIsland is { IsVisible: false }) CloseContextMenu();
    }

    private void OnMenuIslandClosed(object? sender, EventArgs args) => CloseContextMenu();

    private void DetachMenuIsland()
    {
        if (menuIsland is not { } island) return;
        island.BoundsChanged -= OnMenuIslandBoundsChanged;
        island.IsVisibleChanged -= OnMenuIslandVisibilityChanged;
        island.Closed -= OnMenuIslandClosed;
        menuIsland = null;
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
        if (restrictedDemo && requested.StartWithWindows != settings.StartWithWindows)
        {
            menu.ApplySettings(settings);
            return;
        }
        if (requested.StartWithWindows == settings.StartWithWindows)
        {
            Change(requested);
            return;
        }

        try
        {
            setAutostart(requested.StartWithWindows);
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
        updateWindow?.Close();
        DetachMenuIsland();
        icon.Visible = false;
        icon.Dispose();
        trayIcon.Dispose();
        trayIconStream.Dispose();
        menu.Close();
    }
}
