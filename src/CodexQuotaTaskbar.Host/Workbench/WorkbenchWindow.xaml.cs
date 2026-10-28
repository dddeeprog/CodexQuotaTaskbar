using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace CodexQuotaTaskbar.Host.Workbench;

internal partial class WorkbenchWindow : Window
{
    private bool closing;
    private int ownedInteraction;
    private long activation;
    internal bool CaptureActive { get; private set; }
    internal Func<bool> CaptureProbe { get; set; } = WorkbenchCoordinator.IsCaptured;
    internal bool Pinned { get; private set; }
    internal bool CanStartInteraction => !closing && IsVisible && !CaptureActive && !CaptureProbe();
    internal event Action? PreferencesChanged;
    internal event Action? HotkeyChangeRequested;
    internal event Action? DirectoryRequested;
    internal event Action? RepositoryRequested;
    internal event Action? RefreshRequested;
    internal event Action<string>? ViewRequested;

    internal WorkbenchWindow(WorkbenchSettings settings)
    {
        InitializeComponent();
        Width = settings.Width; Height = settings.Height; Pinned = settings.Pinned; Topmost = Pinned;
        InitializePreviewLayout(settings);
        PinButton.Content = Pinned ? "置顶：开" : "置顶：关";
        PinButton.ToolTip = "切换置顶；开启后工作台保持显示";
        HotkeyButton.ToolTip = $"修改工作台快捷键（当前：{settings.Hotkey}）";
        PinButton.Click += (_, _) => SetPinned(!Pinned);
        HotkeyButton.Click += (_, _) => { if (CanStartInteraction) HotkeyChangeRequested?.Invoke(); };
        ChooseDirectoryButton.Click += (_, _) => { if (CanStartInteraction) DirectoryRequested?.Invoke(); };
        ChooseRepositoryButton.Click += (_, _) => { if (CanStartInteraction) RepositoryRequested?.Invoke(); };
        RefreshButton.Click += (_, _) => RefreshRequested?.Invoke();
        CloseWorkbenchButton.Click += (_, _) => HideSafely();
        MinimizeButton.Click += (_, _) => { if (CanStartInteraction) SystemCommands.MinimizeWindow(this); };
        FilesButton.Click += (_, _) => ViewRequested?.Invoke("files");
        SearchButton.Click += (_, _) => ViewRequested?.Invoke("search");
        GitButton.Click += (_, _) => ViewRequested?.Invoke("git");
        Activated += (_, _) => activation++;
        Deactivated += (_, _) => QueueDeactivation();
        Closing += OnClosing;
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape && ownedInteraction == 0 && !HasOpenContextMenu())
            { HideSafely(); args.Handled = true; }
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) { ResetPreview(); PreferencesChanged?.Invoke(); } };
        ApplyAppearance();
        SystemParameters.StaticPropertyChanged += SystemAppearanceChanged;
    }

    internal static bool ShouldAutoHide(long requested, long current, bool visible, bool active,
        bool pinned, bool captured, bool interacting) => requested == current && visible && !active && !pinned && !captured && !interacting;
    internal bool CanAutoHide(long generation, bool active) => !closing && ShouldAutoHide(generation, activation,
        IsVisible, active, Pinned, CaptureActive, ownedInteraction != 0 || HasOpenContextMenu());
    private void QueueDeactivation()
    {
        var generation = activation;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (IsVisible) CaptureActive = CaptureProbe();
            if (CanAutoHide(generation, IsActive)) HideSafely();
        });
    }
    private bool HasOpenContextMenu() => ContextMenuService.GetContextMenu(this)?.IsOpen == true
        || Mouse.Captured is System.Windows.Controls.Primitives.MenuBase
        || OwnedWindows.Cast<Window>().Any(w => w.IsVisible);
    internal void SetCaptureActive(bool value)
    {
        var previous = CaptureActive; CaptureActive = value;
        if (previous && !value && IsVisible && !IsActive) QueueDeactivation();
    }
    internal void SetPinned(bool value)
    {
        if (CaptureActive || (IsVisible && CaptureProbe())) return;
        Pinned = value; Topmost = value; PinButton.Content = value ? "置顶：开" : "置顶：关";
        PreferencesChanged?.Invoke();
    }
    internal void HideSafely() { if (!CaptureActive && !closing && (!IsVisible || !CaptureProbe())) { Hide(); ResetPreview(); } }
    internal IDisposable ProtectInteraction()
    {
        ownedInteraction++;
        return new InteractionScope(() => { ownedInteraction--; if (IsVisible && !IsActive) QueueDeactivation(); });
    }
    internal string? Prompt(string title, string initial = "")
    {
        if (!CanStartInteraction) return null;
        using var scope = ProtectInteraction();
        var dialog = new Window { Owner = this, Title = title, Width = 430, Height = 210,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            Background = Background, Foreground = Foreground, FontFamily = FontFamily, ShowInTaskbar = false };
        var input = new TextBox { Text = initial, Margin = new Thickness(0, 0, 0, 12) };
        var confirm = new Button { Content = "确定", IsDefault = true, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Padding = new Thickness(18, 5, 18, 5) };
        confirm.Click += (_, _) => { if (CanStartInteraction) dialog.DialogResult = true; };
        var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(18, 5, 18, 5) };
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        actions.Children.Add(cancel); actions.Children.Add(confirm);
        var panel = new StackPanel { Margin = new Thickness(18) }; panel.Children.Add(input); panel.Children.Add(actions); StyleDialog(dialog, panel);
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text : null;
    }
    internal void SetStatus(string text)
    { StatusLabel.Text = text; StatusBorder.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible; }
    internal void SetViewMode(string mode)
    { FilesButton.IsChecked = mode == "files"; SearchButton.IsChecked = mode == "search"; GitButton.IsChecked = mode == "git"; }
    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (closing) return;
        args.Cancel = true; HideSafely();
    }
    internal void CloseForShutdown()
    { closing = true; SystemParameters.StaticPropertyChanged -= SystemAppearanceChanged; Close(); }
    private sealed class InteractionScope(Action dispose) : IDisposable
    { private Action? callback = dispose; public void Dispose() => Interlocked.Exchange(ref callback, null)?.Invoke(); }
}
