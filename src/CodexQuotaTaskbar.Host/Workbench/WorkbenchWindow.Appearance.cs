using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using CodexQuotaTaskbar.Host.UI;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using SystemColors = System.Windows.SystemColors;

namespace CodexQuotaTaskbar.Host.Workbench;

internal partial class WorkbenchWindow
{
    private void SystemAppearanceChanged(object? sender, PropertyChangedEventArgs args) => ApplyAppearance();
    private void ApplyAppearance() => ApplyAppearance(SystemParameters.HighContrast, SystemParameters.IsGlassEnabled);

    internal void ApplyAppearance(bool highContrast, bool glassEnabled)
    {
        var opaque = CapsuleThemePolicy.Resolve(highContrast, glassEnabled) == CapsuleSurfaceMode.OpaqueSystem;
        System.Windows.Media.Brush Tint(string value) { var brush = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(value)); brush.Freeze(); return brush; }
        Resources["WorkbenchBackground"] = opaque ? SystemColors.WindowBrush : OverlayGlassMaterial.Surface;
        GlassBorder.Background = (System.Windows.Media.Brush)Resources["WorkbenchBackground"];
        GlassBorder.BorderBrush = opaque ? SystemColors.ActiveBorderBrush : OverlayGlassMaterial.Border;
        SurfaceShadow.Background = GlassBorder.Background;
        SurfaceShadow.Visibility = opaque ? Visibility.Hidden : Visibility.Visible;
        Resources["WorkbenchText"] = opaque ? SystemColors.WindowTextBrush : Tint("#F4F4F6");
        Resources["WorkbenchMuted"] = opaque ? SystemColors.WindowTextBrush : Tint("#B5B5BA");
        Resources["WorkbenchPane"] = opaque ? SystemColors.ControlBrush : Tint("#10000000");
        Resources["WorkbenchInput"] = opaque ? SystemColors.WindowBrush : Tint("#18000000");
        Resources["WorkbenchLine"] = opaque ? SystemColors.ActiveBorderBrush : Tint("#28FFFFFF");
        Resources["WorkbenchButton"] = opaque ? SystemColors.ControlBrush : Tint("#18FFFFFF");
        Resources["WorkbenchHover"] = opaque ? SystemColors.ControlBrush : Tint("#0EFFFFFF");
        Resources["WorkbenchActive"] = opaque ? SystemColors.HighlightBrush : Tint("#22FFFFFF");
        Resources["WorkbenchSelectedText"] = opaque ? SystemColors.HighlightTextBrush : Tint("#F4F4F6");
        Resources["WorkbenchFocus"] = opaque ? SystemColors.WindowTextBrush : Tint("#B0FFFFFF");
        Resources["WorkbenchThumb"] = opaque ? SystemColors.WindowTextBrush : Tint("#68FFFFFF");
        Resources["WorkbenchDiffRemoved"] = opaque ? SystemColors.WindowBrush : Tint("#452D31");
        Resources["WorkbenchDiffAdded"] = opaque ? SystemColors.WindowBrush : Tint("#263E35");
    }

    internal void StyleDialog(Window dialog, FrameworkElement body)
    {
        dialog.WindowStyle = WindowStyle.None; dialog.AllowsTransparency = true; dialog.Background = System.Windows.Media.Brushes.Transparent;
        dialog.FontFamily = OverlayTypography.Interface; dialog.FontSize = 13;
        dialog.Resources.MergedDictionaries.Add(Resources);
        dialog.SetResourceReference(ForegroundProperty, "WorkbenchText");
        WindowChrome.SetWindowChrome(dialog, new WindowChrome { CaptionHeight = 48, ResizeBorderThickness = new Thickness(dialog.ResizeMode == ResizeMode.NoResize ? 0 : 5), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(16), UseAeroCaptionButtons = false });
        var heading = new DockPanel { Margin = new Thickness(18, 12, 12, 8) };
        var close = new Button { Content = "关闭", Style = (Style)Resources["WorkbenchQuietButton"], Margin = new Thickness(10, 0, 0, 0) };
        WindowChrome.SetIsHitTestVisibleInChrome(close, true); DockPanel.SetDock(close, Dock.Right);
        close.Click += (_, _) => { if (CanStartInteraction) dialog.Close(); };
        heading.Children.Add(close); heading.Children.Add(new TextBlock { Text = dialog.Title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var layout = new Grid(); layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new());
        layout.Children.Add(heading); Grid.SetRow(body, 1); layout.Children.Add(body);
        var surface = new Border { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(0.5), BorderBrush = GlassBorder.BorderBrush, Child = layout };
        surface.SetResourceReference(BackgroundProperty, "WorkbenchBackground");
        dialog.Content = new Grid { Margin = new Thickness(6), Children = { surface } };
    }
}
