namespace CodexQuotaTaskbar.Core.Overlay;

public sealed record SessionStackPlacement(ScreenRect Capsule, ScreenRect Stack);

public static class SessionStackPlacementCalculator
{
    public static SessionStackPlacement CalculateBelow(TaskbarAnchor anchor, ScreenRect capsule, double widthDip, double heightDip, double gapDip)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        var scale = anchor.Dpi / 96d;
        var width = checked((int)Math.Round(widthDip * scale));
        var height = checked((int)Math.Round(heightDip * scale));
        var gap = checked((int)Math.Round(gapDip * scale));
        var work = AvailableBounds(anchor);

        var groupBottom = capsule.Bottom + gap + height;
        var shiftY = groupBottom > work.Bottom ? work.Bottom - groupBottom : 0;
        if (capsule.Top + shiftY < work.Top)
        {
            shiftY = work.Top - capsule.Top;
        }
        var adjustedCapsule = new ScreenRect(
            capsule.Left,
            capsule.Top + shiftY,
            capsule.Right,
            capsule.Bottom + shiftY);
        var top = adjustedCapsule.Bottom + gap;
        var left = capsule.Left + (capsule.Width - width) / 2;

        left = width >= work.Width ? work.Left : Math.Clamp(left, work.Left, work.Right - width);
        var stack = new ScreenRect(left, top, checked(left + width), checked(top + height));
        return new SessionStackPlacement(adjustedCapsule, stack);
    }

    private static ScreenRect AvailableBounds(TaskbarAnchor anchor) => anchor.Edge switch
    {
        TaskbarEdge.Top => anchor.MonitorBounds with { Top = Math.Max(anchor.MonitorBounds.Top, anchor.TaskbarBounds.Bottom) },
        TaskbarEdge.Bottom => anchor.MonitorBounds with { Bottom = Math.Min(anchor.MonitorBounds.Bottom, anchor.TaskbarBounds.Top) },
        TaskbarEdge.Left => anchor.MonitorBounds with { Left = Math.Max(anchor.MonitorBounds.Left, anchor.TaskbarBounds.Right) },
        TaskbarEdge.Right => anchor.MonitorBounds with { Right = Math.Min(anchor.MonitorBounds.Right, anchor.TaskbarBounds.Left) },
        _ => anchor.MonitorBounds,
    };
}
