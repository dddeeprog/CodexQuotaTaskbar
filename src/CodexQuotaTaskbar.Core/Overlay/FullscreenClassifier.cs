namespace CodexQuotaTaskbar.Core.Overlay;

public static class FullscreenClassifier
{
    public static bool IsTrueFullscreen(ScreenRect monitor, ScreenRect window, bool isCloaked, int tolerancePixels = 2)
    {
        if (isCloaked)
        {
            return false;
        }

        return Math.Abs(window.Left - monitor.Left) <= tolerancePixels
            && Math.Abs(window.Top - monitor.Top) <= tolerancePixels
            && Math.Abs(window.Right - monitor.Right) <= tolerancePixels
            && Math.Abs(window.Bottom - monitor.Bottom) <= tolerancePixels;
    }
}
