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
    // Retain the compatibility event while the workbench entry point is disabled.
#pragma warning disable CS0067
    internal event EventHandler? OpenWorkbenchRequested;
#pragma warning restore CS0067
    private static readonly MediaBrush ToggleOn = Freeze("#E9E9EE");
    private static readonly MediaBrush ToggleOff = Freeze("#35FFFFFF");
    private readonly MediaBrush normalGlassBackground;
    private readonly MediaBrush normalBackdropBackground;
    private bool activationEstablished;
    private bool blurReady;
    private bool applyingSettings;
    private bool materialTransparencyPending;
    private readonly bool activateOnShow;
    private readonly DispatcherTimer materialSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private AppSettings settings;

    internal TrayMenuWindow(AppSettings settings, bool activateOnShow = true)
    {
        this.settings = settings;
        this.activateOnShow = activateOnShow;
        InitializeComponent();
        InitializeCompanionMenu();
        InitializePopupMotion();
        normalGlassBackground = GlassBorder.Background;
        normalBackdropBackground = BackdropLayer.Background;
        materialSaveTimer.Tick += (_, _) => FlushMaterialTransparencyChange();
        MaterialTransparencySlider.ValueChanged += (_, _) => OnMaterialTransparencyChanged();
        IsVisibleChanged += (_, _) =>
        {
            OverlayLifecycleDiagnostics.Local.Window(this, OverlayLifecycleEvent.MenuVisibility);
            if (IsVisible)
            {
                if (!preparingPopup) PlayPopupEntrance(popupEntranceOffset);
            }
            else
            {
                CancelPopupMotion();
                CancelPageTransition();
                ResetMenuScrollbar();
                CloseSubmenus(); FlushMaterialTransparencyChange();
                PublishPlacement(null);
            }
        };
        AppearanceButton.Click += (_, _) => ToggleSubmenu(true);
        PreferencesButton.Click += (_, _) => ToggleSubmenu(false);
        PageBackButton.Click += (_, _) =>
        {
            var target = appearanceOpen ? AppearanceButton : PreferencesButton;
            ReturnToPrimary();
            FocusWhenPageReady(target);
        };
        InitializeMenuInteraction();
        VersionText.Text = $"v{ProductVersion.Text}";
        RefreshButton.Click += (_, _) => InvokeAndHide(RefreshRequested);
        CheckUpdatesButton.Click += (_, _) => InvokeAndHide(CheckUpdatesRequested);
        OpenCodexButton.Click += (_, _) => InvokeAndHide(OpenCodexRequested);
        DotRealtimeButton.Click += (_, _) => RequestSettings(this.settings with { DotRealtime = !this.settings.DotRealtime });
        SubscriptionLoginButton.Click += (_, _) => InvokeAndHide(OpenSubscriptionLoginRequested);
        ExportDiagnosticsButton.Click += (_, _) => InvokeAndHide(ExportDiagnosticsRequested);
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
            StartPendingPopupEntrance();
        };
        Activated += (_, _) => activationEstablished = true;
        Deactivated += (_, _) =>
        {
            OverlayLifecycleDiagnostics.Local.Window(this, OverlayLifecycleEvent.MenuDeactivated);
            if (activationEstablished) RequestHide();
        };
        KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == Key.Escape)
            {
                if (appearanceOpen || preferencesOpen) CloseSubmenus();
                else RequestHide();
                eventArgs.Handled = true;
            }
        };
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        Closed += (_, _) =>
        {
            DisposePopupMotion();
            pageTransitionClosed = true;
            CancelPageTransition();
            FlushMaterialTransparencyChange();
            CloseSubmenus();
            materialSaveTimer.Stop();
            DisposeMenuScrolling();
            SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        };
        ApplySettings(settings);
        UpdatePageConstraints();
    }

    internal event EventHandler? RefreshRequested;
    internal event EventHandler? CheckUpdatesRequested;
    internal event EventHandler? OpenCodexRequested;
    internal event EventHandler? OpenSubscriptionLoginRequested;
    internal event EventHandler<AppSettings>? SettingsChangeRequested;
    internal event EventHandler<int>? MaterialTransparencyPreviewRequested;
    internal event EventHandler? ExportDiagnosticsRequested;
    internal event EventHandler? ExitRequested;

    internal void ApplySettings(AppSettings value)
    {
        settings = value;
        RefreshPopupMotionPolicy();
        if (value.ReduceCompanionMotion || !SystemParameters.ClientAreaAnimation)
        {
            CancelPageTransition();
            NormalizeScrollbarMotion();
        }
        ApplyCompanionMenu(value);
        ClassicStyleButton.Background = UnifiedStyleButton.Background = MediaBrushes.Transparent;
        ClassicStyleButton.Tag = value.UnifiedPanel ? null : "Selected";
        UnifiedStyleButton.Tag = value.UnifiedPanel ? "Selected" : null;
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

    internal void ShowAt(System.Drawing.Point cursor, int anchorGap = 8, System.Drawing.Rectangle? island = null, uint islandDpi = 96,
        IReadOnlyList<CodexQuotaTaskbar.Core.Overlay.ScreenRect>? avoidance = null)
    {
        RunPopupRequest(() =>
        {
            OverlayLifecycleDiagnostics.Local.Window(this, OverlayLifecycleEvent.MenuToggle);
            // A second click during dismissal completes that dismissal; it must
            // not reverse the old close animation into an unsolicited reopen.
            if (IsPopupClosing) { HidePopupImmediately(); return; }
            if (IsVisible) { RequestHide(); return; }
            var generation = PreparePopupOpen();
            ShowPopupAt(cursor, anchorGap, island, islandDpi, avoidance, generation);
        });
    }

    internal bool IsPopupRequestInProgress => preparingPopup;
    internal bool RunPopupRequest(Action request)
    {
        if (popupMotionClosed || preparingPopup) return false;
        preparingPopup = true;
        try { request(); return true; }
        finally { preparingPopup = false; }
    }

    private void ShowPopupAt(System.Drawing.Point cursor, int anchorGap, System.Drawing.Rectangle? island,
        uint islandDpi, IReadOnlyList<CodexQuotaTaskbar.Core.Overlay.ScreenRect>? avoidance, long generation)
    {
        if (!TrySetPageHeightLimit(MenuPageHeightLimitDip)) return;
        ResetPrimaryPageForPopup();
        if (!IsVisible) activationEstablished = false;
        PlacementUnavailable = false;
        if (island is { } fullIsland)
        {
            menuIslandBounds = new(fullIsland.Left, fullIsland.Top, fullIsland.Right, fullIsland.Bottom);
            menuIslandDpi = islandDpi is >= 48 and <= 768 ? islandDpi : 96;
            menuIslandAvoidance = avoidance?.ToArray() ?? [];
            menuAnchorGap = anchorGap;
            menuCenteredOnIsland = true;
            RepositionIslandMenu(show: true, generation);
            return;
        }
        menuIslandBounds = null;
        menuIslandAvoidance = [];
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
        var handle = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        var dpi = Math.Max(96u, NativeMethods.GetDpiForWindow(handle));
        var workArea = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
        var maximumHeight = Math.Min(MenuPageHeightLimitDip, Math.Max(1, (workArea.Height - 16) * 96d / dpi));
        if (menuCenteredOnIsland)
            maximumHeight = Math.Min(maximumHeight, Math.Max(1, ((menuOpensAbove ? cursor.Y - workArea.Top : workArea.Bottom - cursor.Y) - anchorGap - 8) * 96d / dpi));
        if (!TrySetPageHeightLimit(maximumHeight)) { Hide(); PlacementUnavailable = true; return; }
        var content = (FrameworkElement)Content;
        content.Measure(new System.Windows.Size(Width, maximumHeight));
        var heightDip = Math.Min(content.DesiredSize.Height, maximumHeight);
        content.Arrange(new Rect(0, 0, Width, heightDip));
        var width = checked((int)Math.Ceiling(Width * dpi / 96d));
        var height = checked((int)Math.Ceiling(heightDip * dpi / 96d));
        var bounds = CalculateAnchoredBounds(workArea, cursor, new System.Drawing.Size(width, height), menuOpensAbove, anchorGap, menuCenteredOnIsland);
        _ = NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost, bounds.X, bounds.Y, bounds.Width, bounds.Height, NativeMethods.SwpNoActivate);
        SetPopupEntranceAnchor(new(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
        if (!IsCurrentPopupOpenRequest(generation)) return;
        if (!IsVisible) Show();
        UpdateLayout();
        if (!IsCurrentPopupOpen(generation)) return;
        _ = NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost, bounds.X, bounds.Y, bounds.Width, bounds.Height,
            NativeMethods.SwpNoActivate);
        PublishPlacement(new(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
        if (!IsCurrentPopupOpen(generation)) return;
        PlayPopupEntrance(popupEntranceOffset);
        if (activateOnShow) { Activate(); Focus(); }
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
        button.Tag = enabled ? "On" : null;
        System.Windows.Automation.AutomationProperties.SetItemStatus(button, enabled ? "已开启" : "已关闭");
    }

    private void CloseSubmenus()
    {
        ReturnToPrimary();
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
                    : Equals(text.Tag, "secondary") ? Freeze("#A7ABB2") : Freeze("#F4F4F6");
            foreach (var path in FindVisualChildren<System.Windows.Shapes.Path>(root))
                path.Stroke = opaque ? System.Windows.SystemColors.WindowTextBrush : Freeze("#E9E9EE");
            // Inline sections share the owning surface and backdrop; no extra blur layer.
        }
    }

    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        ApplySystemAppearance();
        RefreshPopupMotionPolicy();
    }

    private void ApplySystemAppearance()
    {
        SurfaceShadow.Visibility = SystemParameters.HighContrast ? Visibility.Hidden : Visibility.Visible;
        var opaque = CapsuleThemePolicy.Resolve(SystemParameters.HighContrast, SystemParameters.IsGlassEnabled) == CapsuleSurfaceMode.OpaqueSystem;
        Resources["MenuSegmentTrackBrush"] = opaque ? System.Windows.SystemColors.ControlBrush : Freeze("#18000000");
        Resources["MenuSelectedBrush"] = opaque ? System.Windows.SystemColors.WindowBrush : Freeze("#22FFFFFF");
        Resources["MenuChoiceBorderBrush"] = opaque ? System.Windows.SystemColors.WindowTextBrush : Freeze("#18FFFFFF");
        Resources["MenuHoverBrush"] = opaque ? System.Windows.SystemColors.ControlBrush : Freeze("#0EFFFFFF");
        Resources["MenuSwitchOffBrush"] = opaque ? System.Windows.SystemColors.ControlBrush : Freeze("#26FFFFFF");
        Resources["MenuSwitchOnBrush"] = opaque ? System.Windows.SystemColors.WindowTextBrush : Freeze("#95BBB7");
        Resources["MenuSwitchThumbBrush"] = opaque ? System.Windows.SystemColors.WindowTextBrush : Freeze("#DDE0E4");
        Resources["MenuSwitchOnThumbBrush"] = opaque ? System.Windows.SystemColors.WindowBrush : MediaBrushes.White;
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
        PageTitleText.Foreground = opaque ? System.Windows.SystemColors.WindowTextBrush : MediaBrushes.White;
        PageHeader.BorderBrush = opaque ? System.Windows.SystemColors.ActiveBorderBrush : Freeze("#28FFFFFF");
        if (menuVerticalScrollBar is not null)
            menuVerticalScrollBar.Foreground = opaque ? System.Windows.SystemColors.WindowTextBrush : Freeze("#68FFFFFF");
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
