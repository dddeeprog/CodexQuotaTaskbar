namespace CodexQuotaTaskbar.Core.Overlay;

public static class OverlayPlacementCalculator
{
    public static ScreenRect Calculate(TaskbarAnchor anchor, double widthDip, double heightDip, double farMarginDip, double edgeGapDip)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        var scale = anchor.Dpi / 96d;
        var width = checked((int)Math.Round(widthDip * scale));
        var height = checked((int)Math.Round(heightDip * scale));
        var farMargin = checked((int)Math.Round(farMarginDip * scale));
        var edgeGap = checked((int)Math.Round(edgeGapDip * scale));

        var left = anchor.Edge switch
        {
            TaskbarEdge.Left => anchor.TaskbarBounds.Right + edgeGap,
            TaskbarEdge.Right => anchor.TaskbarBounds.Left - edgeGap - width,
            _ => anchor.MonitorBounds.Right - farMargin - width,
        };
        var top = anchor.Edge switch
        {
            TaskbarEdge.Top => anchor.TaskbarBounds.Bottom + edgeGap,
            TaskbarEdge.Bottom => anchor.TaskbarBounds.Top - edgeGap - height,
            _ => anchor.MonitorBounds.Bottom - farMargin - height,
        };

        left = Math.Clamp(left, anchor.MonitorBounds.Left, anchor.MonitorBounds.Right - width);
        top = Math.Clamp(top, anchor.MonitorBounds.Top, anchor.MonitorBounds.Bottom - height);
        return new ScreenRect(left, top, checked(left + width), checked(top + height));
    }
}
