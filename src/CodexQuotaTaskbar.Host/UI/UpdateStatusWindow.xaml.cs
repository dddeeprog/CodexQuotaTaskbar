using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CodexQuotaTaskbar.Host.Update;
using CodexQuotaTaskbar.Core.Overlay;
using FrostedBlur = BlurredBackground.WPF.BlurredBackground;
using SystemColors = System.Windows.SystemColors;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace CodexQuotaTaskbar.Host.UI;

internal partial class UpdateStatusWindow : Window
{
    private readonly int transparency;
    private bool blurReady;
    internal event EventHandler? RetryRequested;
    internal UpdateStatus? Status { get; private set; }

    internal UpdateStatusWindow(int transparency = 0)
    {
        this.transparency = transparency;
        InitializeComponent();
        DismissButton.Click += (_, _) => Close();
        RetryButton.Click += (_, _) => { if (Status?.CanRetry == true) RetryRequested?.Invoke(this, EventArgs.Empty); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Loaded += (_, _) => { blurReady = true; ApplyAppearance(); };
        SystemParameters.StaticPropertyChanged += OnAppearanceChanged;
        Closed += (_, _) => { SystemParameters.StaticPropertyChanged -= OnAppearanceChanged; };
        ApplyAppearance();
    }

    internal void Apply(UpdateStatus status)
    {
        Status = status;
        StatusTitle.Text = status.Title; MessageText.Text = status.Message;
        DetailText.Text = status.Detail; DetailText.Visibility = string.IsNullOrEmpty(status.Detail) ? Visibility.Collapsed : Visibility.Visible;
        Progress.Visibility = status.Busy ? Visibility.Visible : Visibility.Collapsed;
        Progress.IsIndeterminate = status.Busy && status.Percent is null && IsLoaded && SystemParameters.ClientAreaAnimation;
        Progress.Value = Math.Clamp(status.Percent ?? 0, 0, 100);
        ProgressText.Text = status.Percent is { } percent ? $"{percent:0}%" : "关闭窗口不会取消后台更新";
        ProgressText.Visibility = status.Busy ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = status.CanRetry ? Visibility.Visible : Visibility.Collapsed;
        DismissButton.Content = status.Busy ? "收起" : "关闭";
        System.Windows.Automation.AutomationProperties.SetName(this, status.Title + "。" + status.Message);
    }

    private void OnAppearanceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => ApplyAppearance();
    private void ApplyAppearance()
    {
        var opaque = CapsuleThemePolicy.Resolve(SystemParameters.HighContrast, SystemParameters.IsGlassEnabled) == CapsuleSurfaceMode.OpaqueSystem;
        Surface.Background = opaque ? SystemColors.WindowBrush : OverlayGlassMaterial.CreateSurfaceBrush(transparency);
        StatusTitle.Foreground = MessageText.Foreground = opaque ? SystemColors.WindowTextBrush : Brushes.WhiteSmoke;
        ProgressText.Foreground = opaque ? SystemColors.WindowTextBrush : Brushes.LightGray;
        DetailText.Foreground = ProgressText.Foreground;
        DismissButton.Background = opaque ? SystemColors.ControlBrush : new SolidColorBrush(Color.FromArgb(24, 255, 255, 255));
        DismissButton.Foreground = opaque ? SystemColors.ControlTextBrush : Brushes.White;
        RetryButton.Background = opaque ? SystemColors.HighlightBrush : Brushes.WhiteSmoke;
        RetryButton.Foreground = opaque ? SystemColors.HighlightTextBrush : Brushes.Black;
        if (blurReady)
        {
            FrostedBlur.SetBlurRadius(BackdropLayer, OverlayGlassMaterial.BlurRadius);
            FrostedBlur.SetMerging(BackdropLayer, OverlayGlassMaterial.Merging);
            FrostedBlur.SetDpi(BackdropLayer, 48);
            FrostedBlur.SetEnableBlur(BackdropLayer, !opaque);
        }
        if (Status is { } status) Apply(status);
    }
}
