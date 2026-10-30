using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.Platform;

namespace CodexQuotaTaskbar.Host.UI;

public partial class TrayMenuWindow
{
    private MenuTraceWindowObserver? menuTrace;

    private void InitializeMenuTrace()
    {
        try
        {
            var session = MenuTraceSession.Local; // Eager TrayController construction writes Armed before any click.
            if (session.Enabled) menuTrace = new(this, session);
        }
        catch { /* Optional diagnostics cannot prevent normal startup. */ }
    }

    internal MenuTraceState CaptureMenuTrace(MenuTraceSession session, long generation)
    {
        T Read<T>(Func<T> read) => session.Read(generation, read);
        var handle = Read(() => new WindowInteropHelper(this).Handle); // Never creates a handle.
        var bounds = default(MenuTraceBounds);
        if (handle != 0)
        {
            var own = Read(() =>
            {
                var available = NativeMethods.GetWindowRect(handle, out var rect);
                return (Available: available, Rect: rect);
            });
            if (own.Available)
                bounds = new(own.Rect.Left, own.Rect.Top, own.Rect.Right, own.Rect.Bottom, Read(() => NativeMethods.GetDpiForWindow(handle)));
        }
        var foreground = Read(NativeMethods.GetForegroundWindow);
        var role = foreground == 0 ? MenuTraceRole.None : foreground == handle ? MenuTraceRole.Menu : MenuTraceRole.Other;
        if (role == MenuTraceRole.Other && Read(() => System.Windows.Application.Current) is { } app)
        {
            var windows = Read(() => app.Windows);
            for (var index = 0; index < Read(() => windows.Count); index++)
            {
                var window = Read(() => windows[index]);
                if (window is not (TrayMenuWindow or QuotaPopoverWindow or QuotaCapsuleWindow)) continue;
                if (Read(() => new WindowInteropHelper(window).Handle) != foreground) continue;
                role = window is TrayMenuWindow ? MenuTraceRole.Menu
                    : window is QuotaPopoverWindow ? MenuTraceRole.Details : MenuTraceRole.Capsule;
                break;
            }
        }
        var transform = Read(() => MenuContent.RenderTransform);
        var matrix = Read(() => transform.Value);
        return new(Read(() => IsVisible), Read(() => IsLoaded), preparingPopup, IsPopupClosing, role,
            new(Read(() => MenuShell.Opacity), Read(() => menuPopupScale.ScaleX), Read(() => menuPopupScale.ScaleY),
                Read(() => menuPopupTranslate.X), Read(() => menuPopupTranslate.Y)),
            new(Read(() => MenuContent.Opacity), matrix.M11, matrix.M22, matrix.OffsetX, matrix.OffsetY), bounds,
            MotionGeneration: PopupTransitionGeneration, PendingGeneration: popupPendingEntranceGeneration ?? 0);
    }
}

// Own-HWND messages are observed unchanged. Rendering is a WPF callback, not DWM
// present evidence. Session admission is revoked by a background deadline; when the
// UI thread is blocked, actual unsubscription waits for that Dispatcher to run.
internal sealed class MenuTraceWindowObserver : IDisposable
{
    private readonly TrayMenuWindow window;
    private readonly MenuTraceSession session;
    private static long nextEpisode;
    private long episode;
    private bool renderingAttached;
    private bool disposed;
    private HwndSource? source;

    internal MenuTraceWindowObserver(TrayMenuWindow window, MenuTraceSession session)
    {
        this.window = window; this.session = session;
        if (!session.Enabled) return;
        window.Closed += OnClosed;
        session.EpisodeStopped += OnStopped;
    }

    internal bool Active => session.IsActive(episode);

    internal void Begin()
    {
        if (disposed || !session.Enabled) return;
        try
        {
            if (Active) { Record(MenuTraceKind.ShowRejected); return; }
            Detach();
            episode = Interlocked.Increment(ref nextEpisode);
            var generation = episode;
            if (!session.Begin(generation, OverlayLifecycleDiagnostics.Local.LastMenuTraceInput,
                () => window.CaptureMenuTrace(session, generation))) return;
            CompositionTarget.Rendering += OnRendering;
            renderingAttached = true;
            window.SourceInitialized += OnSourceInitialized;
            AttachNative();
        }
        catch { Stop(MenuTraceStop.Exception); }
    }

    internal void Record(MenuTraceKind kind, bool result = false, int nativeValue = 0, MenuTraceBounds? requested = null)
    {
        var generation = episode;
        session.Observe(generation, kind, () =>
        {
            var state = window.CaptureMenuTrace(session, generation);
            return state with { Result = result, NativeValue = nativeValue, Bounds = requested ?? state.Bounds };
        });
    }

    internal void Stop(MenuTraceStop reason)
    {
        try { session.Stop(episode, reason); }
        finally { Detach(); }
    }

    private void OnRendering(object? sender, EventArgs args) => Record(MenuTraceKind.FrameSample);
    private void OnSourceInitialized(object? sender, EventArgs args)
    {
        if (!Active) return;
        try { AttachNative(); } catch { Stop(MenuTraceStop.Exception); }
    }
    private void AttachNative()
    {
        if (!Active || source is not null) return;
        var generation = episode;
        var handle = session.Read(generation, () => new WindowInteropHelper(window).Handle);
        if (handle == 0) return;
        source = session.Read(generation, () => HwndSource.FromHwnd(handle));
        if (session.IsActive(generation)) source?.AddHook(OnMessage);
    }
    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // Do not inspect lParam pointers, external identities, or modify handled.
        if (message == 0x0018) Record(MenuTraceKind.NativeShow, nativeValue: wParam == 0 ? 0 : 1);
        else if (message == 0x0006) Record(MenuTraceKind.NativeActivation, nativeValue: (int)((long)wParam & 0xffff));
        else if (message is 0x0046 or 0x0047) Record(MenuTraceKind.NativePosition, nativeValue: message);
        return 0;
    }
    private void OnStopped(long stoppedEpisode)
    {
        if (stoppedEpisode != episode) return;
        if (window.Dispatcher.CheckAccess()) { Detach(); return; }
        try
        {
            window.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                if (stoppedEpisode == episode) Detach();
            }));
        }
        catch { /* Dispatcher shutdown also removes its HWND and callbacks. */ }
    }
    private void Detach()
    {
        try { source?.RemoveHook(OnMessage); }
        catch { }
        finally
        {
            source = null;
            window.SourceInitialized -= OnSourceInitialized;
            if (renderingAttached) CompositionTarget.Rendering -= OnRendering;
            renderingAttached = false;
        }
    }
    private void OnClosed(object? sender, EventArgs args) => Dispose();
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { Stop(MenuTraceStop.Disposed); }
        finally
        {
            session.EpisodeStopped -= OnStopped;
            window.Closed -= OnClosed;
        }
    }
}
