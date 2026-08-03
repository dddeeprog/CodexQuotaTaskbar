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

        var work = AvailableBounds(anchor);
        var above = capsule.Top - work.Top;
        var below = work.Bottom - capsule.Bottom;
        var leftSpace = capsule.Left - work.Left;
        var rightSpace = work.Right - capsule.Right;

        var direction = ChooseDirection(above, below, leftSpace, rightSpace, width, height, gap);
        var left = direction switch
        {
            PlacementDirection.Left => capsule.Left - gap - width,
            PlacementDirection.Right => capsule.Right + gap,
            _ => capsule.Right - width,
        };
        var top = direction switch
        {
            PlacementDirection.Above => capsule.Top - gap - height,
            PlacementDirection.Below => capsule.Bottom + gap,
            _ => capsule.Top + (capsule.Height - height) / 2,
        };

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

    private static PlacementDirection ChooseDirection(int above, int below, int left, int right, int width, int height, int gap)
    {
        if (Math.Max(above, below) >= height + gap)
        {
            return above >= below ? PlacementDirection.Above : PlacementDirection.Below;
        }
        if (Math.Max(left, right) >= width + gap)
        {
            return left >= right ? PlacementDirection.Left : PlacementDirection.Right;
        }

        var candidates = new[]
        {
            (PlacementDirection.Above, above),
            (PlacementDirection.Below, below),
            (PlacementDirection.Left, left),
            (PlacementDirection.Right, right),
        };
        return candidates.MaxBy(candidate => candidate.Item2).Item1;
    }

    private enum PlacementDirection
    {
        Above,
        Below,
        Left,
        Right,
    }
}
