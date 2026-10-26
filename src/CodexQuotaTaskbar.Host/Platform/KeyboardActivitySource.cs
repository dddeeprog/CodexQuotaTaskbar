using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace CodexQuotaTaskbar.Host.Platform;

internal interface IKeyboardActivitySource : IDisposable
{
    event Action<CompanionInputEvent>? Activity;
    bool IsRunning { get; }
    bool TryStart();
    void Stop();
}

internal interface IKeyboardActivityWindow : IDisposable
{
    IntPtr Handle { get; }
}

internal interface IKeyboardActivityNative
{
    bool CheckAccess();
    IKeyboardActivityWindow CreateWindow(HwndSourceHook hook);
    bool Register(IntPtr target, ushort usage, bool remove);
    uint ReadInput(IntPtr input, IntPtr destination, ref uint size, uint headerSize);
}

/// <summary>
/// Opt-in ordinary-desktop physical state, used only for immediate local animation.
/// Reads MakeCode/break/E0 and left/right mouse-button flags. Never translates text,
/// reads window details, retains device identity, or stores an input sequence.
/// State transitions are never throttled: dropping key-up would leave a stuck paw.
/// Create/start/stop this source on its owning UI thread. Construction alone registers nothing.
/// </summary>
internal sealed class KeyboardActivitySource : IKeyboardActivitySource
{
    private const int WmInput = 0x00ff;
    private const int WmInputDeviceChange = 0x00fe;
    private const int MouseDeviceType = 0;
    private const int KeyboardDeviceType = 1;
    internal static int HeaderByteCount => 8 + 2 * IntPtr.Size;
    internal static int BufferByteCount => HeaderByteCount + 24;
    private readonly IKeyboardActivityNative native;
    private readonly HwndSourceHook messageHook;
    private IKeyboardActivityWindow? window;
    private IntPtr buffer;
    private bool keyboardRegistered, mouseRegistered;
    private bool disposed;

    internal KeyboardActivitySource() : this(new Win32KeyboardActivityNative()) { }

    internal KeyboardActivitySource(IKeyboardActivityNative native)
    {
        this.native = native ?? throw new ArgumentNullException(nameof(native));
        messageHook = OnMessage;
    }

    public event Action<CompanionInputEvent>? Activity;
    public bool IsRunning { get; private set; }

    public bool TryStart()
    {
        if (disposed || !native.CheckAccess()) return false;
        if (IsRunning) return true;
        try
        {
            buffer = Marshal.AllocHGlobal(BufferByteCount);
            ClearBuffer();
            window = native.CreateWindow(messageHook);
            if (window.Handle == IntPtr.Zero || !native.Register(window.Handle, 6, remove: false))
            {
                Stop();
                return false;
            }
            keyboardRegistered = true;
            if (!native.Register(window.Handle, 2, remove: false))
            {
                Stop();
                return false;
            }
            mouseRegistered = true;
            IsRunning = true;
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Stop();
            return false;
        }
    }

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmInputDeviceChange && wParam == new IntPtr(2) && IsRunning && !disposed)
        {
            // A removed device may never send its releases. Read no device identity.
            try { Activity?.Invoke(new(CompanionInputKind.Reset)); }
            catch (Exception error) when (error is not OutOfMemoryException) { Stop(); }
            return IntPtr.Zero;
        }
        if (message != WmInput) return IntPtr.Zero;
        // In particular, foreground WM_INPUT must reach DefWindowProc for OS cleanup.
        handled = false;
        if (!IsRunning || disposed || buffer == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            var key = default(CompanionInputEvent);
            var hasKey = false;
            ushort mouseFlags = 0;
            bool valid;
            try
            {
                var size = (uint)BufferByteCount;
                var copied = native.ReadInput(lParam, buffer, ref size, (uint)HeaderByteCount);
                valid = copied >= HeaderByteCount && copied <= BufferByteCount && size >= copied && size <= BufferByteCount;
                if (valid)
                {
                    var type = Marshal.ReadInt32(buffer);
                    valid = unchecked((uint)Marshal.ReadInt32(buffer, 4)) == copied;
                    if (valid && type == KeyboardDeviceType)
                    {
                        valid = copied >= HeaderByteCount + 16;
                        if (valid)
                        {
                            var make = unchecked((ushort)Marshal.ReadInt16(buffer, HeaderByteCount));
                            var flags = unchecked((ushort)Marshal.ReadInt16(buffer, HeaderByteCount + 2));
                            // Ignore E1/Pause, overrun and invalid physical codes; do not guess a key from text/VKey.
                            if (make is > 0 and < 0x80 && (flags & ~3) == 0)
                            {
                                hasKey = true;
                                key = new(CompanionInputKind.Key, (ushort)(make | ((flags & 2) != 0 ? 0x100 : 0)), (flags & 1) == 0);
                            }
                        }
                    }
                    else if (valid && type == MouseDeviceType)
                    {
                        valid = copied >= HeaderByteCount + 24;
                        if (valid) mouseFlags = unchecked((ushort)Marshal.ReadInt16(buffer, HeaderByteCount + 4));
                    }
                }
            }
            finally { ClearBuffer(); }
            if (!valid)
            {
                Stop();
                return IntPtr.Zero;
            }
            // Every payload byte is zero before delivery. A subscriber may synchronously stop this source.
            if (hasKey) Activity?.Invoke(key);
            EmitMouse(mouseFlags, 1, CompanionMouseButton.Left, true);
            EmitMouse(mouseFlags, 2, CompanionMouseButton.Left, false);
            EmitMouse(mouseFlags, 4, CompanionMouseButton.Right, true);
            EmitMouse(mouseFlags, 8, CompanionMouseButton.Right, false);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Stop(); }
        return IntPtr.Zero;
    }

    private void EmitMouse(ushort flags, ushort flag, CompanionMouseButton button, bool down)
    {
        if ((flags & flag) != 0 && IsRunning && !disposed)
            Activity?.Invoke(new(CompanionInputKind.MouseButton, IsDown: down, MouseButton: button));
    }

    private void ClearBuffer()
    {
        if (buffer == IntPtr.Zero) return;
        for (var index = 0; index < BufferByteCount; index++) Marshal.WriteByte(buffer, index, 0);
    }

    public void Stop()
    {
        var wasRunning = IsRunning;
        IsRunning = false;
        var removeKeyboard = keyboardRegistered;
        var removeMouse = mouseRegistered;
        keyboardRegistered = mouseRegistered = false;
        var ownedWindow = window;
        window = null;
        try
        {
            if (removeKeyboard) Remove(6);
            if (removeMouse) Remove(2);
            try { ownedWindow?.Dispose(); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }
        finally
        {
            ClearBuffer();
            if (buffer != IntPtr.Zero)
            {
                var ownedBuffer = buffer;
                buffer = IntPtr.Zero;
                Marshal.FreeHGlobal(ownedBuffer);
            }
            if (wasRunning)
                try { Activity?.Invoke(new(CompanionInputKind.Reset)); }
                catch (Exception error) when (error is not OutOfMemoryException) { }
        }
    }

    private void Remove(ushort usage)
    {
        try { _ = native.Register(IntPtr.Zero, usage, remove: true); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Stop();
        Activity = null;
    }

    private sealed class Win32KeyboardActivityNative : IKeyboardActivityNative
    {
        private readonly int ownerThread = Environment.CurrentManagedThreadId;
        public bool CheckAccess() => Environment.CurrentManagedThreadId == ownerThread;
        public IKeyboardActivityWindow CreateWindow(HwndSourceHook hook) => new MessageWindow(hook);

        public bool Register(IntPtr target, ushort usage, bool remove) => RegisterRawInputDevices(
            [new RawInputDevice { UsagePage = 1, Usage = usage, Flags = remove ? 1u : 0x2100u, Target = remove ? IntPtr.Zero : target }],
            1, (uint)Marshal.SizeOf<RawInputDevice>());

        public uint ReadInput(IntPtr input, IntPtr destination, ref uint size, uint headerSize) =>
            GetRawInputData(input, 0x10000003, destination, ref size, headerSize); // RID_INPUT, fixed bounded buffer.

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputDevice
        {
            internal ushort UsagePage;
            internal ushort Usage;
            internal uint Flags;
            internal IntPtr Target;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint structureSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(IntPtr input, uint command, IntPtr destination, ref uint size, uint headerSize);

        private sealed class MessageWindow : IKeyboardActivityWindow
        {
            private readonly HwndSource source;
            private readonly HwndSourceHook hook;
            private bool disposed;

            internal MessageWindow(HwndSourceHook hook)
            {
                this.hook = hook;
                source = new HwndSource(new HwndSourceParameters("DianDaoKeyboardActivity")
                {
                    ParentWindow = new IntPtr(-3), // HWND_MESSAGE: never visible or focusable.
                    WindowStyle = 0, Width = 0, Height = 0,
                });
                try { source.AddHook(hook); }
                catch { source.Dispose(); throw; }
            }

            public IntPtr Handle => source.Handle;

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                try { source.RemoveHook(hook); }
                finally { source.Dispose(); }
            }
        }
    }
}
