using System.Windows;
using System.Windows.Interop;
using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Host.Platform;
using Size = System.Windows.Size;

namespace CodexQuotaTaskbar.Host.UI;

public partial class TrayMenuWindow
{
    private ScreenRect? menuIslandBounds;
    private uint menuIslandDpi = 96;
    private IReadOnlyList<ScreenRect> menuIslandAvoidance = [];
    private ScreenRect? publishedPlacement;
    internal event Action<ScreenRect?>? PlacementBoundsChanged;
    internal ScreenRect? PresentedBounds => publishedPlacement;
    internal bool PlacementUnavailable { get; private set; }

    internal void UpdateIslandAnchor(ScreenRect island, uint dpi = 96, IReadOnlyList<ScreenRect>? avoidance = null)
    {
        menuIslandBounds = island;
        menuIslandDpi = dpi is >= 48 and <= 768 ? dpi : 96;
        menuIslandAvoidance = avoidance?.ToArray() ?? [];
        if (IsVisible) RepositionIslandMenu(show: false);
    }

    private void PublishPlacement(ScreenRect? bounds)
    {
        if (publishedPlacement == bounds) return;
        publishedPlacement = bounds;
        PlacementBoundsChanged?.Invoke(bounds);
    }

    // All three pages share this HWND and width. Changing page only changes
    // the bounded content height; no side pane or accordion is created.
    private void RepositionIslandMenu(bool show, long openingGeneration = 0)
    {
        if (placingMenu || (!show && (preparingPopup || IsPopupClosing)) || menuIslandBounds is not { } island) return;
        placingMenu = true;
        try
        {
            var point = new System.Drawing.Point(island.Left + island.Width / 2, island.Top + island.Height / 2);
            var workArea = System.Windows.Forms.Screen.FromPoint(point).WorkingArea;
            var work = new ScreenRect(workArea.Left, workArea.Top, workArea.Right, workArea.Bottom);
            var scale = menuIslandDpi / 96d;
            menuAnchor = point;

            // Measure the current page before allocating a lane.
            // No size/position changes are made to the owning island.
            if (!TrySetPageHeightLimit(Math.Min(MenuPageHeightLimitDip, Math.Max(1, (workArea.Height - 16) / scale))))
            {
                PlacementUnavailable = true;
                if (IsVisible) Hide();
                PublishPlacement(null);
                return;
            }
            var content = (FrameworkElement)Content;
            content.Measure(new Size(Width, double.PositiveInfinity));
            var naturalHeight = Math.Min(MenuPageHeightLimitDip, Math.Max(1, content.DesiredSize.Height));
            if (!TryCalculateIslandBounds(work, island, menuIslandDpi, naturalHeight, out var bounds, out var maximumHeight,
                menuAnchorGap, menuIslandAvoidance, MinimumUsablePageHeightDip, (int)MenuShell.Margin.Left))
            {
                PlacementUnavailable = true;
                if (IsVisible) Hide();
                PublishPlacement(null);
                return;
            }

            PlacementUnavailable = false;
            if (!TrySetPageHeightLimit(maximumHeight))
            {
                PlacementUnavailable = true;
                if (IsVisible) Hide();
                PublishPlacement(null);
                return;
            }
            content.Measure(new Size(Width, maximumHeight));
            content.Arrange(new Rect(0, 0, Width, Math.Min(content.DesiredSize.Height, maximumHeight)));
            var handle = new WindowInteropHelper(this).EnsureHandle();
            // A hidden HWND is positioned before Show so the first painted frame
            // cannot flash over the cat or at the desktop origin.
            if (!NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                NativeMethods.SwpNoActivate))
            {
                PlacementUnavailable = true; PublishPlacement(null); return;
            }
            if (show) SetPopupEntranceAnchor(bounds);
            if (show && !IsCurrentPopupOpenRequest(openingGeneration)) return;
            if (show && !IsVisible) Show();
            UpdateLayout();
            if (!IsVisible || (show && !IsCurrentPopupOpen(openingGeneration))) return;
            if (!NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                NativeMethods.SwpNoActivate))
            {
                PlacementUnavailable = true; Hide(); PublishPlacement(null); return;
            }
            PublishPlacement(bounds);
            if (show)
            {
                if (!IsCurrentPopupOpen(openingGeneration)) return;
                StartPendingPopupEntrance();
                // Only the user's explicit right-click opens/focuses the menu.
                // Drag/size/DPI updates never call Activate or Focus.
                if (activateOnShow) { Activate(); Focus(); }
            }
        }
        finally { placingMenu = false; }
    }

    internal static bool TryCalculateIslandBounds(ScreenRect work, ScreenRect island, uint dpi, double naturalHeightDip,
        out ScreenRect bounds, out double maximumHeightDip, int gap = 8, IReadOnlyList<ScreenRect>? obstacles = null,
        double minimumHeightDip = 120, int surfaceInsetDip = 0)
    {
        bounds = default; maximumHeightDip = 0;
        if (!double.IsFinite(naturalHeightDip) || naturalHeightDip <= 0 ||
            !double.IsFinite(minimumHeightDip) || minimumHeightDip <= 0) return false;
        var scale = dpi is >= 48 and <= 768 ? dpi / 96d : 1;
        var width = checked((int)Math.Ceiling(272 * scale));
        var height = checked((int)Math.Ceiling(Math.Min(naturalHeightDip, 10000) * scale));
        var physicalGap = checked((int)Math.Ceiling(Math.Max(0, gap) * scale));
        var inset = checked((int)Math.Ceiling(Math.Max(0, surfaceInsetDip) * scale));
        var margin = checked((int)Math.Ceiling(8 * scale));
        if (PopupSurfacePlacement.TryPlace(work, island, width, height, inset, obstacles, out bounds, physicalGap, margin))
        {
            maximumHeightDip = height / scale;
            return true;
        }

        var minimumHeight = checked((int)Math.Ceiling(Math.Min(naturalHeightDip, minimumHeightDip) * scale));
        if (!PopupSurfacePlacement.TryFindLane(work, island, width, minimumHeight, inset, obstacles, out var lane, physicalGap, margin))
            return false;
        height = Math.Min(height, lane.Height);
        var left = (int)Math.Clamp((long)island.Left + island.Width / 2 - width / 2, lane.Left, (long)lane.Right - width);
        var top = (int)Math.Clamp((long)island.Top, lane.Top, (long)lane.Bottom - height);
        bounds = new(left, top, left + width, top + height);
        maximumHeightDip = height / scale;
        return true;
    }
}
