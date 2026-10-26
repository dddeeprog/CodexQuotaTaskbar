using CodexQuotaTaskbar.Core.Overlay;

namespace CodexQuotaTaskbar.Host.UI;

// Pure physical-pixel geometry. False means that a surface must defer or collapse;
// returning a clamped rectangle over the island is never a successful placement.
internal static class FloatingSurfaceLayout
{
    private const int MaximumObstacles = 8;

    internal static bool TryPlace(ScreenRect work, ScreenRect island, int width, int height,
        IReadOnlyList<ScreenRect>? obstacles, out ScreenRect bounds, int gap = 8, int margin = 8)
    {
        bounds = default;
        if (width <= 0 || height <= 0 || !Prepare(work, island, obstacles, gap, margin, out var area, out var blocked)
            || width > area.Width || height > area.Height) return false;
        var xs = new HashSet<int> { area.Left, area.Right - width };
        var ys = new HashSet<int> { area.Top, area.Bottom - height };
        Add(xs, (long)island.Left + island.Width / 2 - width / 2, area.Left, area.Right - width);
        Add(ys, (long)island.Top + island.Height / 2 - height / 2, area.Top, area.Bottom - height);
        Add(ys, island.Top, area.Top, area.Bottom - height);
        foreach (var obstacle in blocked)
        {
            Add(xs, (long)obstacle.Left - width, area.Left, area.Right - width);
            Add(xs, obstacle.Right, area.Left, area.Right - width);
            Add(ys, (long)obstacle.Top - height, area.Top, area.Bottom - height);
            Add(ys, obstacle.Bottom, area.Top, area.Bottom - height);
        }
        (int Direction, long Distance, int X, int Y)? best = null;
        foreach (var x in xs)
            foreach (var y in ys)
            {
                var candidate = new ScreenRect(x, y, x + width, y + height);
                if (blocked.Any(obstacle => Intersects(candidate, obstacle))) continue;
                var score = (Direction(candidate, island), Distance(candidate, island), x, y);
                if (best is not null && score.CompareTo(best.Value) >= 0) continue;
                best = score; bounds = candidate;
            }
        return best is not null;
    }

    internal static bool TryFindLane(ScreenRect work, ScreenRect island, int desiredWidth, int minimumWidth,
        int minimumHeight, IReadOnlyList<ScreenRect>? obstacles, out ScreenRect lane, int gap = 8, int margin = 8)
    {
        lane = default;
        if (desiredWidth <= 0 || minimumWidth <= 0 || minimumHeight <= 0 || desiredWidth < minimumWidth
            || !Prepare(work, island, obstacles, gap, margin, out var area, out var blocked)
            || minimumWidth > area.Width || minimumHeight > area.Height) return false;
        var edges = new SortedSet<int> { area.Left, area.Right };
        foreach (var obstacle in blocked)
        {
            edges.Add(Math.Clamp(obstacle.Left, area.Left, area.Right));
            edges.Add(Math.Clamp(obstacle.Right, area.Left, area.Right));
        }
        var xs = edges.ToArray();
        (int Direction, int WidthLoss, long Distance, int HeightLoss, int X, int Y)? best = null;
        var bestBounds = default(ScreenRect);
        for (var left = 0; left < xs.Length - 1; left++)
            for (var right = left + 1; right < xs.Length; right++)
            {
                var availableWidth = (long)xs[right] - xs[left];
                if (availableWidth < minimumWidth) continue;
                var width = Math.Min(desiredWidth, (int)availableWidth);
                var x = (int)Math.Clamp((long)island.Left + island.Width / 2 - width / 2,
                    xs[left], (long)xs[right] - width);
                var spans = blocked.Where(rect => rect.Left < x + width && rect.Right > x)
                    .OrderBy(rect => rect.Top).ToArray();
                var top = area.Top;
                foreach (var span in spans)
                {
                    Consider(top, Math.Min(span.Top, area.Bottom));
                    top = Math.Max(top, span.Bottom);
                    if (top >= area.Bottom) break;
                }
                Consider(top, area.Bottom);

                void Consider(int y1, int y2)
                {
                    if ((long)y2 - y1 < minimumHeight) return;
                    var candidate = new ScreenRect(x, y1, x + width, y2);
                    if (blocked.Any(rect => Intersects(candidate, rect))) return;
                    var score = (Direction(candidate, island), desiredWidth - width, Distance(candidate, island),
                        area.Height - candidate.Height, candidate.Left, candidate.Top);
                    if (best is not null && score.CompareTo(best.Value) >= 0) return;
                    best = score; bestBounds = candidate;
                }
            }
        lane = bestBounds;
        return best is not null;
    }

    internal static bool Intersects(ScreenRect first, ScreenRect second) =>
        first.Left < second.Right && first.Right > second.Left && first.Top < second.Bottom && first.Bottom > second.Top;

    private static bool Prepare(ScreenRect work, ScreenRect island, IReadOnlyList<ScreenRect>? obstacles,
        int gap, int margin, out ScreenRect area, out ScreenRect[] blocked)
    {
        area = default; blocked = [];
        if (!Valid(work) || !Valid(island) || gap is < 0 or > 4096 || margin is < 0 or > 4096
            || obstacles?.Count > MaximumObstacles || (long)work.Width <= margin * 2L || (long)work.Height <= margin * 2L) return false;
        area = new(work.Left + margin, work.Top + margin, work.Right - margin, work.Bottom - margin);
        var list = new List<ScreenRect> { Inflate(island, gap, area) };
        foreach (var obstacle in obstacles ?? [])
        {
            if (!Valid(obstacle)) return false;
            var inflated = Inflate(obstacle, gap, area);
            if (Valid(inflated)) list.Add(inflated);
        }
        blocked = list.Where(Valid).ToArray(); return true;
    }

    private static bool Valid(ScreenRect rect) => (long)rect.Right - rect.Left is > 0 and <= int.MaxValue
        && (long)rect.Bottom - rect.Top is > 0 and <= int.MaxValue;

    private static ScreenRect Inflate(ScreenRect rect, int gap, ScreenRect area) => new(
        (int)Math.Clamp((long)rect.Left - gap, area.Left, area.Right),
        (int)Math.Clamp((long)rect.Top - gap, area.Top, area.Bottom),
        (int)Math.Clamp((long)rect.Right + gap, area.Left, area.Right),
        (int)Math.Clamp((long)rect.Bottom + gap, area.Top, area.Bottom));

    private static void Add(HashSet<int> values, long value, int minimum, int maximum) =>
        values.Add((int)Math.Clamp(value, minimum, maximum));

    private static int Direction(ScreenRect rect, ScreenRect island) => rect.Left >= island.Right ? 0
        : rect.Right <= island.Left ? 1 : rect.Top >= island.Bottom ? 2 : 3;

    private static long Distance(ScreenRect rect, ScreenRect island)
    {
        var dx = Math.Max(0L, Math.Max((long)island.Left - rect.Right, (long)rect.Left - island.Right));
        var dy = Math.Max(0L, Math.Max((long)island.Top - rect.Bottom, (long)rect.Top - island.Bottom));
        return dx + dy;
    }
}
