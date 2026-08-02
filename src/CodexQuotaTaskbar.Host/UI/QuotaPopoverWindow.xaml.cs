using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CodexQuotaTaskbar.Core.Quota;
using FrostedBlur = BlurredBackground.WPF.BlurredBackground;

namespace CodexQuotaTaskbar.Host.UI;

public partial class QuotaPopoverWindow : Window
{
    private bool activationEstablished;
    private bool blurReady;
    private readonly System.Windows.Media.Brush normalGlassBackground;
    private readonly System.Windows.Media.Brush normalBackdropBackground;

    internal QuotaPopoverWindow()
    {
        InitializeComponent();
        normalGlassBackground = GlassBorder.Background;
        normalBackdropBackground = BackdropLayer.Background;
        Loaded += (_, _) =>
        {
            blurReady = true;
            ApplySystemAppearance();
        };
        CloseButton.Click += (_, _) => Close();
        RefreshButton.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        OpenCodexButton.Click += (_, _) => OpenCodexRequested?.Invoke(this, EventArgs.Empty);
        Activated += (_, _) => activationEstablished = true;
        Deactivated += (_, _) =>
        {
            if (activationEstablished)
            {
                Dispatcher.BeginInvoke(Close);
            }
        };
        KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == Key.Escape)
            {
                Close();
            }
        };
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        Closed += (_, _) => SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        ApplySystemAppearance();
    }

    internal event EventHandler? RefreshRequested;
    internal event EventHandler? OpenCodexRequested;

    internal void Apply(QuotaSnapshot snapshot)
    {
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
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text"),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var value = new TextBlock
            {
                Text = $"{window.RemainingPercent:0}%",
                Foreground = System.Windows.Media.Brushes.White,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Display"),
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
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text"),
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
        var rowText = RowsPanel.Children.OfType<TextBlock>()
            .Concat(RowsPanel.Children.OfType<Border>().SelectMany(border =>
                border.Child is Grid grid ? grid.Children.OfType<TextBlock>() : []));
        foreach (var text in rowText)
        {
            text.Foreground = opaque
                ? System.Windows.SystemColors.WindowTextBrush
                : Equals(text.Tag, "secondary") ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(174, 174, 178)) : System.Windows.Media.Brushes.White;
        }
        CloseButton.Foreground = RefreshButton.Foreground = opaque ? System.Windows.SystemColors.ControlTextBrush : System.Windows.Media.Brushes.White;
        CloseButton.Background = RefreshButton.Background = opaque ? System.Windows.SystemColors.ControlBrush : new SolidColorBrush(System.Windows.Media.Color.FromArgb(44, 255, 255, 255));
        OpenCodexButton.Foreground = opaque ? System.Windows.SystemColors.HighlightTextBrush : new SolidColorBrush(System.Windows.Media.Color.FromRgb(17, 17, 19));
        OpenCodexButton.Background = opaque ? System.Windows.SystemColors.HighlightBrush : new SolidColorBrush(System.Windows.Media.Color.FromRgb(242, 242, 244));
    }

    private void SetBlurEnabled(bool enabled)
    {
        if (blurReady && FrostedBlur.GetEnableBlur(GlassBorder) != enabled)
        {
            FrostedBlur.SetBlurRadius(GlassBorder, 24);
            FrostedBlur.SetMerging(GlassBorder, 0.94);
            FrostedBlur.SetDpi(GlassBorder, 48);
            FrostedBlur.SetEnableBlur(GlassBorder, enabled);
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
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text"),
            FontSize = 12,
        },
    };

    private static System.Windows.Media.Brush QuotaBrush(double remaining) => new SolidColorBrush(remaining switch
    {
        < 10 => System.Windows.Media.Color.FromRgb(255, 92, 108),
        < 30 => System.Windows.Media.Color.FromRgb(255, 184, 77),
        _ => System.Windows.Media.Color.FromRgb(242, 242, 247),
    });
}
