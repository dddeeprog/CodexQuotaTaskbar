using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace CodexQuotaTaskbar.Host.UI;

public static class OverlayGlassMaterial
{
    public const double BlurRadius = 20;
    public const double Merging = 0.92;
    public const byte BorderAlpha = 70;

    public static WpfBrush Surface { get; } = CreateSurfaceBrush();
    public static WpfBrush HoverSurface => Surface;
    public static WpfBrush Border { get; } = CreateBorderBrush();

    internal static double MaterialOpacity(int transparencyPercent) =>
        1d - Math.Clamp(transparencyPercent, 0, 100) / 100d;

    internal static WpfBrush WithTransparency(WpfBrush source, int transparencyPercent)
    {
        if (MaterialOpacity(transparencyPercent) == 1 && source.Opacity == 1) return source;
        var brush = source.CloneCurrentValue();
        brush.Opacity = MaterialOpacity(transparencyPercent);
        brush.Freeze();
        return brush;
    }

    internal static LinearGradientBrush CreateSurfaceBrush(int transparencyPercent = 0)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new WpfPoint(0, 0),
            EndPoint = new WpfPoint(0, 1),
            Opacity = MaterialOpacity(transparencyPercent),
        };
        brush.GradientStops.Add(new GradientStop(WpfColor.FromRgb(58, 58, 60), 0));
        brush.GradientStops.Add(new GradientStop(WpfColor.FromRgb(48, 48, 50), 1));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush CreateBorderBrush()
    {
        var brush = new SolidColorBrush(WpfColor.FromArgb(BorderAlpha, 255, 255, 255));
        brush.Freeze();
        return brush;
    }
}
