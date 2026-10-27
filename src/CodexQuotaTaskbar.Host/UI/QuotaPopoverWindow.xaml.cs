using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Core.Quota;
using FrostedBlur = BlurredBackground.WPF.BlurredBackground;

namespace CodexQuotaTaskbar.Host.UI;

public partial class QuotaPopoverWindow : Window
{
    internal const int EntranceAnimationMilliseconds = 220;
    internal const int ExitAnimationMilliseconds = 130;
    internal double PlacementSurfaceInsetDip => PopoverShell.Margin.Left;
    internal const string GlassCaptureTargetName = "BackdropLayer";
    internal const double GlassBlurRadius = OverlayGlassMaterial.BlurRadius;
    internal const double GlassMerging = OverlayGlassMaterial.Merging;
    internal static EasingMode ExitEasingMode => EasingMode.EaseOut;
    private bool activationEstablished;
    internal long ActivationGeneration { get; private set; }
    private bool popupClosed;
    private bool blurReady;
    private int materialTransparency;
    private bool closingAnimated;
    private double anchorOffsetX;
    private double anchorOffsetY;
    private readonly System.Windows.Media.Brush normalGlassBackground;
    private readonly System.Windows.Media.Brush normalBackdropBackground;
    private QuotaSnapshot quotaSnapshot = QuotaSnapshot.Unavailable("正在连接 Codex…");

    internal QuotaPopoverWindow()
    {
        InitializeComponent();
        normalGlassBackground = GlassBorder.Background;
        normalBackdropBackground = BackdropLayer.Background;
        Opacity = SystemParameters.ClientAreaAnimation ? 0 : 1;
        Loaded += (_, _) =>
        {
            blurReady = true;
            ApplySystemAppearance();
        };
        CloseButton.Click += (_, _) => RequestClose();
        RefreshButton.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        OpenCodexButton.Click += (_, _) => OpenCodexRequested?.Invoke(this, EventArgs.Empty);
        AddShortcutButton.Click += (_, _) => OpenAddShortcutDialog();
        SubscriptionLoginButton.Click += (_, _) =>
        {
            RequestClose();
            OpenSubscriptionLoginRequested?.Invoke(this, EventArgs.Empty);
        };
        Activated += (_, _) => { activationEstablished = true; ++ActivationGeneration; };
        Deactivated += (_, _) =>
        {
            OverlayLifecycleDiagnostics.Local.Window(this, OverlayLifecycleEvent.DetailsDeactivated);
            if (activationEstablished && !HasOpenShortcutInteraction)
            {
                var generation = ++ActivationGeneration;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (CanCloseAfterDeactivation(generation, IsActive, IsVisible)) RequestClose();
                }));
            }
        };
        KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == Key.Escape)
            {
                RequestClose();
            }
        };
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        Closed += (_, _) =>
        {
            popupClosed = true; ++ActivationGeneration;
            SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        };
        ApplySystemAppearance();
    }

    internal event EventHandler? RefreshRequested;
    internal event EventHandler? OpenCodexRequested;
    internal event EventHandler? OpenSubscriptionLoginRequested;

    internal void PlayEntrance((double X, double Y) offset)
    {
        ++ActivationGeneration;
        anchorOffsetX = offset.X;
        anchorOffsetY = offset.Y;
        Opacity = 1;
        PopoverScale.ScaleX = PopoverScale.ScaleY = 1;
        PopoverTranslate.X = PopoverTranslate.Y = 0;
        if (!SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        BeginAnimation(OpacityProperty, CreateSurfaceAnimation(0, 1, true));
        PopoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, CreateSurfaceAnimation(0.97, 1, true));
        PopoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, CreateSurfaceAnimation(0.97, 1, true));
        PopoverTranslate.BeginAnimation(TranslateTransform.XProperty, CreateSurfaceAnimation(offset.X, 0, true));
        PopoverTranslate.BeginAnimation(TranslateTransform.YProperty, CreateSurfaceAnimation(offset.Y, 0, true));
        PlaySectionEntrance();
    }

    private void PlaySectionEntrance()
    {
        var groups = new FrameworkElement[] { AccountSection, QuotaSection, AnnouncementsSection };
        for (var index = 0; index < groups.Length; index++)
        {
            var group = groups[index];
            var slide = new TranslateTransform();
            group.RenderTransform = slide;
            group.BeginAnimation(OpacityProperty, CreateSectionEntranceAnimation(0.7, 1, index));
            slide.BeginAnimation(TranslateTransform.YProperty, CreateSectionEntranceAnimation(3, 0, index));
        }
    }

    internal static DoubleAnimation CreateSurfaceAnimation(double from, double to, bool opening) =>
        new(from, to, TimeSpan.FromMilliseconds(opening ? EntranceAnimationMilliseconds : ExitAnimationMilliseconds))
        { EasingFunction = new CubicEase { EasingMode = opening ? EasingMode.EaseOut : ExitEasingMode } };

    internal static DoubleAnimationUsingKeyFrames CreateSectionEntranceAnimation(double from, double to, int index)
    {
        var delay = TimeSpan.FromMilliseconds(index * 25);
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(delay)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(delay + TimeSpan.FromMilliseconds(150)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        return animation;
    }

    internal void FitAvailableHeight(double availableDip)
    {
        var chromeHeight = Math.Max(0, ActualHeight - DetailsScroller.ActualHeight);
        MaxHeight = Math.Clamp(availableDip, 1, 552);
        DetailsScroller.MaxHeight = Math.Clamp(MaxHeight - chromeHeight, 0, 400);
        UpdateLayout();
    }

    internal void RequestClose()
    {
        if (closingAnimated)
        {
            return;
        }
        if (!SystemParameters.ClientAreaAnimation)
        {
            Close();
            return;
        }

        closingAnimated = true;
        var opacity = CreateSurfaceAnimation(Opacity, 0, false);
        opacity.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, opacity);
        PopoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, CreateSurfaceAnimation(PopoverScale.ScaleX, 0.98, false));
        PopoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, CreateSurfaceAnimation(PopoverScale.ScaleY, 0.98, false));
        PopoverTranslate.BeginAnimation(TranslateTransform.XProperty, CreateSurfaceAnimation(PopoverTranslate.X, anchorOffsetX * 0.7, false));
        PopoverTranslate.BeginAnimation(TranslateTransform.YProperty, CreateSurfaceAnimation(PopoverTranslate.Y, anchorOffsetY * 0.7, false));
    }

    internal bool CanCloseAfterDeactivation(long generation, bool active, bool visible) =>
        generation == ActivationGeneration && !active && visible && !popupClosed
        && !closingAnimated && !HasOpenShortcutInteraction;

    internal static (double X, double Y) CalculateEntranceOffset(ScreenRect anchor, ScreenRect popover)
    {
        if (popover.Bottom <= anchor.Top)
        {
            return (0, 10);
        }
        if (popover.Top >= anchor.Bottom)
        {
            return (0, -10);
        }
        return popover.Right <= anchor.Left ? (10, 0) : (-10, 0);
    }

    internal void Apply(QuotaSnapshot snapshot)
    {
        quotaSnapshot = snapshot;
        Render();
    }

    private void Render()
    {
        var snapshot = quotaSnapshot;
        PlanText.Text = snapshot.SubscriptionPlan ?? "未知";
        ExpiryText.Text = FormatMembershipExpiration(snapshot.SubscriptionExpiration);
        ExpiryText.Tag = snapshot.SubscriptionExpiration.Availability == SubscriptionExpirationAvailability.Available ? null : "secondary";
        ExtraCreditsText.Text = FormatExtraCredits(snapshot.ExtraCredits);
        ExtraCreditsText.Tag = snapshot.ExtraCredits is null ? "secondary" : null;
        RowsPanel.Children.Clear();
        if (snapshot.Availability != QuotaAvailability.Available || snapshot.UnresolvedWindows.Count > 0)
            RowsPanel.Children.Add(new TextBlock { Text = snapshot.Availability == QuotaAvailability.Stale
                ? "连接中断 · 以下为上次数据" : snapshot.UnresolvedWindows.Count > 0
                    ? "部分窗口字段未提供 · 未知不代表无限额度" : snapshot.StatusText,
                TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 0, 0, 10), Tag = "secondary" });
        if (snapshot.Windows.Count == 0)
        {
            if (snapshot.Availability == QuotaAvailability.Available && snapshot.UnresolvedWindows.Count == 0)
                RowsPanel.Children.Add(CreateEmptyState());
            ApplySystemAppearance();
            return;
        }

        foreach (var window in snapshot.Windows.OrderBy(value => SourceRank(value.LimitId)).ThenBy(value => value.WindowDurationMinutes))
        {
            var title = FormatWindowTitle(window);
            var panel = new Grid();
            panel.ColumnDefinitions.Add(new ColumnDefinition());
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition());
            panel.RowDefinitions.Add(new RowDefinition());
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
            panel.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = System.Windows.Media.Brushes.White,
                FontFamily = OverlayTypography.Interface,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var value = new TextBlock
            {
                Text = $"{window.RemainingPercent:0}%",
                Foreground = System.Windows.Media.Brushes.White,
                FontFamily = OverlayTypography.Interface,
                FontSize = 20,
                Width = 62,
                TextAlignment = TextAlignment.Right,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(value, 1);
            Grid.SetRowSpan(value, 2);
            panel.Children.Add(value);
            var reset = new TextBlock
            {
                Text = window.ResetsAt is { } resetsAt ? $"{resetsAt.ToLocalTime():M月d日 HH:mm} 重置" : "重置时间未提供",
                Tag = "secondary",
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(150, 215, 226, 236)),
                FontFamily = OverlayTypography.Interface,
                FontSize = 11,
                Margin = new Thickness(0, 3, 0, 0),
            };
            Grid.SetRow(reset, 1);
            Grid.SetColumnSpan(reset, 1);
            panel.Children.Add(reset);
            var bar = new System.Windows.Controls.ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = window.RemainingPercent,
                Height = 5,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(28, 255, 255, 255)),
                Foreground = QuotaBrush(window.RemainingPercent),
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Bottom,
                Style = (Style)FindResource("LiquidProgress"),
            };
            Grid.SetRow(bar, 2);
            Grid.SetColumnSpan(bar, 2);
            panel.Children.Add(bar);
            RowsPanel.Children.Add(new Border
            {
                Background = System.Windows.Media.Brushes.Transparent,
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(34, 255, 255, 255)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0, 5, 0, 12),
                Margin = new Thickness(0, 0, 0, 4),
                Child = panel,
            });
        }
        ApplySystemAppearance();
    }

    internal static string FormatWindowTitle(QuotaWindowSnapshot window)
    {
        var source = window.LimitId switch
        {
            var value when string.Equals(value, "codex", StringComparison.OrdinalIgnoreCase) => "Codex",
            var value when string.Equals(value, "codex_bengalfox", StringComparison.OrdinalIgnoreCase) => "Spark",
            _ => window.LimitName is { } name && name != window.LimitId ? $"{name} ({window.LimitId})" : window.LimitId,
        };
        return $"{source} · {window.DurationLabel}额度";
    }

    internal static int SourceRank(string limitId) => limitId switch
    {
        var value when string.Equals(value, "codex", StringComparison.OrdinalIgnoreCase) => 0,
        var value when string.Equals(value, "codex_bengalfox", StringComparison.OrdinalIgnoreCase) => 1,
        _ => 2,
    };

    internal static string FormatMembershipExpiration(SubscriptionExpirationSnapshot expiration) =>
        expiration.Availability == SubscriptionExpirationAvailability.Available && expiration.ExpiresAt is { } value
            ? value.ToLocalTime().ToString("yyyy年M月d日", CultureInfo.GetCultureInfo("zh-CN"))
            : expiration.StatusText;

    internal static string FormatExtraCredits(QuotaCreditsSnapshot? credits)
    {
        if (credits is null)
        {
            return "—";
        }
        if (credits.Unlimited)
        {
            return "不限";
        }
        if (!credits.HasCredits)
        {
            return "0";
        }
        return credits.Balance is { } balance
            ? balance.ToString("#,##0.##", CultureInfo.GetCultureInfo("zh-CN"))
            : "可用";
    }

    internal void SetMaterialTransparency(int value)
    {
        materialTransparency = Math.Clamp(value, 0, 100);
        ApplySystemAppearance();
    }

    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs) => ApplySystemAppearance();

    private void ApplySystemAppearance()
    {
        SurfaceShadow.Visibility = SystemParameters.HighContrast ? Visibility.Hidden : Visibility.Visible;
        var opaque = CapsuleThemePolicy.Resolve(SystemParameters.HighContrast, SystemParameters.IsGlassEnabled) == CapsuleSurfaceMode.OpaqueSystem;
        GlassBorder.Background = opaque ? System.Windows.SystemColors.WindowBrush : OverlayGlassMaterial.WithTransparency(normalGlassBackground, materialTransparency);
        GlassBorder.BorderBrush = SystemParameters.HighContrast ? System.Windows.SystemColors.ActiveBorderBrush : OverlayGlassMaterial.Border;
        BackdropLayer.Background = opaque ? System.Windows.SystemColors.WindowBrush : normalBackdropBackground;
        SetBlurEnabled(!opaque);
        TitleText.Foreground = opaque ? System.Windows.SystemColors.WindowTextBrush : System.Windows.Media.Brushes.White;
        foreach (var title in new[] { AccountSectionTitle, QuotaSectionTitle, ShortcutsSectionTitle })
            title.Foreground = opaque ? System.Windows.SystemColors.WindowTextBrush : System.Windows.Media.Brushes.White;
        AccountSectionDivider.BorderBrush = QuotaSectionDivider.BorderBrush = opaque
            ? System.Windows.SystemColors.ActiveBorderBrush
            : new SolidColorBrush(System.Windows.Media.Color.FromArgb(96, 255, 255, 255));
        AccountSummaryBorder.Background = System.Windows.Media.Brushes.Transparent;
        AccountSummaryBorder.BorderBrush = opaque ? System.Windows.SystemColors.ActiveBorderBrush : new SolidColorBrush(System.Windows.Media.Color.FromArgb(34, 255, 255, 255));
        AccountDividerOne.Background = AccountDividerTwo.Background = opaque
            ? System.Windows.SystemColors.ActiveBorderBrush
            : new SolidColorBrush(System.Windows.Media.Color.FromArgb(34, 255, 255, 255));
        foreach (var text in FindTextBlocks(AccountSummaryBorder))
        {
            text.Foreground = opaque
                ? System.Windows.SystemColors.WindowTextBrush
                : Equals(text.Tag, "secondary") ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(174, 174, 178))
                : System.Windows.Media.Brushes.White;
        }
        foreach (var text in FindTextBlocks(RowsPanel).Concat(FindResetElements<TextBlock>(ShortcutsSection)))
        {
            text.Foreground = opaque
                ? System.Windows.SystemColors.WindowTextBrush
                : Equals(text.Tag, "secondary") ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(174, 174, 178))
                : System.Windows.Media.Brushes.White;
        }
        CloseButton.Foreground = RefreshButton.Foreground = opaque ? System.Windows.SystemColors.ControlTextBrush : System.Windows.Media.Brushes.White;
        CloseButton.Background = RefreshButton.Background = opaque ? System.Windows.SystemColors.ControlBrush : new SolidColorBrush(System.Windows.Media.Color.FromArgb(20, 255, 255, 255));
        SubscriptionLoginButton.Foreground = opaque ? System.Windows.SystemColors.WindowTextBrush : new SolidColorBrush(System.Windows.Media.Color.FromRgb(194, 194, 198));
        SubscriptionLoginButton.Background = System.Windows.Media.Brushes.Transparent;
        ApplyShortcutAppearance(opaque);
        OpenCodexButton.Foreground = opaque ? System.Windows.SystemColors.HighlightTextBrush : new SolidColorBrush(System.Windows.Media.Color.FromRgb(17, 17, 19));
        OpenCodexButton.Background = opaque ? System.Windows.SystemColors.HighlightBrush : new SolidColorBrush(System.Windows.Media.Color.FromRgb(242, 242, 244));
    }

    private void SetBlurEnabled(bool enabled)
    {
        if (blurReady && FrostedBlur.GetEnableBlur(BackdropLayer) != enabled)
        {
            FrostedBlur.SetBlurRadius(BackdropLayer, GlassBlurRadius);
            FrostedBlur.SetMerging(BackdropLayer, GlassMerging);
            FrostedBlur.SetDpi(BackdropLayer, 48);
            FrostedBlur.SetEnableBlur(BackdropLayer, enabled);
        }
    }

    private static Border CreateEmptyState() => new()
    {
        Background = System.Windows.Media.Brushes.Transparent,
        BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(34, 255, 255, 255)),
        BorderThickness = new Thickness(0),
        Padding = new Thickness(0, 10, 0, 16),
        Child = new TextBlock
        {
            Text = "暂无可显示的额度窗口",
            Tag = "secondary",
            Foreground = System.Windows.Media.Brushes.White,
            FontFamily = OverlayTypography.Interface,
            FontSize = 12,
        },
    };

    private static IEnumerable<TextBlock> FindTextBlocks(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is TextBlock text)
            {
                yield return text;
            }
            foreach (var descendant in FindTextBlocks(child))
            {
                yield return descendant;
            }
        }
    }

    private static IEnumerable<T> FindResetElements<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var descendant in FindResetElements<T>(child)) yield return descendant;
        }
    }

    private static System.Windows.Media.Brush QuotaBrush(double remaining) => new SolidColorBrush(remaining switch
    {
        < 10 => System.Windows.Media.Color.FromRgb(255, 92, 108),
        < 30 => System.Windows.Media.Color.FromRgb(255, 184, 77),
        _ => System.Windows.Media.Color.FromRgb(242, 242, 247),
    });
}
