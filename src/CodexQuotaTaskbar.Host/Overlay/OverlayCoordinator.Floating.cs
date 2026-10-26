using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.Platform;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Overlay;

internal sealed partial class OverlayCoordinator
{
    private bool dotPresentationEnabled;
    private ScreenRect? menuBounds;
    private ScreenRect? popoverBounds;
    private readonly Dictionary<string, (ScreenRect Owner, ScreenRect Work, int Dpi, ScreenRect Lane)> dotLanes = new();

    internal void SetDotPresentationEnabled(bool enabled)
    {
        if (dotPresentationEnabled == enabled) return;
        dotPresentationEnabled = enabled; dotLanes.Clear();
        RefreshInlineDotPresentation();
        if (popoverOwner is { } owner) RepositionPopover(owner);
        PublishDotAnchor();
    }

    internal void SetMenuBounds(ScreenRect? bounds)
    {
        if (menuBounds == bounds) return;
        menuBounds = bounds;
        UpdateDotNotificationPlacement();
        if (popoverOwner is { } owner) RepositionPopover(owner);
        PublishDotAnchor();
    }

    internal IReadOnlyList<ScreenRect> GetMenuObstacles(QuotaCapsuleWindow owner)
    {
        var work = WorkAreaFor(owner); var dpi = DpiFor(owner);
        var result = new List<ScreenRect>();
        if (!inlineDotMode && ResolveDotLane(owner, work, dpi) is { } lane && lane.Right > lane.Left && lane.Bottom > lane.Top)
            result.Add(new(lane.Left, lane.Top, lane.Right, lane.Bottom));
        if (popoverBounds is { } details) result.Add(details);
        return result;
    }

    private static ScreenRect WorkAreaFor(QuotaCapsuleWindow owner)
    {
        var body = owner.PhysicalBounds;
        var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(body.Left + body.Width / 2,
            body.Top + body.Height / 2)).WorkingArea;
        return new(area.Left, area.Top, area.Right, area.Bottom);
    }

    private static int DpiFor(QuotaCapsuleWindow owner)
    {
        var dpi = NativeMethods.GetDpiForWindow(new System.Windows.Interop.WindowInteropHelper(owner).Handle);
        return dpi is >= 48 and <= 768 ? (int)dpi : 96;
    }

    private DotViewport? ResolveDotLane(QuotaCapsuleWindow owner, ScreenRect work, double dpiValue)
    {
        if (!dotPresentationEnabled) return null;
        if (inlineDotMode) return null;
        var dpi = (int)dpiValue; var scale = dpi / 96d; var body = GroupBounds(owner);
        var obstacles = menuBounds is { } menu ? new[] { menu } : Array.Empty<ScreenRect>();
        var gap = (int)Math.Ceiling(8 * scale);
        if (dotLanes.TryGetValue(owner.MonitorId, out var existing) && existing.Owner == body && existing.Work == work
            && existing.Dpi == dpi && !obstacles.Any(rect => FloatingSurfaceLayout.Intersects(existing.Lane,
                new(rect.Left - gap, rect.Top - gap, rect.Right + gap, rect.Bottom + gap))))
            return new(existing.Lane.Left, existing.Lane.Top, existing.Lane.Right, existing.Lane.Bottom);
        if (FloatingSurfaceLayout.TryFindLane(work, body, (int)Math.Ceiling(320 * scale), (int)Math.Ceiling(240 * scale),
            (int)Math.Ceiling(240 * scale), obstacles, out var lane, gap, gap))
        {
            dotLanes[owner.MonitorId] = (body, work, dpi, lane);
            return new(lane.Left, lane.Top, lane.Right, lane.Bottom);
        }
        dotLanes.Remove(owner.MonitorId);
        // No safe region: the child keeps receiving and retains pending/composition/drafts.
        return new(0, 0, 0, 0);
    }

    private bool TryCalculatePopoverBounds(QuotaCapsuleWindow owner, QuotaPopoverWindow details, out ScreenRect bounds)
    {
        bounds = default;
        var work = WorkAreaFor(owner); var dpi = DpiFor(owner); var scale = dpi / 96d;
        var island = PopupAnchorBounds(owner); var obstacles = new List<ScreenRect>();
        if (!inlineDotMode && ResolveDotLane(owner, work, dpi) is { } dot && dot.Right > dot.Left && dot.Bottom > dot.Top)
            obstacles.Add(new(dot.Left, dot.Top, dot.Right, dot.Bottom));
        if (menuBounds is { } menu) obstacles.Add(menu);
        var desiredWidth = (int)Math.Ceiling(PopoverWidth * scale);
        var gap = (int)Math.Ceiling(6 * scale);
        var inset = (int)Math.Ceiling(details.PlacementSurfaceInsetDip * scale);
        var margin = (int)Math.Ceiling(8 * scale);
        details.FitAvailableHeight(552);
        var content = (System.Windows.FrameworkElement)details.Content;
        content.Measure(new System.Windows.Size(PopoverWidth, double.PositiveInfinity));
        var naturalHeight = (int)Math.Ceiling(Math.Max(1, content.DesiredSize.Height) * scale);
        if (PopupSurfacePlacement.TryPlace(work, island, desiredWidth, naturalHeight, inset, obstacles, out bounds, gap, margin)) return true;
        if (!PopupSurfacePlacement.TryFindLane(work, island, desiredWidth,
            (int)Math.Ceiling(160 * scale), inset, obstacles, out var lane, gap, margin)) return false;
        details.FitAvailableHeight(lane.Height / scale);
        content.Measure(new System.Windows.Size(PopoverWidth, lane.Height / scale));
        var height = Math.Min(lane.Height, (int)Math.Ceiling(Math.Max(1, content.DesiredSize.Height) * scale));
        var x = (int)Math.Clamp((long)island.Left + island.Width / 2 - desiredWidth / 2, lane.Left, (long)lane.Right - desiredWidth);
        var y = (int)Math.Clamp((long)island.Top, lane.Top, (long)lane.Bottom - height);
        bounds = new(x, y, x + desiredWidth, y + height); return true;
    }

    private ScreenRect PopupAnchorBounds(QuotaCapsuleWindow owner) =>
        sessionWindows.TryGetValue(owner.MonitorId, out var stack) && stack.IsVisible
            ? Union(owner.VisibleSurfaceBounds, stack.VisibleSurfaceBounds) : owner.VisibleSurfaceBounds;
}
