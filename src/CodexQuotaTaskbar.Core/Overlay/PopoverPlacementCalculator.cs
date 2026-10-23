namespace CodexQuotaTaskbar.Core.Overlay;

public static class PopoverPlacementCalculator
{
    public static ScreenRect Calculate(TaskbarAnchor anchor, ScreenRect capsule, double widthDip, double heightDip, double gapDip, ScreenRect? alignment = null)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        var scale = anchor.Dpi / 96d;
        var width = checked((int)Math.Round(widthDip * scale));
        var height = checked((int)Math.Round(heightDip * scale));
        var gap = checked((int)Math.Round(gapDip * scale));

        var work = AvailableBounds(anchor);
        var above = capsule.Top - work.Top;
        var below = work.Bottom - capsule.Bottom;
        width = Math.Min(width, work.Width);
        height = Math.Min(height, Math.Max(1, Math.Min(work.Height, Math.Max(above, below) - gap)));
        var centeredOn = alignment ?? capsule;
        var left = centeredOn.Left + (centeredOn.Width - width) / 2;
        var top = above >= below ? capsule.Top - gap - height : capsule.Bottom + gap;

        left = Math.Clamp(left, work.Left, work.Right - width);
        top = Math.Clamp(top, work.Top, work.Bottom - height);
        return new ScreenRect(left, top, checked(left + width), checked(top + height));
    }

    private static ScreenRect AvailableBounds(TaskbarAnchor anchor) => anchor.Edge switch
    {
        TaskbarEdge.Top => anchor.MonitorBounds with { Top = Math.Max(anchor.MonitorBounds.Top, anchor.TaskbarBounds.Bottom) },
        TaskbarEdge.Bottom => anchor.MonitorBounds with { Bottom = Math.Min(anchor.MonitorBounds.Bottom, anchor.TaskbarBounds.Top) },
        TaskbarEdge.Left => anchor.MonitorBounds with { Left = Math.Max(anchor.MonitorBounds.Left, anchor.TaskbarBounds.Right) },
        TaskbarEdge.Right => anchor.MonitorBounds with { Right = Math.Min(anchor.MonitorBounds.Right, anchor.TaskbarBounds.Left) },
        _ => anchor.MonitorBounds,
    };

    public static double AvailableHeightDip(TaskbarAnchor anchor, ScreenRect obstruction, double gapDip)
    {
        var work = AvailableBounds(anchor);
        var scale = anchor.Dpi / 96d;
        return Math.Max(1, Math.Min(work.Height, Math.Max(obstruction.Top - work.Top, work.Bottom - obstruction.Bottom)
            - Math.Round(gapDip * scale))) / scale;
    }
}
