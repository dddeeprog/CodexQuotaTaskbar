using System.Runtime.InteropServices;
using System.Windows.Interop;
using CodexQuotaTaskbar.Host.Platform;

namespace CodexQuotaTaskbar.Host.Tests.Platform;

// All packets and native operations are synthetic: no real listener or input injection.
public sealed class KeyboardActivitySourceTests
{
    [Fact]
    public void Construction_is_inert_and_start_stop_registers_both_classes_idempotently()
    {
        using var f = new Fixture();
        Assert.False(f.Source.IsRunning); Assert.Equal(0, f.Native.CreateCalls); Assert.Empty(f.Native.Registrations);
        Assert.True(f.Source.TryStart()); Assert.True(f.Source.TryStart()); Assert.Equal(1, f.Native.CreateCalls);
        Assert.Equal(new ushort[] { 6, 2 }, f.Native.Registrations.Select(r => r.Usage));
        Assert.All(f.Native.Registrations, r => { Assert.False(r.Remove); Assert.Equal(f.Native.WindowHandle, r.Target); });
        f.Source.Stop(); f.Source.Stop();
        Assert.False(f.Source.IsRunning); Assert.Equal(1, f.Native.WindowDisposeCalls);
        Assert.Equal(new ushort[] { 6, 2 }, f.Native.Registrations.Where(r => r.Remove).Select(r => r.Usage));
        Assert.All(f.Native.Registrations.Where(r => r.Remove), r => Assert.Equal(IntPtr.Zero, r.Target));
        Assert.Equal(new CompanionInputEvent(CompanionInputKind.Reset), Assert.Single(f.Events));
    }

    [Fact]
    public void Non_owner_start_has_no_native_effect()
    {
        using var f = new Fixture(); f.Native.AccessAllowed = false;
        Assert.False(f.Source.TryStart()); Assert.Equal(0, f.Native.CreateCalls); Assert.Empty(f.Native.Registrations);
    }

    [Theory]
    [InlineData(6, false)] [InlineData(6, true)] [InlineData(2, false)] [InlineData(2, true)]
    public void Failed_registration_releases_only_owned_classes(int failedUsage, bool throws)
    {
        using var f = new Fixture(); f.Native.FailedUsage = (ushort)failedUsage; f.Native.ThrowOnRegister = throws;
        Assert.False(f.Source.TryStart()); Assert.False(f.Source.IsRunning); Assert.Equal(1, f.Native.WindowDisposeCalls);
        Assert.Equal(failedUsage == 2 ? new ushort[] { 6 } : Array.Empty<ushort>(), f.Native.Registrations.Where(r => r.Remove).Select(r => r.Usage));
        Assert.Empty(f.Events);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Invalid_window_cannot_register(bool throws)
    {
        using var f = new Fixture(); f.Native.ThrowOnCreate = throws; f.Native.WindowHandle = IntPtr.Zero;
        Assert.False(f.Source.TryStart()); Assert.Empty(f.Native.Registrations); Assert.Equal(throws ? 0 : 1, f.Native.WindowDisposeCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void A_physical_press_and_release_are_delivered_with_zeroed_payload(int inputCode)
    {
        using var f = new Fixture(); var cleared = true;
        f.Source.Activity += e => { if (e.Kind != CompanionInputKind.Reset) cleared &= Enumerable.Range(0, KeyboardActivitySource.BufferByteCount)
            .All(offset => Marshal.ReadByte(f.Native.LastBuffer, offset) == 0); };
        Assert.True(f.Source.TryStart()); var result = f.Native.Emit(inputCode: inputCode, handled: true);
        f.Native.Flags = 1; f.Native.Emit();
        Assert.Equal(new[] { new CompanionInputEvent(CompanionInputKind.Key, 0x1e, true), new CompanionInputEvent(CompanionInputKind.Key, 0x1e, false) }, f.Events);
        Assert.True(cleared); Assert.False(result.Handled); Assert.Equal(IntPtr.Zero, result.Result);
        Assert.Equal((uint)KeyboardActivitySource.HeaderByteCount, f.Native.LastHeaderSize);
        Assert.Equal((uint)KeyboardActivitySource.BufferByteCount, f.Native.LastBufferSize);
    }

    [Theory]
    [InlineData(0x1d, 0, 0x1d, true)] [InlineData(0x1d, 1, 0x1d, false)]
    [InlineData(0x1d, 2, 0x11d, true)] [InlineData(0x1d, 3, 0x11d, false)]
    [InlineData(0x38, 2, 0x138, true)] [InlineData(0x2a, 0, 0x2a, true)] [InlineData(0x39, 0, 0x39, true)]
    public void Extended_keys_and_modifiers_keep_physical_identity(int scan, int flags, int expected, bool down)
    {
        using var f = new Fixture(); f.Native.Scan = (ushort)scan; f.Native.Flags = (ushort)flags;
        Assert.True(f.Source.TryStart()); f.Native.Emit();
        Assert.Equal(new CompanionInputEvent(CompanionInputKind.Key, (ushort)expected, down), Assert.Single(f.Events));
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(0xff, 0)] [InlineData(0x80, 0)] [InlineData(0x1d, 4)] [InlineData(0x1d, 8)]
    public void Invalid_or_E1_codes_are_ignored_without_guessing_VKey(int scan, int flags)
    {
        using var f = new Fixture(); f.Native.Scan = (ushort)scan; f.Native.Flags = (ushort)flags;
        Assert.True(f.Source.TryStart()); f.Native.Emit(); Assert.Empty(f.Events); Assert.True(f.Source.IsRunning);
    }

    [Fact]
    public void Fast_transitions_and_repeat_keydown_are_not_throttled_or_queued()
    {
        using var f = new Fixture(); Assert.True(f.Source.TryStart());
        for (var i = 0; i < 5000; i++) { f.Native.Flags = (ushort)(i % 2); f.Native.Emit(); }
        f.Native.Flags = 0; f.Native.Emit(); f.Native.Emit();
        Assert.Equal(5002, f.Events.Count); Assert.Equal(5002, f.Native.ReadCalls);
        Assert.False(f.Events[4999].IsDown); Assert.True(f.Events[5000].IsDown); Assert.True(f.Events[5001].IsDown);
    }

    [Fact]
    public void Mouse_buttons_are_independent_and_non_button_motion_emits_nothing()
    {
        using var f = new Fixture(); f.Native.Type = 0; Assert.True(f.Source.TryStart());
        f.Native.MouseFlags = 1 | 4; f.Native.Emit(); f.Native.MouseFlags = 2; f.Native.Emit(); f.Native.MouseFlags = 8; f.Native.Emit();
        f.Native.MouseFlags = 0x400 | 0x10; f.Native.Emit();
        Assert.Equal(new[] { new CompanionInputEvent(CompanionInputKind.MouseButton, IsDown: true, MouseButton: CompanionMouseButton.Left),
            new CompanionInputEvent(CompanionInputKind.MouseButton, IsDown: true, MouseButton: CompanionMouseButton.Right),
            new CompanionInputEvent(CompanionInputKind.MouseButton, IsDown: false, MouseButton: CompanionMouseButton.Left),
            new CompanionInputEvent(CompanionInputKind.MouseButton, IsDown: false, MouseButton: CompanionMouseButton.Right) }, f.Events);
    }

    [Fact]
    public void Unknown_device_and_unrelated_messages_do_not_emit_or_consume_state()
    {
        using var f = new Fixture(); Assert.True(f.Source.TryStart());
        Assert.True(f.Native.Emit(message: 0x0010, handled: true).Handled); Assert.Equal(0, f.Native.ReadCalls);
        f.Native.Type = 2; f.Native.Emit(); Assert.Empty(f.Events); Assert.True(f.Source.IsRunning);
        f.Native.Type = 1; f.Native.Emit(); Assert.Single(f.Events);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Keyboard_packet_accepts_compact_or_union_padded_size(bool padded)
    {
        using var f = new Fixture(); f.Native.PaddedKeyboard = padded;
        Assert.True(f.Source.TryStart()); f.Native.Emit(); Assert.Single(f.Events); Assert.True(f.Source.IsRunning);
    }

    [Theory]
    [InlineData("error")] [InlineData("short-header")] [InlineData("short-keyboard")] [InlineData("over-capacity")]
    [InlineData("reported-small")] [InlineData("reported-large")] [InlineData("header-mismatch")] [InlineData("short-mouse")]
    public void Malformed_packet_fails_closed_and_resets_held_state(string kind)
    {
        using var f = new Fixture(); Assert.True(f.Source.TryStart()); f.Native.Emit();
        var h = (uint)KeyboardActivitySource.HeaderByteCount; var cap = (uint)KeyboardActivitySource.BufferByteCount;
        switch (kind)
        {
            case "error": f.Native.CopiedBytes = uint.MaxValue; break;
            case "short-header": f.Native.CopiedBytes = h - 1; break;
            case "short-keyboard": f.Native.CopiedBytes = h + 15; break;
            case "over-capacity": f.Native.CopiedBytes = cap + 1; break;
            case "reported-small": f.Native.ReportedSize = h; break;
            case "reported-large": f.Native.ReportedSize = cap + 1; break;
            case "header-mismatch": f.Native.HeaderSize = h + 1; break;
            case "short-mouse": f.Native.Type = 0; f.Native.CopiedBytes = cap - 1; break;
        }
        f.Native.Emit(); Assert.False(f.Source.IsRunning); Assert.Equal(CompanionInputKind.Reset, f.Events[^1].Kind);
        Assert.Equal(2, f.Events.Count); Assert.Equal(1, f.Native.WindowDisposeCalls); Assert.Equal(2, f.Native.Registrations.Count(r => r.Remove));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Read_or_subscriber_failure_stops_and_resets_without_escaping(bool subscriberThrows)
    {
        using var f = new Fixture(); Assert.True(f.Source.TryStart()); f.Native.ThrowOnRead = !subscriberThrows;
        if (subscriberThrows) f.Source.Activity += _ => throw new InvalidOperationException("synthetic failure");
        Assert.Null(Record.Exception(() => f.Native.Emit())); Assert.False(f.Source.IsRunning);
        Assert.Equal(CompanionInputKind.Reset, f.Events[^1].Kind); Assert.Equal(1, f.Native.WindowDisposeCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Subscriber_stop_during_mouse_event_prevents_later_button_delivery(bool dispose)
    {
        using var f = new Fixture(); f.Native.Type = 0; f.Native.MouseFlags = 1 | 4;
        f.Source.Activity += e => { if (e.Kind != CompanionInputKind.Reset) { if (dispose) f.Source.Dispose(); else f.Source.Stop(); } };
        Assert.True(f.Source.TryStart()); Assert.Null(Record.Exception(() => f.Native.Emit()));
        Assert.Equal(2, f.Events.Count); Assert.Equal(CompanionMouseButton.Left, f.Events[0].MouseButton);
        Assert.Equal(CompanionInputKind.Reset, f.Events[1].Kind); Assert.False(f.Source.IsRunning);
        var reads = f.Native.ReadCalls; f.Native.Emit(); Assert.Equal(reads, f.Native.ReadCalls);
    }

    [Fact]
    public void Stop_disables_callback_before_native_teardown_and_reset_is_once()
    {
        using var f = new Fixture(); Assert.True(f.Source.TryStart()); f.Native.OnWindowDispose = () => f.Native.Emit();
        f.Source.Stop(); f.Source.Stop(); Assert.Equal(0, f.Native.ReadCalls); Assert.Equal(CompanionInputKind.Reset, Assert.Single(f.Events).Kind);
    }

    [Fact]
    public void Restart_is_fresh_and_dispose_prevents_restart()
    {
        using var f = new Fixture(); Assert.True(f.Source.TryStart()); f.Native.Emit(); f.Source.Stop();
        Assert.True(f.Source.TryStart()); f.Native.Emit(); f.Source.Dispose(); f.Source.Dispose();
        Assert.False(f.Source.TryStart()); Assert.Equal(2, f.Native.CreateCalls); Assert.Equal(2, f.Native.WindowDisposeCalls);
        Assert.Equal(new[] { CompanionInputKind.Key, CompanionInputKind.Reset, CompanionInputKind.Key, CompanionInputKind.Reset }, f.Events.Select(e => e.Kind));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Removal_failure_still_removes_other_class_and_disposes_window(bool throws)
    {
        using var f = new Fixture(); Assert.True(f.Source.TryStart()); f.Native.RemoveSucceeds = false; f.Native.ThrowOnRemove = throws;
        Assert.Null(Record.Exception(f.Source.Stop)); Assert.False(f.Source.IsRunning);
        Assert.Equal(2, f.Native.Registrations.Count(r => r.Remove)); Assert.Equal(1, f.Native.WindowDisposeCalls);
    }

    [Fact]
    public void Device_removal_clears_state_without_reading_identity_or_stopping_listener()
    {
        using var f = new Fixture(); Assert.True(f.Source.TryStart()); f.Native.Emit();
        f.Native.Emit(message: 0x00fe, inputCode: 2); Assert.Equal(1, f.Native.ReadCalls);
        Assert.Equal(CompanionInputKind.Reset, f.Events[^1].Kind); Assert.True(f.Source.IsRunning);
        f.Native.Emit(message: 0x00fe, inputCode: 1); Assert.Equal(2, f.Events.Count);
    }

    private sealed class Fixture : IDisposable
    {
        internal FakeNative Native { get; } = new();
        internal KeyboardActivitySource Source { get; }
        internal List<CompanionInputEvent> Events { get; } = [];
        internal Fixture() { Source = new(Native); Source.Activity += Events.Add; }
        public void Dispose() => Source.Dispose();
    }

    private sealed class FakeNative : IKeyboardActivityNative
    {
        internal bool AccessAllowed = true, RemoveSucceeds = true;
        internal bool ThrowOnCreate, ThrowOnRegister, ThrowOnRemove, ThrowOnRead;
        internal ushort? FailedUsage;
        internal uint Type = 1;
        internal ushort Scan = 0x1e, Flags, MouseFlags;
        internal bool PaddedKeyboard;
        internal uint? CopiedBytes, ReportedSize, HeaderSize;
        internal IntPtr WindowHandle = new(42), LastBuffer;
        internal int CreateCalls, WindowDisposeCalls, ReadCalls;
        internal uint LastHeaderSize, LastBufferSize;
        internal List<(IntPtr Target, ushort Usage, bool Remove)> Registrations { get; } = [];
        internal Action? OnWindowDispose;
        private HwndSourceHook? hook;

        public bool CheckAccess() => AccessAllowed;
        public IKeyboardActivityWindow CreateWindow(HwndSourceHook messageHook)
        {
            CreateCalls++; if (ThrowOnCreate) throw new InvalidOperationException("synthetic create failure");
            hook = messageHook; return new FakeWindow(this);
        }
        public bool Register(IntPtr target, ushort usage, bool remove)
        {
            Registrations.Add((target, usage, remove));
            if (remove && ThrowOnRemove || !remove && FailedUsage == usage && ThrowOnRegister)
                throw new InvalidOperationException("synthetic registration failure");
            return remove ? RemoveSucceeds : FailedUsage != usage;
        }
        public uint ReadInput(IntPtr input, IntPtr destination, ref uint size, uint headerSize)
        {
            ReadCalls++; LastBuffer = destination; LastHeaderSize = headerSize; LastBufferSize = size;
            if (ThrowOnRead) throw new InvalidOperationException("synthetic read failure");
            var count = CopiedBytes ?? (uint)(KeyboardActivitySource.HeaderByteCount + (Type == 1 && !PaddedKeyboard ? 16 : 24));
            for (var i = 0; i < KeyboardActivitySource.BufferByteCount; i++) Marshal.WriteByte(destination, i, 0xa5);
            Marshal.WriteInt32(destination, unchecked((int)Type)); Marshal.WriteInt32(destination, 4, unchecked((int)(HeaderSize ?? count)));
            if (Type == 1) { Marshal.WriteInt16(destination, KeyboardActivitySource.HeaderByteCount, (short)Scan); Marshal.WriteInt16(destination, KeyboardActivitySource.HeaderByteCount + 2, (short)Flags); }
            if (Type == 0) Marshal.WriteInt16(destination, KeyboardActivitySource.HeaderByteCount + 4, (short)MouseFlags);
            size = ReportedSize ?? size; return count;
        }
        internal (IntPtr Result, bool Handled) Emit(int message = 0x00ff, int inputCode = 1, bool handled = false)
        {
            if (hook is null) throw new InvalidOperationException("No fake callback.");
            var result = hook(WindowHandle, message, new IntPtr(inputCode), new IntPtr(100), ref handled); return (result, handled);
        }
        private sealed class FakeWindow(FakeNative owner) : IKeyboardActivityWindow
        {
            private bool disposed;
            public IntPtr Handle => owner.WindowHandle;
            public void Dispose() { if (disposed) return; disposed = true; owner.WindowDisposeCalls++; owner.OnWindowDispose?.Invoke(); }
        }
    }
}
