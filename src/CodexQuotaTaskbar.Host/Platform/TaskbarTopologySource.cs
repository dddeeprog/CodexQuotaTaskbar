using System.Runtime.InteropServices;
using System.Text;
using CodexQuotaTaskbar.Core.Overlay;

namespace CodexQuotaTaskbar.Host.Platform;

internal sealed class TaskbarTopologySource
{
    internal IReadOnlyList<TaskbarAnchor> Capture()
    {
        var anchors = new List<TaskbarAnchor>();
        NativeMethods.EnumWindows((window, _) =>
        {
            var className = new StringBuilder(64);
            if (NativeMethods.GetClassName(window, className, className.Capacity) == 0
                || className.ToString() is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd"))
            {
                return true;
            }

            if (!NativeMethods.GetWindowRect(window, out var taskbar))
            {
                return true;
            }

            var monitorHandle = NativeMethods.MonitorFromWindow(window, 2);
            var monitor = new NativeMethods.MonitorInfoEx { Size = Marshal.SizeOf<NativeMethods.MonitorInfoEx>(), Device = string.Empty };
            if (monitorHandle == nint.Zero || !NativeMethods.GetMonitorInfo(monitorHandle, ref monitor))
            {
                return true;
            }

            var taskbarRect = ToScreenRect(taskbar);
            var monitorRect = ToScreenRect(monitor.Monitor);
            var edge = ClassifyEdge(taskbarRect, monitorRect);
            var thickness = edge is TaskbarEdge.Top or TaskbarEdge.Bottom ? taskbarRect.Height : taskbarRect.Width;
            anchors.Add(new TaskbarAnchor(
                string.IsNullOrWhiteSpace(monitor.Device) ? monitorHandle.ToString("X") : monitor.Device,
                monitorRect,
                taskbarRect,
                edge,
                checked((int)Math.Max(96, NativeMethods.GetDpiForWindow(window))),
                (monitor.Flags & 1) != 0,
                NativeMethods.IsWindowVisible(window) && thickness > 4));
            return true;
        }, nint.Zero);

        return anchors.GroupBy(anchor => anchor.MonitorId, StringComparer.Ordinal).Select(group => group.First()).ToArray();
    }

    internal static bool IsMonitorFullscreen(TaskbarAnchor anchor)
        => GetForegroundDisposition(anchor) == ForegroundDisposition.Fullscreen;

    internal static ForegroundDisposition GetForegroundDisposition(TaskbarAnchor anchor)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == nint.Zero || !NativeMethods.IsWindowVisible(foreground))
        {
            return ForegroundDisposition.Normal;
        }

        // Show Desktop makes the shell's monitor-sized surface foreground. It is
        // not a fullscreen application; otherwise Reconcile hides our overlays.
        var shell = NativeMethods.GetShellWindow();
        var foregroundClass = new StringBuilder(64);
        _ = NativeMethods.GetClassName(foreground, foregroundClass, foregroundClass.Capacity);
        _ = NativeMethods.GetWindowThreadProcessId(foreground, out var foregroundProcess);
        _ = NativeMethods.GetWindowThreadProcessId(shell, out var shellProcess);
        if (IsDesktopSurface(foreground, shell, foregroundProcess, shellProcess, foregroundClass.ToString()))
        {
            return ForegroundDisposition.Normal;
        }

        var monitor = NativeMethods.MonitorFromWindow(foreground, 2);
        var info = new NativeMethods.MonitorInfoEx { Size = Marshal.SizeOf<NativeMethods.MonitorInfoEx>(), Device = string.Empty };
        if (monitor == nint.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info)
            || !string.Equals(info.Device, anchor.MonitorId, StringComparison.Ordinal))
        {
            return ForegroundDisposition.Normal;
        }

        var cloaked = 0;
        _ = NativeMethods.DwmGetWindowAttribute(foreground, 14, out cloaked, sizeof(int));
        NativeMethods.Rect frame;
        if (NativeMethods.DwmGetWindowAttribute(foreground, 9, out frame, Marshal.SizeOf<NativeMethods.Rect>()) != 0
            && !NativeMethods.GetWindowRect(foreground, out frame))
        {
            return ForegroundDisposition.Normal;
        }

        var monitorBounds = ToScreenRect(info.Monitor);
        if (!FullscreenClassifier.IsTrueFullscreen(monitorBounds, ToScreenRect(frame), cloaked != 0))
            return ForegroundDisposition.Normal;
        return IsQqCaptureForeground(foreground, foregroundProcess, monitorBounds)
            ? ForegroundDisposition.CaptureOverlay : ForegroundDisposition.Fullscreen;
    }

    private static bool IsQqCaptureForeground(nint foreground, uint processId, ScreenRect monitor)
    {
        var window = CaptureFacts(foreground);
        if (window is null || window.Value.Class is not ("Chrome_WidgetWin_1" or "QQScreenCapture Window Class")) return false;
        var capture = window.Value.Class == "QQScreenCapture Window Class" ? window : CaptureFacts(window.Value.Owner);
        if (capture is null || capture.Value.Class != "QQScreenCapture Window Class") return false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(checked((int)processId));
            return CaptureOverlayClassifier.IsQqCapture(window.Value, capture.Value, monitor, process.ProcessName);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or OverflowException)
        { return false; } // Unknown identity stays on the existing fullscreen policy.
    }

    private static CaptureWindowFacts? CaptureFacts(nint handle)
    {
        if (handle == 0) return null;
        var name = new StringBuilder(128);
        if (NativeMethods.GetClassName(handle, name, name.Capacity) == 0
            || NativeMethods.DwmGetWindowAttribute(handle, 14, out int cloaked, sizeof(int)) != 0
            || NativeMethods.DwmGetWindowAttribute(handle, 9, out NativeMethods.Rect frame, Marshal.SizeOf<NativeMethods.Rect>()) != 0) return null;
        _ = NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        return new(handle, processId, NativeMethods.GetWindow(handle, 4), NativeMethods.MonitorFromWindow(handle, 2),
            name.ToString(), unchecked((uint)NativeMethods.GetWindowLongPtr(handle, -16).ToInt64()),
            unchecked((uint)NativeMethods.GetWindowLongPtr(handle, -20).ToInt64()),
            NativeMethods.IsWindowVisible(handle), cloaked == 0, ToScreenRect(frame));
    }

    private static ScreenRect ToScreenRect(NativeMethods.Rect rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    internal static bool IsDesktopSurface(nint window, nint shellWindow, uint processId, uint shellProcessId, string windowClass)
    {
        if (window == nint.Zero || shellWindow == nint.Zero) return false;
        if (window == shellWindow) return true;
        // Explorer can move the desktop view into WorkerW (e.g. after wallpaper
        // changes). Do not exempt every Explorer window or trust class names alone.
        return shellProcessId != 0 && processId == shellProcessId
            && windowClass is "Progman" or "WorkerW" or "SHELLDLL_DefView";
    }

    private static TaskbarEdge ClassifyEdge(ScreenRect taskbar, ScreenRect monitor)
    {
        if (taskbar.Width >= taskbar.Height)
        {
            return Math.Abs(taskbar.Top - monitor.Top) <= Math.Abs(monitor.Bottom - taskbar.Bottom) ? TaskbarEdge.Top : TaskbarEdge.Bottom;
        }

        return Math.Abs(taskbar.Left - monitor.Left) <= Math.Abs(monitor.Right - taskbar.Right) ? TaskbarEdge.Left : TaskbarEdge.Right;
    }
}
