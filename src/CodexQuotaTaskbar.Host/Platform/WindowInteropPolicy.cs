using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CodexQuotaTaskbar.Core.Overlay;

namespace CodexQuotaTaskbar.Host.Platform;

internal static class WindowInteropPolicy
{
    internal static void AttachNoActivate(Window window)
    {
        var helper = new WindowInteropHelper(window);
        var handle = helper.Handle;
        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        var desired = new nint(style | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate);
        _ = NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, desired);
        HwndSource.FromHwnd(handle)?.AddHook(NoActivateHook);
    }

    internal static void Position(Window window, ScreenRect rect)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (!NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost, rect.Left, rect.Top, rect.Width, rect.Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法定位额度浮层。");
        }
    }

    private static nint NoActivateHook(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == NativeMethods.WmMouseActivate)
        {
            handled = true;
            return NativeMethods.MaNoActivate;
        }

        return nint.Zero;
    }
}
