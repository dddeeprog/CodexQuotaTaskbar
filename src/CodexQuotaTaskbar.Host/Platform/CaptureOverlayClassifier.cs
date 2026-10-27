using CodexQuotaTaskbar.Core.Overlay;

namespace CodexQuotaTaskbar.Host.Platform;

internal enum ForegroundDisposition { Normal, Fullscreen, CaptureOverlay }
internal readonly record struct CaptureWindowFacts(nint Handle, uint ProcessId, nint Owner, nint Monitor,
    string Class, uint Style, uint ExtendedStyle, bool Visible, bool Uncloaked, ScreenRect Frame);

internal static class CaptureOverlayClassifier
{
    // Observed together in the user's 2026-10-27 manual QQ capture trace.
    // Neither Chrome's shared class nor popup/topmost styles alone are an exemption.
    internal static bool IsQqCapture(CaptureWindowFacts foreground, CaptureWindowFacts capture,
        ScreenRect monitor, string processName)
    {
        if (!string.Equals(processName, "QQ", StringComparison.OrdinalIgnoreCase)
            || foreground.Handle == 0 || capture.Handle == 0 || foreground.ProcessId == 0
            || foreground.ProcessId != capture.ProcessId || foreground.Monitor == 0
            || foreground.Monitor != capture.Monitor || !foreground.Visible || !capture.Visible
            || !foreground.Uncloaked || !capture.Uncloaked
            || capture.Class != "QQScreenCapture Window Class") return false;
        const uint popup = 0x80000000, child = 0x40000000, caption = 0x00C00000;
        const uint captureExtended = 0x08000088; // NOACTIVATE | TOOLWINDOW | TOPMOST
        if ((capture.Style & (popup | child | caption)) != popup
            || (capture.ExtendedStyle & captureExtended) != captureExtended) return false;
        if (foreground.Handle != capture.Handle
            && (foreground.Owner != capture.Handle || foreground.Class != "Chrome_WidgetWin_1"
                || (foreground.Style & (child | caption)) != 0
                || (foreground.ExtendedStyle & 0x00080008) != 0x00080008)) return false;
        return FullscreenClassifier.IsTrueFullscreen(monitor, foreground.Frame, false)
            && FullscreenClassifier.IsTrueFullscreen(monitor, capture.Frame, false);
    }
}
