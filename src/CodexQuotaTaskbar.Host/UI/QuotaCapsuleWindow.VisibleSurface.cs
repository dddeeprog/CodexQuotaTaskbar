using System.Windows;
using CodexQuotaTaskbar.Core.Overlay;

namespace CodexQuotaTaskbar.Host.UI;

public partial class QuotaCapsuleWindow
{
    // Popup spacing uses painted chrome, not the transparent HWND shadow gutter.
    // Existing group/drag reservations deliberately continue to use PhysicalBounds.
    internal ScreenRect VisibleSurfaceBounds
    {
        get
        {
            if (physicalBounds.Width <= 0 || physicalBounds.Height <= 0) return physicalBounds;
            var scale = physicalBounds.Width / LayoutWidth;
            if (!double.IsFinite(scale) || scale <= 0) return physicalBounds;
            var margin = GlassBorder.Margin;
            var width = LayoutWidth - margin.Left - margin.Right;
            var height = physicalBounds.Height / scale - margin.Top - margin.Bottom;
            if (width <= 0 || height <= 0) return physicalBounds;
            var surface = new Rect(margin.Left, margin.Top, width, height);

            // The status ball (including its count badge) and the unread marker
            // can extend beyond the glass. Keep their full clickable envelopes safe.
            if (StatusBallVisibleForLayout)
            {
                if (companion?.IsArrangeValid != true || companion.ActualWidth <= 0 || companion.ActualHeight <= 0)
                    return physicalBounds;
                var ball = StatusBallBoundsDip;
                if (ball.IsEmpty) return physicalBounds;
                surface.Union(ball);
            }
            if (DotMarkerVisible)
            {
                if (dotMarker?.IsArrangeValid != true || dotMarker.ActualWidth <= 0 || dotMarker.ActualHeight <= 0)
                    return physicalBounds;
                var marker = DotMarkerBoundsDip;
                if (marker.IsEmpty) return physicalBounds;
                surface.Union(marker);
            }

            return new ScreenRect(
                Math.Max(physicalBounds.Left, (int)Math.Floor(physicalBounds.Left + surface.Left * scale)),
                Math.Max(physicalBounds.Top, (int)Math.Floor(physicalBounds.Top + surface.Top * scale)),
                Math.Min(physicalBounds.Right, (int)Math.Ceiling(physicalBounds.Left + surface.Right * scale)),
                Math.Min(physicalBounds.Bottom, (int)Math.Ceiling(physicalBounds.Top + surface.Bottom * scale)));
        }
    }
}
