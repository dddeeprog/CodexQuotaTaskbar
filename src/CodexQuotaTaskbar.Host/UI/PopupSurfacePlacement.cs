using CodexQuotaTaskbar.Core.Overlay;

namespace CodexQuotaTaskbar.Host.UI;

// Places the visible card, while retaining its unchanged transparent shadow
// inset inside the returned HWND. All values are physical pixels.
internal static class PopupSurfacePlacement
{
    internal static bool TryPlace(ScreenRect work, ScreenRect visibleIsland, int outerWidth, int outerHeight,
        int inset, IReadOnlyList<ScreenRect>? obstacles, out ScreenRect bounds, int gap = 6, int margin = 8)
    {
        bounds = default;
        if (!Prepare(work, visibleIsland, outerWidth, outerHeight, inset, obstacles, out var visibleWork,
                out var visibleWidth, out var visibleHeight, out var blocked)
            || !FloatingSurfaceLayout.TryPlace(visibleWork, visibleIsland, visibleWidth, visibleHeight,
                blocked, out var visibleBounds, gap, margin)) return false;
        return TryInflate(visibleBounds, inset, out bounds);
    }

    // The lane has the exact requested outer width and the maximum available
    // outer height. A shorter popup can be clamped inside it without losing its
    // visible-edge separation or clipping its shadow at the work-area edge.
    internal static bool TryFindLane(ScreenRect work, ScreenRect visibleIsland, int outerWidth, int minimumOuterHeight,
        int inset, IReadOnlyList<ScreenRect>? obstacles, out ScreenRect lane, int gap = 6, int margin = 8)
    {
        lane = default;
        if (!Prepare(work, visibleIsland, outerWidth, minimumOuterHeight, inset, obstacles, out var visibleWork,
                out var visibleWidth, out var minimumVisibleHeight, out var blocked)
            || !FloatingSurfaceLayout.TryFindLane(visibleWork, visibleIsland, visibleWidth, visibleWidth,
                minimumVisibleHeight, blocked, out var visibleLane, gap, margin)) return false;
        return TryInflate(visibleLane, inset, out lane);
    }

    private static bool Prepare(ScreenRect work, ScreenRect visibleIsland, int outerWidth, int outerHeight,
        int inset, IReadOnlyList<ScreenRect>? obstacles, out ScreenRect visibleWork, out int visibleWidth,
        out int visibleHeight, out ScreenRect[] blocked)
    {
        visibleWork = default; visibleWidth = visibleHeight = 0; blocked = [];
        if (!Valid(work) || !Valid(visibleIsland) || inset is < 0 or > 4096 || obstacles?.Count > 8
            || outerWidth <= 2L * inset || outerHeight <= 2L * inset
            || (long)work.Right - work.Left <= 2L * inset || (long)work.Bottom - work.Top <= 2L * inset)
            return false;
        visibleWork = new(work.Left + inset, work.Top + inset, work.Right - inset, work.Bottom - inset);
        visibleWidth = outerWidth - 2 * inset;
        visibleHeight = outerHeight - 2 * inset;
        var expanded = new List<ScreenRect>();
        foreach (var obstacle in obstacles ?? [])
        {
            // Other popup bounds are already HWND bounds. Expanding them before
            // testing the visible candidate also excludes this popup's shadow.
            if (!Valid(obstacle) || !TryInflate(obstacle, inset, out var value)) return false;
            expanded.Add(value);
        }
        blocked = expanded.ToArray();
        return true;
    }

    private static bool Valid(ScreenRect rect) =>
        (long)rect.Right - rect.Left is > 0 and <= int.MaxValue
        && (long)rect.Bottom - rect.Top is > 0 and <= int.MaxValue;

    private static bool TryInflate(ScreenRect rect, int inset, out ScreenRect inflated)
    {
        inflated = default;
        if ((long)rect.Left - inset < int.MinValue || (long)rect.Top - inset < int.MinValue
            || (long)rect.Right + inset > int.MaxValue || (long)rect.Bottom + inset > int.MaxValue)
            return false;
        inflated = new(rect.Left - inset, rect.Top - inset, rect.Right + inset, rect.Bottom + inset);
        return Valid(inflated);
    }
}
