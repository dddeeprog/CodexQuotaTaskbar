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
        InitializeCompanionMenu();
        normalGlassBackground = GlassBorder.Background;
        normalBackdropBackground = BackdropLayer.Background;
        materialSaveTimer.Tick += (_, _) => FlushMaterialTransparencyChange();
        MaterialTransparencySlider.ValueChanged += (_, _) => OnMaterialTransparencyChanged();
        IsVisibleChanged += (_, _) => { if (!IsVisible) { CloseSubmenus(); FlushMaterialTransparencyChange(); } };
        AppearanceButton.Click += (_, _) => ToggleSubmenu(true);
        PreferencesButton.Click += (_, _) => ToggleSubmenu(false);
        InitializeMenuInteraction();
        AppearanceRoot.PreviewKeyDown += OnSubmenuKeyDown;
        PreferencesRoot.PreviewKeyDown += OnSubmenuKeyDown;
        VersionText.Text = $"v{ProductVersion.Text}";
        RefreshButton.Click += (_, _) => InvokeAndHide(RefreshRequested);
        CheckUpdatesButton.Click += (_, _) => InvokeAndHide(CheckUpdatesRequested);
        OpenCodexButton.Click += (_, _) => InvokeAndHide(OpenCodexRequested);
        DotChatButton.Click += (_, _) => InvokeAndHide(OpenDotChatRequested);
        DotRealtimeButton.Click += (_, _) => RequestSettings(this.settings with { DotRealtime = !this.settings.DotRealtime });
        SubscriptionLoginButton.Click += (_, _) => InvokeAndHide(OpenSubscriptionLoginRequested);
        ExitButton.Click += (_, _) => InvokeAndHide(ExitRequested);
        ClassicStyleButton.Click += (_, _) => RequestSettings(this.settings with { UnifiedPanel = false });
        UnifiedStyleButton.Click += (_, _) => RequestSettings(this.settings with { UnifiedPanel = true });
        AllTaskbarsButton.Click += (_, _) => RequestSettings(this.settings with { ShowAllTaskbars = !this.settings.ShowAllTaskbars });
        TrayIconButton.Click += (_, _) => RequestSettings(this.settings with { ShowTrayIcon = !this.settings.TrayIconEnabled });
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
                if (appearanceOpen || preferencesOpen) CloseSubmenus();
                else Hide();
                eventArgs.Handled = true;
            }
        };
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        Closed += (_, _) =>
        {
            FlushMaterialTransparencyChange();
            CloseSubmenus();
            materialSaveTimer.Stop();
            SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        };
        ApplySettings(settings);
    }

    internal event EventHandler? RefreshRequested;
    internal event EventHandler? CheckUpdatesRequested;
    internal event EventHandler? OpenCodexRequested;
    internal event EventHandler? OpenDotChatRequested;
    internal event EventHandler? OpenSubscriptionLoginRequested;
    internal event EventHandler<AppSettings>? SettingsChangeRequested;
    internal event EventHandler<int>? MaterialTransparencyPreviewRequested;
    internal event EventHandler? ExitRequested;

    internal void ApplySettings(AppSettings value)
    {
        settings = value;
        ApplyCompanionMenu(value);
        ClassicStyleButton.Background = UnifiedStyleButton.Background = MediaBrushes.Transparent;
        StyleSummaryText.Text = value.UnifiedPanel ? "一体面板" : "经典气泡";
        ClassicStyleCheck.Visibility = value.UnifiedPanel ? Visibility.Hidden : Visibility.Visible;
        UnifiedStyleCheck.Visibility = value.UnifiedPanel ? Visibility.Visible : Visibility.Hidden;
        System.Windows.Automation.AutomationProperties.SetItemStatus(ClassicStyleButton, value.UnifiedPanel ? "未选中" : "已选中");
        System.Windows.Automation.AutomationProperties.SetItemStatus(UnifiedStyleButton, value.UnifiedPanel ? "已选中" : "未选中");
        applyingSettings = true;
        MaterialTransparencySlider.Value = value.EffectiveMaterialTransparencyPercent;
        MaterialTransparencyValue.Text = $"{value.EffectiveMaterialTransparencyPercent}%";
        applyingSettings = false;
        ApplyMaterialAppearance();
        ApplyCheck(AllTaskbarsButton, AllTaskbarsCheck, value.ShowAllTaskbars);
        ApplyCheck(TrayIconButton, TrayIconCheck, value.TrayIconEnabled);
        ApplyCheck(DotRealtimeButton, DotRealtimeCheck, value.DotRealtime);
        ApplyCheck(NotificationsButton, NotificationsCheck, value.LowQuotaNotifications);
        ApplyCheck(AutomaticUpdatesButton, AutomaticUpdatesCheck, value.AutomaticUpdatesEnabled);
        ApplyCheck(SubscriptionDetailsButton, SubscriptionDetailsCheck, value.SubscriptionDetailsEnabled);
        ApplyCheck(AutostartButton, AutostartCheck, value.StartWithWindows);
    }

    internal void ShowAt(System.Drawing.Point cursor, int anchorGap = 8, System.Drawing.Rectangle? island = null)
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        activationEstablished = false;
        menuCenteredOnIsland = island.HasValue;
        if (island is { } body)
        {
            var bodyCenter = new System.Drawing.Point(body.Left + body.Width / 2, body.Top + body.Height / 2);
            var work = System.Windows.Forms.Screen.FromPoint(bodyCenter).WorkingArea;
            menuOpensAbove = body.Top - work.Top >= work.Bottom - body.Bottom;
            cursor = new System.Drawing.Point(bodyCenter.X, menuOpensAbove ? body.Top : body.Bottom);
        }
        menuAnchor = cursor;
        menuAnchorGap = anchorGap;
        var area = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
        if (!menuCenteredOnIsland) menuOpensAbove = cursor.Y - area.Top >= area.Bottom - cursor.Y;
        Show();
        UpdateLayout();
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var dpi = Math.Max(96u, NativeMethods.GetDpiForWindow(handle));
        var workArea = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
        MaxHeight = Math.Max(1, (workArea.Height - 16) * 96d / dpi);
        if (menuCenteredOnIsland)
            MaxHeight = Math.Min(MaxHeight, Math.Max(1, ((menuOpensAbove ? cursor.Y - workArea.Top : workArea.Bottom - cursor.Y) - anchorGap - 8) * 96d / dpi));
        MenuScroller.MaxHeight = Math.Max(1, MaxHeight - 20);
        UpdateLayout();
        var width = checked((int)Math.Ceiling(ActualWidth * dpi / 96d));
        var height = checked((int)Math.Ceiling(ActualHeight * dpi / 96d));
        var bounds = CalculateAnchoredBounds(workArea, cursor, new System.Drawing.Size(width, height), menuOpensAbove, anchorGap, menuCenteredOnIsland);
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
        ApplySubmenuAppearance();
    }

    private void InvokeAndHide(EventHandler? handler)
    {
        Hide();
        handler?.Invoke(this, EventArgs.Empty);
    }

    private static void ApplyCheck(System.Windows.Controls.Button button, System.Windows.Shapes.Path check, bool enabled)
    {
        check.Visibility = enabled ? Visibility.Visible : Visibility.Hidden;
        System.Windows.Automation.AutomationProperties.SetItemStatus(button, enabled ? "已开启" : "已关闭");
    }

    private void OnSubmenuKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not (Key.Escape or Key.Left)) return;
        // Left still adjusts the slider; Escape always returns to the parent menu.
        if (e.Key == Key.Left && MaterialTransparencySlider.IsKeyboardFocusWithin) return;
        var target = appearanceOpen ? AppearanceButton : PreferencesButton;
        CloseSubmenus();
        target.Focus();
        e.Handled = true;
    }

    private void CloseSubmenus()
    {
        SetSection(true, false, animate: false);
        SetSection(false, false, animate: false);
        MenuHighlight.Opacity = 0;
        highlightedButton = null;
    }

    private void ApplySubmenuAppearance()
    {
        var opaque = CapsuleThemePolicy.Resolve(SystemParameters.HighContrast, SystemParameters.IsGlassEnabled) == CapsuleSurfaceMode.OpaqueSystem;
        foreach (var (root, glass) in new[]
        {
            (AppearanceRoot, AppearanceGlass),
            (PreferencesRoot, PreferencesGlass),
        })
        {
            glass.Background = MediaBrushes.Transparent;
            glass.BorderBrush = opaque ? System.Windows.SystemColors.ActiveBorderBrush : OverlayGlassMaterial.Border;
            foreach (var text in FindVisualChildren<System.Windows.Controls.TextBlock>(root))
                text.Foreground = opaque ? System.Windows.SystemColors.WindowTextBrush
                    : Equals(text.Tag, "secondary") ? Freeze("#B5B5BA") : Freeze("#F4F4F6");
            foreach (var path in FindVisualChildren<System.Windows.Shapes.Path>(root))
                path.Stroke = opaque ? System.Windows.SystemColors.WindowTextBrush : Freeze("#E9E9EE");
            // Inline sections share the owning surface and backdrop; no extra blur layer.
        }
    }

    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs) => ApplySystemAppearance();

    private void ApplySystemAppearance()
    {
        SurfaceShadow.Visibility = SystemParameters.HighContrast ? Visibility.Hidden : Visibility.Visible;
        var opaque = CapsuleThemePolicy.Resolve(SystemParameters.HighContrast, SystemParameters.IsGlassEnabled) == CapsuleSurfaceMode.OpaqueSystem;
        GlassBorder.Background = opaque ? System.Windows.SystemColors.WindowBrush : normalGlassBackground;
        GlassBorder.BorderBrush = SystemParameters.HighContrast ? System.Windows.SystemColors.ActiveBorderBrush : OverlayGlassMaterial.Border;
        BackdropLayer.Background = opaque ? System.Windows.SystemColors.WindowBrush : normalBackdropBackground;
        TitleText.Foreground = opaque ? System.Windows.SystemColors.WindowTextBrush : MediaBrushes.White;
        VersionText.Foreground = opaque
            ? System.Windows.SystemColors.GrayTextBrush
            : new SolidColorBrush(MediaColor.FromArgb(174, 255, 255, 255));
        VersionBadge.Background = opaque
            ? System.Windows.SystemColors.ControlBrush
            : MediaBrushes.Transparent;
        StyleSummaryText.Foreground = opaque ? System.Windows.SystemColors.WindowTextBrush : Freeze("#B5B5BA");
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
        MenuHighlight.Background = opaque ? System.Windows.SystemColors.ControlBrush : Freeze("#1FFFFFFF");
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
