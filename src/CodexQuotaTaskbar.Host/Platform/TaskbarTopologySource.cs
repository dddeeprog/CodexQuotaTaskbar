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
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == nint.Zero || !NativeMethods.IsWindowVisible(foreground))
        {
            return false;
        }

        var monitor = NativeMethods.MonitorFromWindow(foreground, 2);
        var info = new NativeMethods.MonitorInfoEx { Size = Marshal.SizeOf<NativeMethods.MonitorInfoEx>(), Device = string.Empty };
        if (monitor == nint.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info)
            || !string.Equals(info.Device, anchor.MonitorId, StringComparison.Ordinal))
        {
            return false;
        }

        var cloaked = 0;
        _ = NativeMethods.DwmGetWindowAttribute(foreground, 14, out cloaked, sizeof(int));
        NativeMethods.Rect frame;
        if (NativeMethods.DwmGetWindowAttribute(foreground, 9, out frame, Marshal.SizeOf<NativeMethods.Rect>()) != 0
            && !NativeMethods.GetWindowRect(foreground, out frame))
        {
            return false;
        }

        return FullscreenClassifier.IsTrueFullscreen(ToScreenRect(info.Monitor), ToScreenRect(frame), cloaked != 0);
    }

    private static ScreenRect ToScreenRect(NativeMethods.Rect rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private static TaskbarEdge ClassifyEdge(ScreenRect taskbar, ScreenRect monitor)
    {
        if (taskbar.Width >= taskbar.Height)
        {
            return Math.Abs(taskbar.Top - monitor.Top) <= Math.Abs(monitor.Bottom - taskbar.Bottom) ? TaskbarEdge.Top : TaskbarEdge.Bottom;
        }

        return Math.Abs(taskbar.Left - monitor.Left) <= Math.Abs(monitor.Right - taskbar.Right) ? TaskbarEdge.Left : TaskbarEdge.Right;
    }
}
