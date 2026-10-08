using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.Platform;
using CodexQuotaTaskbar.Host.Settings;
using FrostedBlur = BlurredBackground.WPF.BlurredBackground;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;

namespace CodexQuotaTaskbar.Host.UI;

public partial class TrayMenuWindow : Window
{
    private static readonly MediaBrush ToggleOn = Freeze("#E9E9EE");
    private static readonly MediaBrush ToggleOff = Freeze("#35FFFFFF");
    private readonly MediaBrush normalGlassBackground;
    private readonly MediaBrush normalBackdropBackground;
    private bool activationEstablished;
    private bool blurReady;
    private bool applyingSettings;
    private bool materialTransparencyPending;
    private readonly DispatcherTimer materialSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private AppSettings settings;

    internal TrayMenuWindow(AppSettings settings)
    {
        this.settings = settings;
        InitializeComponent();
        normalGlassBackground = GlassBorder.Background;
        normalBackdropBackground = BackdropLayer.Background;
        materialSaveTimer.Tick += (_, _) => FlushMaterialTransparencyChange();
        MaterialTransparencySlider.ValueChanged += (_, _) => OnMaterialTransparencyChanged();
        IsVisibleChanged += (_, _) => { if (!IsVisible) FlushMaterialTransparencyChange(); };
        VersionText.Text = $"v{ProductVersion.Text}";
        RefreshButton.Click += (_, _) => InvokeAndHide(RefreshRequested);
        CheckUpdatesButton.Click += (_, _) => InvokeAndHide(CheckUpdatesRequested);
        OpenCodexButton.Click += (_, _) => InvokeAndHide(OpenCodexRequested);
        SubscriptionLoginButton.Click += (_, _) => InvokeAndHide(OpenSubscriptionLoginRequested);
        ExitButton.Click += (_, _) => InvokeAndHide(ExitRequested);
        AllTaskbarsButton.Click += (_, _) => RequestSettings(this.settings with { ShowAllTaskbars = !this.settings.ShowAllTaskbars });
        NotificationsButton.Click += (_, _) => RequestSettings(this.settings with { LowQuotaNotifications = !this.settings.LowQuotaNotifications });
        AutomaticUpdatesButton.Click += (_, _) => RequestSettings(this.settings with { AutomaticUpdates = !this.settings.AutomaticUpdatesEnabled });
        SubscriptionDetailsButton.Click += (_, _) => RequestSettings(this.settings with { SubscriptionDetails = !this.settings.SubscriptionDetailsEnabled });
        AutostartButton.Click += (_, _) => RequestSettings(this.settings with { StartWithWindows = !this.settings.StartWithWindows });
        Loaded += (_, _) =>
        {
            blurReady = true;
            ApplySystemAppearance();
        };
        Activated += (_, _) => activationEstablished = true;
        Deactivated += (_, _) =>
        {
            if (activationEstablished)
            {
                Hide();
            }
        };
        KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == Key.Escape)
            {
                Hide();
            }
        };
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        Closed += (_, _) =>
        {
            FlushMaterialTransparencyChange();
            materialSaveTimer.Stop();
            SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        };
        ApplySettings(settings);
    }

    internal event EventHandler? RefreshRequested;
    internal event EventHandler? CheckUpdatesRequested;
    internal event EventHandler? OpenCodexRequested;
    internal event EventHandler? OpenSubscriptionLoginRequested;
    internal event EventHandler<AppSettings>? SettingsChangeRequested;
    internal event EventHandler<int>? MaterialTransparencyPreviewRequested;
    internal event EventHandler? ExitRequested;

    internal void ApplySettings(AppSettings value)
    {
        settings = value;
        applyingSettings = true;
        MaterialTransparencySlider.Value = value.EffectiveMaterialTransparencyPercent;
        MaterialTransparencyValue.Text = $"{value.EffectiveMaterialTransparencyPercent}%";
        applyingSettings = false;
        ApplyMaterialAppearance();
        ApplyToggle(AllTaskbarsTrack, AllTaskbarsKnob, value.ShowAllTaskbars);
        ApplyToggle(NotificationsTrack, NotificationsKnob, value.LowQuotaNotifications);
        ApplyToggle(AutomaticUpdatesTrack, AutomaticUpdatesKnob, value.AutomaticUpdatesEnabled);
        ApplyToggle(SubscriptionDetailsTrack, SubscriptionDetailsKnob, value.SubscriptionDetailsEnabled);
        ApplyToggle(AutostartTrack, AutostartKnob, value.StartWithWindows);
    }

    internal void ShowAt(System.Drawing.Point cursor, int anchorGap = 8)
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        activationEstablished = false;
        Show();
        UpdateLayout();
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var dpi = Math.Max(96u, NativeMethods.GetDpiForWindow(handle));
        var workArea = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
        MaxHeight = Math.Max(1, (workArea.Height - 16) * 96d / dpi);
        UpdateLayout();
        var width = checked((int)Math.Ceiling(ActualWidth * dpi / 96d));
        var height = checked((int)Math.Ceiling(ActualHeight * dpi / 96d));
        var bounds = CalculateBounds(workArea, cursor, new System.Drawing.Size(width, height), anchorGap);
        _ = NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost, bounds.X, bounds.Y, bounds.Width, bounds.Height, NativeMethods.SwpShowWindow);
        Activate();
        Focus();
    }

    internal static System.Drawing.Rectangle CalculateBounds(System.Drawing.Rectangle workArea, System.Drawing.Point cursor, System.Drawing.Size menuSize, int anchorGap = 8)
    {
        const int screenMargin = 8;
        menuSize = new System.Drawing.Size(Math.Min(menuSize.Width, Math.Max(1, workArea.Width - 2 * screenMargin)),
            Math.Min(menuSize.Height, Math.Max(1, workArea.Height - 2 * screenMargin)));
        var left = Math.Clamp(cursor.X - menuSize.Width + 18, workArea.Left + screenMargin, workArea.Right - menuSize.Width - screenMargin);
        var above = cursor.Y - menuSize.Height - anchorGap;
        var top = above >= workArea.Top + screenMargin ? above : cursor.Y + anchorGap;
        top = Math.Clamp(top, workArea.Top + screenMargin, workArea.Bottom - menuSize.Height - screenMargin);
        return new System.Drawing.Rectangle(left, top, menuSize.Width, menuSize.Height);
    }

    private void RequestSettings(AppSettings value)
    {
        FlushMaterialTransparencyChange();
        ApplySettings(value);
        SettingsChangeRequested?.Invoke(this, value);
    }

    private void OnMaterialTransparencyChanged()
    {
        if (applyingSettings) return;
        var value = (int)Math.Round(MaterialTransparencySlider.Value);
        settings = settings with { MaterialTransparencyPercent = value };
        MaterialTransparencyValue.Text = $"{value}%";
        ApplyMaterialAppearance();
        MaterialTransparencyPreviewRequested?.Invoke(this, value);
        materialTransparencyPending = true;
        materialSaveTimer.Stop();
        materialSaveTimer.Start();
    }

    internal void FlushMaterialTransparencyChange()
    {
        materialSaveTimer.Stop();
        if (!materialTransparencyPending) return;
        materialTransparencyPending = false;
        SettingsChangeRequested?.Invoke(this, settings);
    }

    private void ApplyMaterialAppearance()
    {
        var opaque = CapsuleThemePolicy.Resolve(SystemParameters.HighContrast, SystemParameters.IsGlassEnabled) == CapsuleSurfaceMode.OpaqueSystem;
        GlassBorder.Background = opaque ? System.Windows.SystemColors.WindowBrush
            : OverlayGlassMaterial.WithTransparency(normalGlassBackground, settings.EffectiveMaterialTransparencyPercent);
        MaterialTransparencySlider.IsEnabled = !opaque;
        MaterialTransparencySlider.Foreground = opaque ? System.Windows.SystemColors.WindowTextBrush : ToggleOn;
        MaterialTransparencySlider.Background = opaque ? System.Windows.SystemColors.ControlDarkBrush : ToggleOff;
        MaterialTransparencyLabel.Foreground = MaterialTransparencyValue.Foreground = opaque
            ? System.Windows.SystemColors.WindowTextBrush : MediaBrushes.White;
        MaterialTransparencyHint.Foreground = opaque ? System.Windows.SystemColors.WindowTextBrush
            : new SolidColorBrush(MediaColor.FromArgb(184, 255, 255, 255));
        MaterialTransparencyHint.Text = opaque ? "系统已关闭透明效果，设置仍会保留" : "0% 不透明 · 100% 全透明";
    }

    private void InvokeAndHide(EventHandler? handler)
    {
        Hide();
        handler?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyToggle(System.Windows.Controls.Border track, Ellipse knob, bool enabled)
    {
        var opaque = CapsuleThemePolicy.Resolve(SystemParameters.HighContrast, SystemParameters.IsGlassEnabled) == CapsuleSurfaceMode.OpaqueSystem;
        track.Background = opaque
            ? enabled ? System.Windows.SystemColors.HighlightBrush : System.Windows.SystemColors.ControlBrush
            : enabled ? ToggleOn : ToggleOff;
        knob.Fill = opaque
            ? enabled ? System.Windows.SystemColors.HighlightTextBrush : System.Windows.SystemColors.ControlTextBrush
            : enabled ? new SolidColorBrush(MediaColor.FromRgb(25, 25, 27)) : new SolidColorBrush(MediaColor.FromRgb(242, 242, 247));
        knob.HorizontalAlignment = enabled ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Left;
    }

    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs) => ApplySystemAppearance();

    private void ApplySystemAppearance()
    {
        var opaque = CapsuleThemePolicy.Resolve(SystemParameters.HighContrast, SystemParameters.IsGlassEnabled) == CapsuleSurfaceMode.OpaqueSystem;
        GlassBorder.Background = opaque ? System.Windows.SystemColors.WindowBrush : normalGlassBackground;
        GlassBorder.BorderBrush = opaque ? System.Windows.SystemColors.ActiveBorderBrush : OverlayGlassMaterial.Border;
        BackdropLayer.Background = opaque ? System.Windows.SystemColors.WindowBrush : normalBackdropBackground;
        TitleText.Foreground = opaque ? System.Windows.SystemColors.WindowTextBrush : MediaBrushes.White;
        RunningText.Foreground = VersionText.Foreground = opaque
            ? System.Windows.SystemColors.GrayTextBrush
            : new SolidColorBrush(MediaColor.FromArgb(174, 255, 255, 255));
        VersionBadge.Background = opaque
            ? System.Windows.SystemColors.ControlBrush
            : new SolidColorBrush(MediaColor.FromArgb(20, 255, 255, 255));
        foreach (var button in FindVisualChildren<System.Windows.Controls.Button>(this))
        {
            button.Foreground = opaque ? System.Windows.SystemColors.WindowTextBrush : button == ExitButton
                ? new SolidColorBrush(MediaColor.FromRgb(255, 123, 134))
                : new SolidColorBrush(MediaColor.FromArgb(244, 255, 255, 255));
        }
        foreach (var path in FindVisualChildren<System.Windows.Shapes.Path>(this))
        {
            path.Stroke = opaque ? System.Windows.SystemColors.WindowTextBrush : path == ExitIcon
                ? new SolidColorBrush(MediaColor.FromRgb(255, 123, 134))
                : new SolidColorBrush(MediaColor.FromArgb(200, 255, 255, 255));
        }
        ApplySettings(settings);
        SetBlurEnabled(!opaque);
    }

    private void SetBlurEnabled(bool enabled)
    {
        if (blurReady && FrostedBlur.GetEnableBlur(BackdropLayer) != enabled)
        {
            FrostedBlur.SetBlurRadius(BackdropLayer, OverlayGlassMaterial.BlurRadius);
            FrostedBlur.SetMerging(BackdropLayer, OverlayGlassMaterial.Merging);
            FrostedBlur.SetDpi(BackdropLayer, 48);
            FrostedBlur.SetEnableBlur(BackdropLayer, enabled);
        }
    }

    private static MediaBrush Freeze(string color)
    {
        var brush = new SolidColorBrush((MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private static IEnumerable<T> FindVisualChildren<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }
            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
