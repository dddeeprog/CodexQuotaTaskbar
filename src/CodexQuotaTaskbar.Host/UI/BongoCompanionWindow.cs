using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Host.Bongo;
using CodexQuotaTaskbar.Host.Platform;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using Image = System.Windows.Controls.Image;

namespace CodexQuotaTaskbar.Host.UI;

// Presentation only: the service owns native rendering and input. Each handed-off frame is disposed here.
internal sealed class BongoCompanionWindow : Window, IDisposable
{
    private readonly object gate = new();
    private readonly Image image = new() { Stretch = Stretch.Uniform, IsHitTestVisible = false };
    private BongoFrame? pending;
    private WriteableBitmap? bitmap;
    private bool dispatchPending;
    private bool clearRequested;
    private bool disposed;
    private volatile bool hasFrame;
    private bool pointerDown;
    private bool dragging;
    private int pointerX, pointerY;

    internal BongoCompanionWindow()
    {
        Title = "点岛 · BongoCat";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        SnapsToDevicePixels = true;
        Content = image;
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        SetSize(BongoCompanionPlacement.DefaultWidthDip);
        SourceInitialized += (_, _) => WindowInteropPolicy.AttachNoActivate(this);
        PreviewMouseLeftButtonDown += OnPointerDown;
        PreviewMouseMove += OnPointerMove;
        PreviewMouseLeftButtonUp += OnPointerUp;
        MouseRightButtonUp += (_, args) => { ContextInvoked?.Invoke(this, EventArgs.Empty); args.Handled = true; };
        LostMouseCapture += (_, _) => EndDrag();
        Closed += (_, _) => Dispose();
    }

    internal bool HasFrame => hasFrame;
    internal BitmapSource? CurrentBitmap => bitmap;
    internal ScreenRect PhysicalBounds { get; private set; }
    internal double DesiredWidthDip { get; private set; }
    internal event EventHandler? PresentationChanged;
    internal event EventHandler? Invoked;
    internal event EventHandler? ContextInvoked;
    internal event EventHandler? DragStarted;
    internal event Action<int, int>? DragDeltaRequested;
    internal event EventHandler? DragCompleted;

    internal void SetSize(double widthDip)
    {
        VerifyAccess();
        DesiredWidthDip = BongoCompanionPlacement.ClampWidth(widthDip);
        Width = DesiredWidthDip;
        Height = Width / BongoCompanionPlacement.ModelAspect;
    }

    internal void Place(ScreenRect bounds)
    {
        VerifyAccess();
        if (disposed || bounds == PhysicalBounds) return;
        if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) throw new ArgumentOutOfRangeException(nameof(bounds));
        WindowInteropPolicy.Position(this, bounds);
        PhysicalBounds = bounds;
    }

    internal void ShowAt(ScreenRect bounds)
    {
        VerifyAccess();
        if (disposed || !HasFrame) return;
        if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) throw new ArgumentOutOfRangeException(nameof(bounds));
        if (IsVisible) { Place(bounds); return; }
        var handle = new WindowInteropHelper(this).EnsureHandle();
        // Size/position the hidden HWND first. Never expose a first frame at the default origin.
        if (!NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost, bounds.Left, bounds.Top,
            bounds.Width, bounds.Height, NativeMethods.SwpNoActivate))
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        PhysicalBounds = bounds;
        if (!IsVisible) Show();
        // Show can apply WPF's stored bounds; synchronize again in this same UI turn before painting.
        WindowInteropPolicy.Position(this, bounds);
    }

    internal void QueueFrame(BongoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        BongoFrame? dropped;
        bool schedule;
        lock (gate)
        {
            if (disposed) { frame.Dispose(); return; }
            dropped = pending;
            pending = frame;
            clearRequested = false;
            schedule = !dispatchPending;
            dispatchPending = true;
        }
        dropped?.Dispose();
        if (schedule) ScheduleDrain();
    }

    internal void ClearFrame()
    {
        BongoFrame? dropped;
        bool schedule;
        lock (gate)
        {
            if (disposed) return;
            dropped = pending;
            pending = null;
            clearRequested = true;
            schedule = !dispatchPending;
            dispatchPending = true;
        }
        dropped?.Dispose();
        // Restore the white-ball slot immediately when failure is observed on the UI thread.
        if (Dispatcher.CheckAccess()) ClearBitmap();
        if (schedule) ScheduleDrain();
    }

    // The coordinator already owns one UI-dispatch mailbox. Avoid adding a second hop there.
    internal void PresentFrame(BongoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        VerifyAccess();
        BongoFrame? dropped;
        lock (gate)
        {
            if (disposed) { frame.Dispose(); return; }
            dropped = pending;
            pending = null;
            clearRequested = false;
        }
        dropped?.Dispose();
        Present(frame);
    }

    private void ScheduleDrain()
    {
        try
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) { DropPending(); return; }
            var operation = Dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)Drain);
            if (operation.Status == DispatcherOperationStatus.Aborted) DropPending();
        }
        catch (InvalidOperationException) { DropPending(); }
    }

    private void Drain()
    {
        BongoFrame? frame;
        bool clear;
        lock (gate)
        {
            frame = pending;
            pending = null;
            clear = clearRequested || disposed;
            clearRequested = false;
            dispatchPending = false;
        }
        if (clear) { frame?.Dispose(); ClearBitmap(); return; }
        if (frame is not null) Present(frame);
    }

    private void Present(BongoFrame frame)
    {
        try
        {
            var required = (long)frame.Width * frame.Height * 4;
            if (frame.Width is < 1 or > 1024 || frame.Height is < 1 or > 1024 || required > 4 * 1024 * 1024
                || frame.ByteCount != required || frame.Pixels.Length < required) { ClearBitmap(); return; }
            if (bitmap is null || bitmap.PixelWidth != frame.Width || bitmap.PixelHeight != frame.Height)
            {
                bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Pbgra32, null);
                image.Source = bitmap;
            }
            bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Width * 4, 0);
            SetHasFrame(true);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            ClearBitmap();
        }
        finally { frame.Dispose(); }
    }

    private void SetHasFrame(bool value)
    {
        if (hasFrame == value) return;
        hasFrame = value;
        PresentationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearBitmap()
    {
        bitmap = null;
        image.Source = null;
        SetHasFrame(false);
    }

    private void DropPending()
    {
        BongoFrame? dropped;
        lock (gate) { dropped = pending; pending = null; dispatchPending = false; clearRequested = false; }
        dropped?.Dispose();
    }

    private void OnPointerDown(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton != MouseButton.Left || !WindowInteropPolicy.TryGetCursorPosition(out pointerX, out pointerY)) return;
        pointerDown = true;
        dragging = false;
        Mouse.Capture(this);
        args.Handled = true;
    }

    private void OnPointerMove(object sender, System.Windows.Input.MouseEventArgs args)
    {
        if (!pointerDown || args.LeftButton != MouseButtonState.Pressed
            || !WindowInteropPolicy.TryGetCursorPosition(out var x, out var y)) return;
        var dx = x - pointerX;
        var dy = y - pointerY;
        if (!dragging && !QuotaCapsuleWindow.ExceedsDragThreshold(dx, dy)) return;
        if (!dragging) { dragging = true; Cursor = Cursors.SizeAll; DragStarted?.Invoke(this, EventArgs.Empty); }
        DragDeltaRequested?.Invoke(dx, dy);
        args.Handled = true;
    }

    private void OnPointerUp(object sender, MouseButtonEventArgs args)
    {
        if (!pointerDown || args.ChangedButton != MouseButton.Left) return;
        var invoke = !dragging;
        EndDrag();
        ReleaseMouseCapture();
        if (invoke) Invoked?.Invoke(this, EventArgs.Empty);
        args.Handled = true;
    }

    private void EndDrag()
    {
        var completed = dragging;
        pointerDown = dragging = false;
        Cursor = Cursors.Arrow;
        if (completed) DragCompleted?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        DropPending();
        if (Dispatcher.CheckAccess()) { EndDrag(); ClearBitmap(); }
    }
}
