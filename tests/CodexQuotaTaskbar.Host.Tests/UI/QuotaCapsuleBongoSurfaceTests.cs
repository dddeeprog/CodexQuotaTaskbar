using System.Buffers;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Host.Bongo;
using CodexQuotaTaskbar.Host.Resets;
using CodexQuotaTaskbar.Host.UI;
using Size = System.Windows.Size;

namespace CodexQuotaTaskbar.Host.Tests.UI;

[Collection("Native UI isolation")]
public sealed class QuotaCapsuleBongoSurfaceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T08:00:00Z");

    [Theory]
    [InlineData(true, 160)] [InlineData(true, 192)] [InlineData(true, 240)]
    [InlineData(false, 160)] [InlineData(false, 192)] [InlineData(false, 240)]
    public void Production_window_owns_model_and_original_controls_under_one_native_owner(bool unified, int size) => Sta(() =>
    {
        var window = new QuotaCapsuleWindow("fused-owner", unified);
        try
        {
            var chrome = Named<Border>(window, "GlassBorder");
            var originalBody = (FrameworkElement)chrome.Child;
            var header = window.FindName("QuotaHeader");
            var baseHeight = window.DesiredHeight;
            window.ConfigureBongoSurface(true, size);
            Assert.Equal(baseHeight, window.DesiredHeight);
            Assert.False(window.BongoHasFrame);
            window.PresentBongoFrame(Frame());
            var surface = Assert.IsType<BongoIslandSurface>(chrome.Child);
            Assert.True(surface.OwnsQuotaBody);
            Assert.Same(surface, VisualTreeHelper.GetParent(originalBody));
            Assert.Same(window, Window.GetWindow(surface));
            Assert.Same(header, window.FindName("QuotaHeader"));
            Assert.Equal(baseHeight + surface.StageHeightDip, window.DesiredHeight, 6);
            Assert.Equal(window.DesiredHeight, window.Height, 6);
            Assert.Equal(size, window.BongoModelSizeDip.Width);
            Assert.True(window.BongoHasFrame);
            Assert.False(window.BongoVisible); // Offscreen test, not a claim of desktop visibility.
            Assert.Equal(nint.Zero, new WindowInteropHelper(window).Handle);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void First_frame_notifies_once_and_later_frames_reuse_the_bitmap_without_layout_churn() => Sta(() =>
    {
        var window = new QuotaCapsuleWindow("fused-frames");
        try
        {
            window.ConfigureBongoSurface(true, 192);
            var changes = 0; window.PresentationChanged += (_, _) => changes++;
            window.PresentBongoFrame(Frame());
            var chrome = Named<Border>(window, "GlassBorder");
            var surface = chrome.Child;
            var bitmap = Field<WriteableBitmap>(window, "bongoBitmap");
            for (var index = 0; index < 80; index++) window.PresentBongoFrame(Frame());
            Assert.Equal(1, changes);
            Assert.Same(surface, chrome.Child);
            Assert.Same(bitmap, Field<WriteableBitmap>(window, "bongoBitmap"));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Clear_and_white_ball_restore_the_exact_quota_visual_tree_and_height(bool unified) => Sta(() =>
    {
        var window = new QuotaCapsuleWindow("fused-restore", unified);
        try
        {
            var chrome = Named<Border>(window, "GlassBorder");
            var body = chrome.Child; var baseHeight = window.DesiredHeight;
            window.ConfigureBongoSurface(true, 192); window.PresentBongoFrame(Frame());
            window.ClearBongoFrame();
            Assert.False(window.BongoHasFrame);
            Assert.Same(body, chrome.Child);
            Assert.Equal(baseHeight, window.DesiredHeight);
            Assert.Null(Field<WriteableBitmap?>(window, "bongoBitmap"));
            window.PresentBongoFrame(Frame());
            Assert.True(window.BongoHasFrame);
            window.ConfigureBongoSurface(false, 192);
            Assert.False(window.BongoHasFrame);
            Assert.Same(body, chrome.Child);
            window.PresentBongoFrame(Frame());
            Assert.False(window.BongoHasFrame);
            Assert.Same(body, chrome.Child);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Every_incoming_frame_returns_its_buffer_even_if_the_surface_is_disabled() => Sta(() =>
    {
        var pool = new CountingPool();
        var window = new QuotaCapsuleWindow("fused-pool");
        try
        {
            var disabled = new BongoFrame(1, 1, pool.Rent(4), 4, pool);
            window.PresentBongoFrame(disabled);
            Assert.Equal(1, pool.Returns);
            Assert.Throws<ObjectDisposedException>(() => disabled.Pixels);
            window.ConfigureBongoSurface(true, 192);
            var enabled = new BongoFrame(1, 1, pool.Rent(4), 4, pool);
            window.PresentBongoFrame(enabled);
            Assert.Equal(2, pool.Returns);
            Assert.True(window.BongoHasFrame);
            enabled.Dispose();
            Assert.Equal(2, pool.Returns);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Task_expansion_and_schedule_footer_resize_the_shared_card_without_rebuilding_controls() => Sta(() =>
    {
        var window = new QuotaCapsuleWindow("fused-dynamic");
        try
        {
            window.ConfigureBongoSurface(true, 192); window.PresentBongoFrame(Frame());
            var surface = Assert.IsType<BongoIslandSurface>(Named<Border>(window, "GlassBorder").Child);
            var list = Named<StackPanel>(window, "PanelSessionsList");
            window.ApplySessions(new(Enumerable.Range(0, 3).Select(i =>
                new CodexSessionSnapshot("synthetic-" + i, "Synthetic task", CodexSessionState.Running, Now)).ToArray(), Now));
            var expanded = window.DesiredHeight; var reserved = window.ReservedHeight;
            var rows = list.Children.Cast<UIElement>().ToArray();
            window.TogglePanelSessions();
            Assert.Equal(expanded - 68, window.DesiredHeight, 6);
            Assert.Equal(reserved, window.ReservedHeight, 6);
            Assert.Equal(window.DesiredHeight - 32, surface.SurfaceHeightDip, 6);
            Assert.Equal(rows, list.Children.Cast<UIElement>());
            var beforeFooter = window.DesiredHeight;
            window.ApplyResetSchedule(new(new("fused-fixture", ResetKind.Regular, Now.AddMinutes(-1), Now.AddHours(1),
                new Uri("https://example.test/announcement")), Now, false, null), Now, TimeZoneInfo.Utc);
            Assert.Equal(beforeFooter + QuotaCapsuleWindow.ResetScheduleRowHeight, window.DesiredHeight, 6);
            Assert.Equal(window.DesiredHeight - 32, surface.SurfaceHeightDip, 6);
            window.ApplyResetSchedule(new(null, Now, false, null), Now);
            Assert.Equal(beforeFooter, window.DesiredHeight, 6);
            Assert.Same(surface, Named<Border>(window, "GlassBorder").Child);
            Assert.Equal(rows, list.Children.Cast<UIElement>());
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(96)] [InlineData(144)] [InlineData(192)]
    public void Model_geometry_and_native_snapshot_use_the_whole_owner_at_each_dpi(int dpi) => Sta(() =>
    {
        var window = new QuotaCapsuleWindow("fused-dpi");
        try
        {
            window.ConfigureBongoSurface(true, 240); window.PresentBongoFrame(Frame());
            var scale = dpi / 96d;
            var owner = new ScreenRect(-500, 100, -500 + (int)(window.LayoutWidth * scale), 100 + (int)Math.Ceiling(window.DesiredHeight * scale));
            typeof(QuotaCapsuleWindow).GetField("physicalBounds", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, owner);
            var model = window.BongoModelBounds;
            Assert.Equal((int)(240 * scale), model.Width);
            Assert.InRange(model.Left, owner.Left, owner.Right);
            Assert.InRange(model.Right, owner.Left, owner.Right);
            Assert.InRange(model.Top, owner.Top, owner.Bottom);
            Assert.InRange(model.Bottom, owner.Top, owner.Bottom);
            var element = (FrameworkElement)window.Content;
            element.Measure(new Size(window.LayoutWidth, window.DesiredHeight));
            element.Arrange(new Rect(0, 0, window.LayoutWidth, window.DesiredHeight));
            element.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.LayoutWidth * scale),
                (int)Math.Ceiling(window.DesiredHeight * scale), dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(element);
            Assert.Equal((int)Math.Ceiling(window.LayoutWidth * scale), bitmap.PixelWidth);
            Assert.Equal((int)Math.Ceiling(window.DesiredHeight * scale), bitmap.PixelHeight);
            Assert.Equal(nint.Zero, new WindowInteropHelper(window).Handle);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Size_change_reuses_owner_and_status_display_disable_does_not_release_the_cat_stage() => Sta(() =>
    {
        var window = new QuotaCapsuleWindow("fused-size");
        try
        {
            window.ConfigureBongoSurface(true, 160); window.PresentBongoFrame(Frame());
            var surface = Assert.IsType<BongoIslandSurface>(Named<Border>(window, "GlassBorder").Child);
            var shortHeight = window.DesiredHeight;
            window.ConfigureBongoSurface(true, 240);
            Assert.True(window.DesiredHeight > shortHeight);
            Assert.Same(surface, Named<Border>(window, "GlassBorder").Child);
            Assert.Equal(240, surface.ModelWidthDip);
            window.SetCompanion(false, false);
            Assert.True(window.BongoHasFrame);
            Assert.Same(surface, Named<Border>(window, "GlassBorder").Child);
            window.ConfigureBongoSurface(false, 240);
            Assert.False(window.BongoHasFrame);
            Assert.IsType<Grid>(Named<Border>(window, "GlassBorder").Child);
            Assert.Equal(80, window.DesiredHeight);
        }
        finally { window.Close(); }
    });

    private static BongoFrame Frame() => new(1, 1, [255, 255, 255, 255], 4);
    private static T Named<T>(Window window, string name) => Assert.IsType<T>(window.FindName(name));
    private static T Field<T>(object value, string name) => (T)typeof(QuotaCapsuleWindow)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Shared production surface test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private sealed class CountingPool : ArrayPool<byte>
    {
        internal int Returns { get; private set; }
        public override byte[] Rent(int minimumLength) => new byte[minimumLength];
        public override void Return(byte[] array, bool clearArray = false) => Returns++;
    }
}
