using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Host.Platform;

namespace CodexQuotaTaskbar.Host.Workbench;

internal sealed class WorkbenchCoordinator : IAsyncDisposable
{
    private WorkbenchSettings settings;
    private readonly Action<WorkbenchSettings> save;
    private readonly WorkbenchHotkey hotkey = new();
    private readonly DispatcherTimer captureTimer;
    private WorkbenchWindow? window;
    private bool disposed;
    internal event Action<WorkbenchWindow>? WindowCreated;
    internal Task PendingWrite { get; set; } = Task.CompletedTask;

    internal WorkbenchCoordinator(WorkbenchSettings settings, Action<WorkbenchSettings> save, bool enableShortcut = true)
    {
        this.settings = settings.Normalize(); this.save = save;
        hotkey.Pressed += Toggle;
        if (enableShortcut) hotkey.Register(this.settings.Hotkey);
        captureTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(750) };
        captureTimer.Tick += (_, _) => window?.SetCaptureActive(IsCaptured());
    }
    internal static bool IsCaptured()
    {
        var topology = new TaskbarTopologySource();
        return topology.Capture().Any(anchor => TaskbarTopologySource.GetForegroundDisposition(anchor) == ForegroundDisposition.CaptureOverlay);
    }
    internal void Toggle()
    {
        if (disposed || IsCaptured()) return;
        if (window?.IsVisible == true) { window.HideSafely(); return; }
        Open();
    }
    internal void Open()
    {
        if (disposed || IsCaptured()) return;
        if (window is null)
        {
            window = new WorkbenchWindow(settings);
            window.PreferencesChanged += Persist;
            window.HotkeyChangeRequested += ChangeHotkey;
            window.IsVisibleChanged += (_, _) => { if (window.IsVisible) captureTimer.Start(); else captureTimer.Stop(); };
            window.Loaded += (_, _) => ClampToScreen(window, settings);
            WindowCreated?.Invoke(window);
        }
        window.SetCaptureActive(false);
        window.ResetPreview();
        window.SetStatus(hotkey.Error ?? "");
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }
    private static void ClampToScreen(Window view, WorkbenchSettings settings)
    {
        var dpi = VisualTreeHelper.GetDpi(view);
        var point = settings.Left is { } x && settings.Top is { } y
            ? new System.Drawing.Point((int)(x * dpi.DpiScaleX), (int)(y * dpi.DpiScaleY))
            : System.Windows.Forms.Cursor.Position;
        var area = System.Windows.Forms.Screen.FromPoint(point).WorkingArea;
        var bounds = new Rect(area.Left / dpi.DpiScaleX, area.Top / dpi.DpiScaleY, area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY);
        view.MinWidth = Math.Min(view.MinWidth, bounds.Width); view.MinHeight = Math.Min(view.MinHeight, bounds.Height);
        view.Width = Math.Min(view.Width, bounds.Width); view.Height = Math.Min(view.Height, bounds.Height);
        view.Left = Math.Clamp(settings.Left ?? bounds.Left + (bounds.Width - view.Width) / 2, bounds.Left, Math.Max(bounds.Left, bounds.Right - view.Width));
        view.Top = Math.Clamp(settings.Top ?? bounds.Top + (bounds.Height - view.Height) / 2, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - view.Height));
    }
    internal void UpdateSelection(string? directory, string? repository)
    { if (settings.Directory == directory && settings.Repository == repository) return; settings = settings with { Directory = directory, Repository = repository }; Persist(); }
    private void Persist()
    {
        if (disposed || window is null) return;
        var bounds = window.WindowState == WindowState.Normal ? new Rect(window.Left, window.Top, window.Width, window.Height) : window.RestoreBounds;
        settings = window.CaptureLayout(settings with { Pinned = window.Pinned, Left = bounds.Left, Top = bounds.Top }).Normalize();
        save(settings);
    }
    private void ChangeHotkey()
    {
        var requested = window?.Prompt("修改工作台快捷键", settings.Hotkey);
        if (requested is null || window is null) return;
        if (!WorkbenchHotkey.TryParse(requested, out _, out _)) { window.SetStatus("快捷键格式无效，例如 Ctrl+Alt+G。原快捷键保持不变。"); return; }
        hotkey.Register(requested);
        settings = settings with { Hotkey = requested };
        window.HotkeyButton.ToolTip = $"修改工作台快捷键（当前：{requested}）";
        Persist(); window.SetStatus(hotkey.Error ?? $"快捷键已更新：{requested}");
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        await PendingWrite;
        Persist(); disposed = true; captureTimer.Stop(); hotkey.Dispose(); window?.CloseForShutdown();
    }
}
