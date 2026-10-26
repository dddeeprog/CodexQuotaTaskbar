using CodexQuotaTaskbar.Core.Overlay;

namespace CodexQuotaTaskbar.Host.UI;

// All arguments and results are physical pixels; the chosen width remains a DIP preference.
internal static class BongoCompanionPlacement
{
    internal const double MinimumWidthDip = 140;
    internal const double MaximumWidthDip = 240;
    internal const double DefaultWidthDip = 192;
    internal const double ModelAspect = 612d / 354d;
    internal const double GapDip = 4;

    internal static double ClampWidth(double value) => double.IsFinite(value)
        ? Math.Clamp(value, MinimumWidthDip, MaximumWidthDip) : DefaultWidthDip;

    internal static bool TryCalculate(ScreenRect work, ScreenRect group, int anchorX, int dpi,
        double requestedWidthDip, out ScreenRect bounds)
    {
        bounds = default;
        if (work.Right <= work.Left || work.Bottom <= work.Top || group.Right <= group.Left || group.Bottom <= group.Top)
            return false;
        var scale = dpi is >= 48 and <= 768 ? dpi / 96d : 1;
        var gap = (int)Math.Ceiling(GapDip * scale);
        var minimum = (int)Math.Ceiling(MinimumWidthDip * scale);
        var requested = (int)Math.Round(ClampWidth(requestedWidthDip) * scale);
        var workWidth = (long)work.Right - work.Left;
        var workHeight = (long)work.Bottom - work.Top;

        // Prefer staying above the unchanged quota/ task group; shrink only as far as 140 DIP.
        foreach (var above in new[] { true, false })
        {
            var available = above ? (long)group.Top - work.Top - gap : (long)work.Bottom - group.Bottom - gap;
            var width = (int)Math.Min(requested, Math.Min(workWidth, Math.Floor(available * ModelAspect)));
            if (width < minimum) continue;
            var height = Math.Max(1, (int)Math.Ceiling(width / ModelAspect));
            if (height > available) continue;
            var left = (int)Math.Clamp((long)anchorX - width / 2, work.Left, (long)work.Right - width);
            var top = above ? group.Top - gap - height : group.Bottom + gap;
            bounds = new(left, top, left + width, top + height);
            return true;
        }

        // A capsule close to the top edge may need the model beside it instead.
        foreach (var right in new[] { true, false })
        {
            var available = right ? (long)work.Right - group.Right - gap : (long)group.Left - work.Left - gap;
            var width = (int)Math.Min(requested, Math.Min(available, Math.Floor(workHeight * ModelAspect)));
            if (width < minimum) continue;
            var height = Math.Max(1, (int)Math.Ceiling(width / ModelAspect));
            if (height > workHeight) continue;
            var left = right ? group.Right + gap : group.Left - gap - width;
            var top = (int)Math.Clamp((long)group.Top, work.Top, (long)work.Bottom - height);
            bounds = new(left, top, left + width, top + height);
            return true;
        }
        // Never cover quota or tasks to force a model into a space that cannot fit it.
        return false;
    }
}
