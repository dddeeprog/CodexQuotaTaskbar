namespace CodexQuotaTaskbar.Core.Overlay;

public static class PopoverPlacementCalculator
{
    public static ScreenRect Calculate(TaskbarAnchor anchor, ScreenRect capsule, double widthDip, double heightDip, double gapDip)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        var scale = anchor.Dpi / 96d;
        var width = checked((int)Math.Round(widthDip * scale));
        var height = checked((int)Math.Round(heightDip * scale));
        var gap = checked((int)Math.Round(gapDip * scale));

        var left = anchor.Edge switch
        {
            TaskbarEdge.Left => capsule.Right + gap,
            TaskbarEdge.Right => capsule.Left - gap - width,
            _ => capsule.Right - width,
        };
        var top = anchor.Edge switch
        {
            TaskbarEdge.Top => capsule.Bottom + gap,
            TaskbarEdge.Bottom => capsule.Top - gap - height,
            _ => capsule.Bottom - height,
        };

        left = Math.Clamp(left, anchor.MonitorBounds.Left, anchor.MonitorBounds.Right - width);
        top = Math.Clamp(top, anchor.MonitorBounds.Top, anchor.MonitorBounds.Bottom - height);
        return new ScreenRect(left, top, checked(left + width), checked(top + height));
    }
}
