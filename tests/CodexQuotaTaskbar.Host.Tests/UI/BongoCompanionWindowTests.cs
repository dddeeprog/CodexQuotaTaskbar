using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.Bongo;
using CodexQuotaTaskbar.Host.UI;
using Size = System.Windows.Size;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class BongoCompanionWindowTests
{
    [Fact]
    public void Frame_mailbox_disposes_dropped_and_presented_buffers_and_keeps_only_the_latest() => Sta(() =>
    {
        using var window = new BongoCompanionWindow();
        var pool = new ObservedPool();
        var changed = 0;
        window.PresentationChanged += (_, _) => changed++;
        for (byte value = 1; value <= 100; value++) window.QueueFrame(Frame(value, pool));
        Assert.Equal(99, pool.Returns);
        Assert.False(window.HasFrame);
        Flush();
        Assert.Equal(100, pool.Returns);
        Assert.True(window.HasFrame);
        Assert.Equal(1, changed);
        var pixels = new byte[16];
        window.CurrentBitmap!.CopyPixels(pixels, 8, 0);
        Assert.Equal((byte)100, pixels[0]);
        Assert.False(window.IsLoaded);
        Assert.False(window.IsVisible);
        Assert.Equal(nint.Zero, new WindowInteropHelper(window).Handle);
    });

    [Fact]
    public void Same_sized_frames_reuse_the_bitmap_and_clear_restores_fallback_state() => Sta(() =>
    {
        using var window = new BongoCompanionWindow();
        window.QueueFrame(Frame(30)); Flush();
        var bitmap = window.CurrentBitmap;
        window.QueueFrame(Frame(60)); Flush();
        Assert.Same(bitmap, window.CurrentBitmap);
        window.ClearFrame();
        Assert.False(window.HasFrame);
        Assert.Null(window.CurrentBitmap);
        Flush();
        Assert.False(window.HasFrame);
        window.QueueFrame(Frame(90)); Flush();
        Assert.True(window.HasFrame);
        Assert.NotSame(bitmap, window.CurrentBitmap);
    });

    [Fact]
    public void Coordinator_direct_present_supersedes_a_queued_frame_without_a_second_dispatch() => Sta(() =>
    {
        using var window = new BongoCompanionWindow();
        var pool = new ObservedPool();
        window.QueueFrame(Frame(15, pool));
        window.PresentFrame(Frame(45, pool));
        Assert.Equal(2, pool.Returns);
        Assert.True(window.HasFrame);
        Flush();
        Assert.True(window.HasFrame);
        var pixels = new byte[16]; window.CurrentBitmap!.CopyPixels(pixels, 8, 0);
        Assert.Equal((byte)45, pixels[0]);
    });

    [Fact]
    public void Show_without_a_valid_frame_and_show_after_disposal_never_create_a_window() => Sta(() =>
    {
        var window = new BongoCompanionWindow();
        window.ShowAt(new(500, 500, 692, 612));
        Assert.False(window.IsVisible);
        Assert.Equal(nint.Zero, new WindowInteropHelper(window).Handle);
        window.Dispose();
        window.ShowAt(new(500, 500, 692, 612));
        Assert.Equal(nint.Zero, new WindowInteropHelper(window).Handle);
    });

    [Fact]
    public void Clear_disposes_pending_and_does_not_replay_the_old_frame_on_reopen() => Sta(() =>
    {
        using var window = new BongoCompanionWindow();
        var pool = new ObservedPool();
        window.QueueFrame(Frame(25, pool));
        window.ClearFrame(); Flush();
        Assert.Equal(1, pool.Returns);
        Assert.False(window.HasFrame);
        window.QueueFrame(Frame(50, pool));
        window.ClearFrame();
        window.QueueFrame(Frame(75, pool)); Flush();
        Assert.Equal(3, pool.Returns);
        Assert.True(window.HasFrame);
        var pixels = new byte[16]; window.CurrentBitmap!.CopyPixels(pixels, 8, 0);
        Assert.Equal((byte)75, pixels[0]);
    });

    [Fact]
    public void Worker_thread_handoffs_are_bounded_and_disposal_returns_all_future_frames() => Sta(() =>
    {
        var window = new BongoCompanionWindow();
        var pool = new ObservedPool();
        var worker = new Thread(() => { for (var index = 0; index < 500; index++) window.QueueFrame(Frame(30, pool)); });
        worker.Start(); Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(499, pool.Returns);
        window.Dispose();
        Assert.Equal(500, pool.Returns);
        window.QueueFrame(Frame(60, pool));
        Assert.Equal(501, pool.Returns);
        Flush();
        Assert.False(window.HasFrame);
        Assert.Null(window.CurrentBitmap);
        window.Dispose();
    });

    [Theory]
    [InlineData(160, 96)] [InlineData(192, 144)] [InlineData(240, 192)]
    public void Offscreen_presentation_preserves_transparency_aspect_and_nonactivating_window_policy(double width, int dpi) => Sta(() =>
    {
        using var window = new BongoCompanionWindow();
        window.SetSize(width);
        var data = new byte[] { 0, 0, 0, 0, 50, 70, 100, 128, 30, 30, 30, 255, 0, 0, 0, 0 };
        window.QueueFrame(new BongoFrame(2, 2, data, data.Length)); Flush();
        var exact = new byte[16]; window.CurrentBitmap!.CopyPixels(exact, 8, 0);
        Assert.Equal(data, exact);
        Assert.False(window.ShowActivated);
        Assert.False(window.ShowInTaskbar);
        Assert.False(window.Focusable);
        Assert.Equal(ResizeMode.NoResize, window.ResizeMode);
        Assert.Equal(width / BongoCompanionPlacement.ModelAspect, window.Height, 5);
        var view = (FrameworkElement)window.Content;
        view.Measure(new Size(window.Width, window.Height));
        view.Arrange(new Rect(0, 0, window.Width, window.Height));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.Width * dpi / 96),
            (int)Math.Ceiling(window.Height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(view);
        Assert.False(window.IsLoaded);
        Assert.Equal(nint.Zero, new WindowInteropHelper(window).Handle);
        var visible = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(visible, bitmap.PixelWidth * 4, 0);
        Assert.Contains(visible, value => value > 0);
        Assert.Equal((byte)0, visible[3]);
    });

    private static BongoFrame Frame(byte blue, ObservedPool? pool = null)
    {
        var pixels = new byte[16];
        for (var index = 0; index < pixels.Length; index += 4) { pixels[index] = blue; pixels[index + 3] = 255; }
        return new(2, 2, pixels, pixels.Length, pool);
    }

    private static void Flush() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class ObservedPool : ArrayPool<byte>
    {
        internal int Returns;
        public override byte[] Rent(int minimumLength) => new byte[minimumLength];
        public override void Return(byte[] array, bool clearArray = false) => Interlocked.Increment(ref Returns);
    }
}
