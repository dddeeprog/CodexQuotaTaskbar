using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using CodexQuotaTaskbar.Host.Resets;
using Border = System.Windows.Controls.Border;
using Brush = System.Windows.Media.Brush;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Panel = System.Windows.Controls.Panel;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace CodexQuotaTaskbar.Host.UI;

// Restore the classic layout while sharing quota projection, badge animation and drag handling.
public partial class QuotaCapsuleWindow
{
    private TextBlock? classicResetSummary;
    private TextBlock? classicResetSource;
    private Grid? classicQuotaRows;
    private Border? classicLogo;

    private void InitializeClassicLayout()
    {
        Width = 306;
        Height = 112;
        BackdropLayer.Margin = GlassBorder.Margin = new Thickness(12, 28, 22, 16);
        BackdropLayer.CornerRadius = GlassBorder.CornerRadius = new CornerRadius(24);
        GlassBorder.BorderThickness = new Thickness(0.5);
        var body = (Grid)GlassBorder.Child;
        body.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        body.RowDefinitions[1].Height = new GridLength(0);
        PanelSessionsRegion.Visibility = Visibility.Collapsed;
        SessionToggle.Visibility = Visibility.Collapsed;
        QuotaHeader.Children.Remove(FirstRow);
        QuotaHeader.Children.Remove(SecondRow);
        QuotaHeader.Margin = new Thickness(8, 5, 14, 5);
        QuotaHeader.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
        QuotaHeader.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
        QuotaHeader.ColumnDefinitions.Clear();
        foreach (var width in new[] { new GridLength(38), new GridLength(10), new GridLength(1, GridUnitType.Star) })
            QuotaHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = width });

        var logo = new System.Windows.Shapes.Rectangle
        {
            Width = 21, Height = 21, Fill = Freeze("#F5FFFFFF"),
            OpacityMask = new ImageBrush(new BitmapImage(new Uri("pack://application:,,,/CodexQuotaTaskbar;component/Assets/OpenAILogo.png")))
            { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(1124, 274, 1591, 1613), Stretch = Stretch.Uniform }
        };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        classicLogo = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(16),
            Background = Freeze("#1AFFFFFF"), BorderBrush = Freeze("#28FFFFFF"), BorderThickness = new Thickness(0.5), Child = logo };
        QuotaHeader.Children.Add(classicLogo);

        var rows = new Grid();
        classicQuotaRows = rows;
        rows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(19) });
        rows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(19) });
        Grid.SetColumn(rows, 2);
        QuotaHeader.Children.Add(rows);
        ConfigureClassicRow(FirstRow, FirstLabel, FirstBar, FirstValue, 0);
        ConfigureClassicRow(SecondRow, SecondLabel, SecondBar, SecondValue, 1);
        rows.Children.Add(FirstRow);
        rows.Children.Add(SecondRow);

        var reset = new Grid { Margin = new Thickness(5, 0, 0, 0) };
        reset.ColumnDefinitions.Add(new ColumnDefinition());
        reset.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(reset, 1);
        Grid.SetColumnSpan(reset, 3);
        classicResetSummary = new TextBlock { Text = "最近重置 · 正在读取…", FontSize = 9.5,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        classicResetSource = new TextBlock { FontSize = 8.5, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var link = new Hyperlink(new Run("Codex Resets")) { TextDecorations = null };
        link.SetBinding(Hyperlink.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { Source = classicResetSource });
        link.Click += (_, _) => OpenResetSourceRequested?.Invoke(CodexResetFeedClient.SiteUri);
        classicResetSource.Inlines.Add(link);
        Grid.SetColumn(classicResetSource, 1);
        reset.Children.Add(classicResetSummary);
        reset.Children.Add(classicResetSource);
        QuotaHeader.Children.Add(reset);
        RegisterName("ResetSummaryText", classicResetSummary);
        RegisterName("ResetSourceLink", link);

        ((StackPanel)SessionBadge.Parent).Children.Remove(SessionBadge);
        Shell.Children.Add(SessionBadge);
        SessionBadge.MinWidth = 22;
        SessionBadge.CornerRadius = new CornerRadius(11);
        SessionBadge.Padding = new Thickness(3, 1, 3, 1);
        SessionBadge.HorizontalAlignment = HorizontalAlignment.Right;
        SessionBadge.VerticalAlignment = VerticalAlignment.Top;
        SessionBadge.Margin = new Thickness(0, 12, 12, 0);
        SessionBadge.BorderThickness = new Thickness(1);
        SessionBadge.BorderBrush = Freeze("#38FFFFFF");
        SessionBadge.Background = new LinearGradientBrush(
            new GradientStopCollection { new(System.Windows.Media.Color.FromRgb(56, 56, 60), 0),
                new(System.Windows.Media.Color.FromRgb(23, 23, 25), .56), new(System.Windows.Media.Color.FromRgb(41, 41, 45), 1) },
            new System.Windows.Point(0, 0), new System.Windows.Point(1, 1));
        SessionBadge.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 9, ShadowDepth = 2, Opacity = .34 };
        Panel.SetZIndex(SessionBadge, 3);
    }

    private static void ConfigureClassicRow(Grid row, TextBlock label, ProgressBar bar, TextBlock value, int index)
    {
        Grid.SetColumn(row, 0);
        Grid.SetRow(row, index);
        row.Margin = new Thickness(0);
        row.VerticalAlignment = VerticalAlignment.Stretch;
        row.RowDefinitions.Clear();
        row.ColumnDefinitions.Clear();
        foreach (var width in new[] { new GridLength(45), new GridLength(1, GridUnitType.Star), new GridLength(38) })
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        Grid.SetColumn(value, 2);
        Grid.SetRow(bar, 0);
        Grid.SetColumn(bar, 1);
        Grid.SetColumnSpan(bar, 1);
        bar.VerticalAlignment = VerticalAlignment.Center;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        bar.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ProgressBar">
                <Grid Height="5"><Border Background="#24FFFFFF" CornerRadius="2"/>
                    <Grid x:Name="PART_Track"><Border x:Name="PART_Indicator" HorizontalAlignment="Left" Background="{TemplateBinding Foreground}" CornerRadius="2"/></Grid>
                </Grid>
            </ControlTemplate>
            """);
    }

    private void ApplyClassicAppearance(Brush foreground)
    {
        if (classicResetSummary is not null) classicResetSummary.Foreground = foreground;
        if (classicResetSource is not null) classicResetSource.Foreground = foreground;
    }
}
