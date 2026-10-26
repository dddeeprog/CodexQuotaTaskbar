using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexQuotaTaskbar.Core.Overlay;
using Image = System.Windows.Controls.Image;
using Size = System.Windows.Size;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;

namespace CodexQuotaTaskbar.Host.UI;

// One visual tree owns the original model bitmap and the existing native quota/task
// controls. The owning quota window remains the only interactive native surface.
internal sealed class BongoIslandSurface : Grid
{
    internal const double SurfaceWidthDip = 248;
    internal const double CornerRadiusDip = 22;
    internal const double ModelTopInsetDip = 8;
    private readonly Image model = new() { Stretch = Stretch.Uniform, IsHitTestVisible = false };
    private readonly DeskPlane desk = new();
    private readonly Grid stage = new() { ClipToBounds = true, Background = Brushes.Transparent };
    private readonly FrameworkElement quotaBody;
    internal double ModelWidthDip { get; private set; }
    internal double StageHeightDip { get; private set; }
    internal Rect ModelBoundsDip { get; private set; }
    internal double BodyHeightDip { get; private set; }
    internal double SurfaceHeightDip => StageHeightDip + BodyHeightDip;
    internal bool IsModelStageMouseOver => stage.IsMouseOver;

    internal BongoIslandSurface(FrameworkElement quotaBody, double modelWidthDip, double bodyHeightDip,
        double surfaceWidthDip = SurfaceWidthDip)
    {
        this.quotaBody = quotaBody ?? throw new ArgumentNullException(nameof(quotaBody));
        if (VisualTreeHelper.GetParent(quotaBody) is not null) throw new ArgumentException("Quota body must be detached.");
        if (!double.IsFinite(bodyHeightDip) || bodyHeightDip < 48 || bodyHeightDip > 1000)
            throw new ArgumentOutOfRangeException(nameof(bodyHeightDip));
        if (!double.IsFinite(surfaceWidthDip) || surfaceWidthDip < 240 || surfaceWidthDip > 400)
            throw new ArgumentOutOfRangeException(nameof(surfaceWidthDip));
        BodyHeightDip = bodyHeightDip;
        Width = surfaceWidthDip;
        Background = OverlayGlassMaterial.Surface;
        SnapsToDevicePixels = true;
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition());
        model.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        model.VerticalAlignment = System.Windows.VerticalAlignment.Top;
        model.Margin = new Thickness(0, ModelTopInsetDip, 0, 0);
        RenderOptions.SetBitmapScalingMode(model, BitmapScalingMode.HighQuality);
        stage.Children.Add(desk);
        stage.Children.Add(model);
        Children.Add(stage);
        SetRow(quotaBody, 1); Children.Add(quotaBody);
        SetModelWidth(modelWidthDip);
    }

    internal void Present(BitmapSource? bitmap) => model.Source = bitmap;
    internal bool HasModel => model.Source is not null;
    internal bool OwnsQuotaBody => ReferenceEquals(VisualTreeHelper.GetParent(quotaBody), this);
    internal FrameworkElement DetachQuotaBody()
    {
        Present(null);
        Children.Remove(quotaBody);
        SetRow(quotaBody, 0);
        return quotaBody;
    }

    internal void SetBodyHeight(double heightDip)
    {
        if (!double.IsFinite(heightDip) || heightDip < 48 || heightDip > 1000)
            throw new ArgumentOutOfRangeException(nameof(heightDip));
        if (BodyHeightDip == heightDip) return;
        BodyHeightDip = heightDip;
        UpdateGeometry();
    }

    internal void SetModelWidth(double widthDip)
    {
        ModelWidthDip = BongoCompanionPlacement.ClampWidth(widthDip);
        var modelHeight = ModelWidthDip / BongoCompanionPlacement.ModelAspect;
        StageHeightDip = ModelTopInsetDip + modelHeight;
        ModelBoundsDip = new((Width - ModelWidthDip) / 2, ModelTopInsetDip, ModelWidthDip, modelHeight);
        model.Width = ModelWidthDip; model.Height = modelHeight;
        desk.ModelBounds = ModelBoundsDip;
        desk.InvalidateVisual();
        UpdateGeometry();
    }

    private void UpdateGeometry()
    {
        Height = SurfaceHeightDip;
        RowDefinitions[0].Height = new GridLength(StageHeightDip);
        RowDefinitions[1].Height = new GridLength(BodyHeightDip);
        Clip = new RectangleGeometry(new Rect(0, 0, Width, Height), CornerRadiusDip, CornerRadiusDip);
    }

    internal static bool TryPlace(ScreenRect work, ScreenRect originalPanel, int dpi, double heightDip,
        out ScreenRect result)
    {
        result = default;
        if (!double.IsFinite(heightDip) || heightDip < 48 || heightDip > 1500
            || work.Width <= 0 || work.Height <= 0 || originalPanel.Width <= 0 || originalPanel.Height <= 0) return false;
        var scale = dpi is >= 48 and <= 768 ? dpi / 96d : 1;
        var width = (int)Math.Ceiling(SurfaceWidthDip * scale);
        var height = (int)Math.Ceiling(heightDip * scale);
        if (width > work.Width || height > work.Height) return false;
        var left = (int)Math.Clamp((long)originalPanel.Left + (long)Math.Round(16 * scale), work.Left, (long)work.Right - width);
        var top = (int)Math.Clamp((long)originalPanel.Bottom - (long)Math.Round(16 * scale) - height,
            work.Top, (long)work.Bottom - height);
        result = new(left, top, left + width, top + height); return true;
    }

    // Only extends the original model's white tabletop into the shared card.
    // It does not remove/recolor the original keyboard, model, or key overlays.
    private sealed class DeskPlane : FrameworkElement
    {
        internal Rect ModelBounds { get; set; }
        protected override void OnRender(DrawingContext drawing)
        {
            var slope = ModelBounds.Width / 612d * 110 / ModelBounds.Width;
            var y = ModelBounds.Top + ModelBounds.Height * 150 / 354d - ModelBounds.Left * slope;
            var rightY = y + ActualWidth * slope;
            var geometry = new StreamGeometry();
            using (var c = geometry.Open())
            {
                c.BeginFigure(new Point(0, y), true, true);
                c.LineTo(new Point(ActualWidth, rightY), true, false);
                c.LineTo(new Point(ActualWidth, ActualHeight), true, false);
                c.LineTo(new Point(0, ActualHeight), true, false);
            }
            geometry.Freeze();
            drawing.DrawGeometry(Brushes.White, null, geometry);
            drawing.DrawLine(new Pen(Brushes.Black, Math.Max(.8, ModelBounds.Width / 612d * 5)), new Point(0, y), new Point(ActualWidth, rightY));
        }
    }
}
