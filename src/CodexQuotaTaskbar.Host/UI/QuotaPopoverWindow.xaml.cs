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
    internal const string GlassCaptureTargetName = "BackdropLayer";
    internal const double GlassBlurRadius = 24;
    internal const double GlassMerging = 0.94;
    internal static EasingMode ExitEasingMode => EasingMode.EaseOut;
    private bool activationEstablished;
    private bool blurReady;
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
        Activated += (_, _) => activationEstablished = true;
        Deactivated += (_, _) =>
        {
            if (activationEstablished)
            {
                Dispatcher.BeginInvoke(RequestClose);
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
        Closed += (_, _) => SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        ApplySystemAppearance();
    }

    internal event EventHandler? RefreshRequested;
    internal event EventHandler? OpenCodexRequested;

    internal void PlayEntrance((double X, double Y) offset)
    {
        anchorOffsetX = offset.X;
        anchorOffsetY = offset.Y;
        Opacity = 1;
        PopoverScale.ScaleX = PopoverScale.ScaleY = 1;
        PopoverTranslate.X = PopoverTranslate.Y = 0;
        if (!SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        var duration = TimeSpan.FromMilliseconds(EntranceAnimationMilliseconds);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = easing });
        PopoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1, duration) { EasingFunction = easing });
        PopoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1, duration) { EasingFunction = easing });
        PopoverTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(offset.X, 0, duration) { EasingFunction = easing });
        PopoverTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset.Y, 0, duration) { EasingFunction = easing });
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
        var duration = TimeSpan.FromMilliseconds(ExitAnimationMilliseconds);
        var easing = new CubicEase { EasingMode = ExitEasingMode };
        var opacity = new DoubleAnimation(Opacity, 0, duration) { EasingFunction = easing };
        opacity.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, opacity);
        PopoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(PopoverScale.ScaleX, 0.98, duration) { EasingFunction = easing });
        PopoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(PopoverScale.ScaleY, 0.98, duration) { EasingFunction = easing });
        PopoverTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(PopoverTranslate.X, anchorOffsetX * 0.7, duration) { EasingFunction = easing });
        PopoverTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(PopoverTranslate.Y, anchorOffsetY * 0.7, duration) { EasingFunction = easing });
    }

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
        StatusText.Text = snapshot.StatusText + (snapshot.CapturedAt is { } captured ? $" · {captured.ToLocalTime():HH:mm:ss}" : string.Empty);
        PlanText.Text = $"订阅 · {snapshot.SubscriptionPlan ?? "未知"}";
        RowsPanel.Children.Clear();
        if (snapshot.Windows.Count == 0)
        {
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
                FontFamily = OverlayTypography.Text,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var value = new TextBlock
            {
                Text = $"{window.RemainingPercent:0}%",
                Foreground = System.Windows.Media.Brushes.White,
                FontFamily = OverlayTypography.Number,
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(value, 1);
            Grid.SetRowSpan(value, 2);
            panel.Children.Add(value);
            var reset = new TextBlock
            {
                Text = $"{window.ResetsAt.ToLocalTime():M月d日 HH:mm} 重置",
                Tag = "secondary",
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(150, 215, 226, 236)),
                FontFamily = OverlayTypography.Text,
                FontSize = 10.5,
                Margin = new Thickness(0, 3, 0, 0),
            };
            Grid.SetRow(reset, 1);
            Grid.SetColumnSpan(reset, 2);
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
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(22, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(34, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 9),
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
            _ => "其他",
        };
        var duration = window.WindowDurationMinutes % 1440 == 0
            ? $"{window.WindowDurationMinutes / 1440} 天"
            : $"{Math.Max(1, window.WindowDurationMinutes / 60)} 小时";
        return $"{source} · {duration}额度";
    }

    internal static int SourceRank(string limitId) => limitId switch
    {
        var value when string.Equals(value, "codex", StringComparison.OrdinalIgnoreCase) => 0,
        var value when string.Equals(value, "codex_bengalfox", StringComparison.OrdinalIgnoreCase) => 1,
        _ => 2,
    };

    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs) => ApplySystemAppearance();

    private void ApplySystemAppearance()
    {
        var opaque = CapsuleThemePolicy.Resolve(SystemParameters.HighContrast, SystemParameters.IsGlassEnabled) == CapsuleSurfaceMode.OpaqueSystem;
        GlassBorder.Background = opaque ? System.Windows.SystemColors.WindowBrush : normalGlassBackground;
        GlassBorder.BorderBrush = opaque ? System.Windows.SystemColors.ActiveBorderBrush : new SolidColorBrush(System.Windows.Media.Color.FromArgb(80, 255, 255, 255));
        BackdropLayer.Background = opaque ? System.Windows.SystemColors.WindowBrush : normalBackdropBackground;
        SetBlurEnabled(!opaque);
        TitleText.Foreground = opaque ? System.Windows.SystemColors.WindowTextBrush : System.Windows.Media.Brushes.White;
        StatusText.Foreground = opaque ? System.Windows.SystemColors.WindowTextBrush : new SolidColorBrush(System.Windows.Media.Color.FromRgb(174, 174, 178));
        PlanText.Foreground = opaque ? System.Windows.SystemColors.ControlTextBrush : new SolidColorBrush(System.Windows.Media.Color.FromRgb(242, 242, 247));
        PlanBadge.Background = opaque ? System.Windows.SystemColors.ControlBrush : new SolidColorBrush(System.Windows.Media.Color.FromArgb(24, 255, 255, 255));
        PlanBadge.BorderBrush = opaque ? System.Windows.SystemColors.ActiveBorderBrush : new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 255, 255, 255));
        var rowText = FindTextBlocks(RowsPanel).Append(QuotaHeading);
        foreach (var text in rowText)
        {
            text.Foreground = opaque
                ? System.Windows.SystemColors.WindowTextBrush
                : Equals(text.Tag, "secondary") ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(174, 174, 178))
                : System.Windows.Media.Brushes.White;
        }
        CloseButton.Foreground = RefreshButton.Foreground = opaque ? System.Windows.SystemColors.ControlTextBrush : System.Windows.Media.Brushes.White;
        CloseButton.Background = RefreshButton.Background = opaque ? System.Windows.SystemColors.ControlBrush : new SolidColorBrush(System.Windows.Media.Color.FromArgb(44, 255, 255, 255));
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
        Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(22, 255, 255, 255)),
        BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(34, 255, 255, 255)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(16),
        Padding = new Thickness(14),
        Child = new TextBlock
        {
            Text = "暂无可显示的额度窗口",
            Tag = "secondary",
            Foreground = System.Windows.Media.Brushes.White,
            FontFamily = OverlayTypography.Text,
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

    private static System.Windows.Media.Brush QuotaBrush(double remaining) => new SolidColorBrush(remaining switch
    {
        < 10 => System.Windows.Media.Color.FromRgb(255, 92, 108),
        < 30 => System.Windows.Media.Color.FromRgb(255, 184, 77),
        _ => System.Windows.Media.Color.FromRgb(242, 242, 247),
    });
}
