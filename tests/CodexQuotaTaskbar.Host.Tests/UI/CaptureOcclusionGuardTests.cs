using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Host.Bongo;
using CodexQuotaTaskbar.Host.Overlay;
using CodexQuotaTaskbar.Host.Platform;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.Tray;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

[Collection("Native UI isolation")]
public sealed class CaptureOcclusionGuardTests
{
    [Fact]
    public void Captured_owner_and_stack_cannot_request_native_position_or_create_a_handle() => Sta(() =>
    {
        var owner = new QuotaCapsuleWindow("capture") { CaptureOccluded = true };
        var stack = new SessionStackWindow("capture") { CaptureOccluded = true };
        try
        {
            var bounds = new ScreenRect(100, 100, 380, 300);
            owner.Place(bounds); stack.Place(bounds);
            WindowInteropPolicy.Position(owner, bounds); WindowInteropPolicy.Position(stack, bounds);
            Assert.Equal(default, owner.PhysicalBounds);
            Assert.Equal(default, stack.PhysicalBounds);
            Assert.Equal(nint.Zero, new WindowInteropHelper(owner).Handle);
            Assert.Equal(nint.Zero, new WindowInteropHelper(stack).Handle);
            var stackHeight = stack.Height;
            stack.Apply(new([new CodexSessionSnapshot("synthetic", "synthetic", CodexSessionState.Running, DateTimeOffset.UtcNow)], DateTimeOffset.UtcNow));
            Assert.Equal(stackHeight, stack.Height);
            stack.CaptureOccluded = false;
            Assert.Equal(stack.DesiredHeight, stack.Height);
            var before = owner.Height;
            owner.ConfigureBongoSurface(true, 192);
            owner.PresentBongoFrame(new BongoFrame(1, 1, [255, 255, 255, 255], 4));
            Assert.Equal(before, owner.Height); // Async model/layout cannot resize the HWND either.
            owner.CaptureOccluded = false;
            owner.PresentBongoFrame(new BongoFrame(1, 1, [255, 255, 255, 255], 4));
            Assert.Equal(owner.DesiredHeight, owner.Height);
        }
        finally { owner.Close(); stack.Close(); }
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Delayed_button_up_during_capture_does_not_open_details_menu_or_chat(bool right) => Sta(() =>
    {
        var owner = new QuotaCapsuleWindow("capture");
        try
        {
            var actions = 0;
            owner.PrimaryInvoked += (_, _) => actions++;
            owner.ContextInvoked += (_, _) => actions++;
            owner.CompanionInvoked += (_, _) => actions++;
            var button = right ? MouseButton.Right : MouseButton.Left;
            Assert.True(owner.BeginPointerGesture(button, IslandHitRegion.Quota, 123, 0, 0, () => true));
            owner.CaptureOccluded = true;
            Assert.True(owner.EndPointerGesture(button, IslandHitRegion.Quota, 124));
            Assert.False(owner.HasPointerGesture);
            Assert.Equal(0, actions);
            Assert.False(owner.BeginPointerGesture(button, IslandHitRegion.Quota, 125, 0, 0,
                () => throw new InvalidOperationException("Capture must not be requested")));
            Assert.Equal(nint.Zero, new WindowInteropHelper(owner).Handle);
        }
        finally { owner.Close(); }
    });

    [Fact]
    public void Direct_details_and_async_placement_requests_are_ignored_while_capture_is_known() => Sta(() =>
    {
        using var coordinator = new OverlayCoordinator(false, observePointer: false);
        var owner = new QuotaCapsuleWindow("capture") { CaptureOccluded = true };
        try
        {
            typeof(OverlayCoordinator).GetMethod("TogglePopover", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(coordinator, [owner]);
            typeof(OverlayCoordinator).GetMethod("UpdatePanelPlacement", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(coordinator, [owner, null, null]);
            typeof(OverlayCoordinator).GetMethod("RepositionPopover", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(coordinator, [owner]);
            Assert.Null(typeof(OverlayCoordinator).GetField("popover", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator));
            coordinator.SetInlineDotMode(true);
            coordinator.SetDotPresentationEnabled(true);
            var closeRequests = 0;
            coordinator.CloseContextRequested += (_, _) => closeRequests++;
            coordinator.OpenInlineDot(owner, focus: true);
            Assert.Equal(0, closeRequests);
            Assert.Null(typeof(OverlayCoordinator).GetField("inlineDotOwner", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator));
            using var tray = new TrayController(AppSettings.Default with { ShowTrayIcon = false }, restrictedDemo: true);
            tray.ShowContextMenu(owner);
            var menu = (TrayMenuWindow)typeof(TrayController).GetField("menu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray)!;
            Assert.False(menu.IsVisible);
            Assert.Equal(nint.Zero, new WindowInteropHelper(menu).Handle);
            Assert.Equal(nint.Zero, new WindowInteropHelper(owner).Handle);
        }
        finally { owner.Close(); }
    });

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            try { action(); } catch (Exception error) { failure = error; }
            finally { dispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
