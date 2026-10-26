using System.Windows.Media;
using CodexQuotaTaskbar.Core.Overlay;

namespace CodexQuotaTaskbar.Host.UI;

public partial class SessionStackWindow
{
    internal ScreenRect VisibleSurfaceBounds
    {
        get
        {
            if (physicalBounds.Width <= 0 || physicalBounds.Height <= 0) return physicalBounds;
            // Reflow/removal ghosts and the toggle can temporarily outlive the
            // current mask. Reserve the original HWND until those clocks finish.
            if (sessionTransitionTimer?.IsEnabled == true
                || CardTransitionOverlay.Children.Count > 0 || LayerTransitionOverlay.Children.Count > 0)
                return physicalBounds;
            if (GlassCaptureSurface.OpacityMask is not DrawingBrush { Drawing: { } drawing }) return physicalBounds;
            var surface = drawing.Bounds;
            if (surface.IsEmpty || surface.Width <= 0 || surface.Height <= 0) return physicalBounds;
            var scale = physicalBounds.Width / StackWidth;
            if (!double.IsFinite(scale) || scale <= 0) return physicalBounds;

            return new ScreenRect(
                Math.Max(physicalBounds.Left, (int)Math.Floor(physicalBounds.Left + surface.Left * scale)),
                Math.Max(physicalBounds.Top, (int)Math.Floor(physicalBounds.Top + surface.Top * scale)),
                Math.Min(physicalBounds.Right, (int)Math.Ceiling(physicalBounds.Left + surface.Right * scale)),
                Math.Min(physicalBounds.Bottom, (int)Math.Ceiling(physicalBounds.Top + surface.Bottom * scale)));
        }
    }
}
