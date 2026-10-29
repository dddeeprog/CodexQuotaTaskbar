using System.Windows;
using System.Windows.Input;
using CodexQuotaTaskbar.Host.Platform;
using CodexQuotaTaskbar.Host.Resets;
using Point = System.Windows.Point;

namespace CodexQuotaTaskbar.Host.UI;

internal enum IslandHitRegion { None, Quota, Ball, Bongo, ResetNotice, DotMarker }
internal readonly record struct IslandInputDiagnostic(string Action, int Timestamp, int DeliveryDelayMs);

public partial class QuotaCapsuleWindow
{
    private MouseButton gestureButton;
    private IslandHitRegion gestureRegion;
    private (MouseButton Button, int Timestamp)? lastPointerDown;
    private readonly Queue<IslandInputDiagnostic> inputDiagnostics = new();
    internal IReadOnlyList<IslandInputDiagnostic> InputDiagnostics => inputDiagnostics.ToArray();
    internal bool HasPointerGesture => pointerDown;

    // Bounded, in-memory only. No coordinates, text, account IDs or per-frame logging.
    private void TracePointer(string action, int timestamp = 0)
    {
        var delay = timestamp == 0 ? 0 : unchecked(Environment.TickCount - timestamp);
        inputDiagnostics.Enqueue(new(action, timestamp, delay is >= 0 and < 60000 ? delay : 0));
        while (inputDiagnostics.Count > 64) inputDiagnostics.Dequeue();
    }

    internal static IslandHitRegion ResolveHitRegion(bool dotContent, bool ball, bool cat, bool reset,
        bool quota, bool interactiveChild)
    {
        if (dotContent) return IslandHitRegion.None;
        if (ball) return IslandHitRegion.Ball;
        if (cat) return IslandHitRegion.Bongo;
        if (reset) return IslandHitRegion.ResetNotice;
        return quota && !interactiveChild ? IslandHitRegion.Quota : IslandHitRegion.None;
    }

    internal static IslandHitRegion RegionAtPoint(Point point, Rect dot, Rect ball, Rect cat,
        Rect reset, Rect quota, Rect toggle, Rect source) => ResolveHitRegion(
            dot.Contains(point), ball.Contains(point), cat.Contains(point), reset.Contains(point),
            quota.Contains(point), toggle.Contains(point) || source.Contains(point));

    private Rect InputBounds(FrameworkElement? element)
    {
        if (element is null || element.Visibility != Visibility.Visible || !element.IsHitTestVisible
            || !Shell.IsAncestorOf(element)) return Rect.Empty;
        return element.TransformToAncestor(Shell).TransformBounds(new Rect(element.RenderSize));
    }

    internal IslandHitRegion PointerRegion(Point point, MouseButton button = MouseButton.Left)
    {
        // Left belongs to Button; right retains the island's normal context menu.
        // A disabled marker must not fall through to the cat's primary action.
        if (InputBounds(dotMarker).Contains(point))
            return button == MouseButton.Right ? IslandHitRegion.DotMarker : IslandHitRegion.None;
        // Element capture intentionally redirects IsMouseOver to this Window.
        // Coordinates and visual transforms remain valid; never classify Up from hover flags.
        var cat = BongoHasFrame && bongoSurface is { Visibility: Visibility.Visible }
            ? bongoSurface.TransformToAncestor(Shell).TransformBounds(
                new Rect(0, 0, bongoSurface.ActualWidth, bongoSurface.StageHeightDip)) : Rect.Empty;
        return RegionAtPoint(point,
            DotTopExtensionVisible ? InputBounds(dotTopContent) : Rect.Empty,
            CompanionReady ? InputBounds(companion) : Rect.Empty, cat,
            InputBounds(CompactResetNotice.Visibility == Visibility.Visible ? CompactResetNotice : ResetScheduleRow),
            InputBounds(QuotaHeader), InputBounds(SessionToggle), InputBounds(classicResetSource));
    }

    internal IslandHitRegion PreparePointerDown(Point point, MouseButton button)
    {
        var region = PointerRegion(point, button);
        // A child Down can follow a lost island Up with no intervening MouseMove.
        // Reset only our gesture/capture before allowing the child's normal route.
        if (region == IslandHitRegion.None) CancelPointerGesture("child-down");
        return region;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return;
        var region = PreparePointerDown(args.GetPosition(Shell), args.ChangedButton);
        if (region == IslandHitRegion.None) return;
        if (!WindowInteropPolicy.TryGetCursorPosition(out var x, out var y)) { CancelPointerGesture("cursor-unavailable"); return; }
        if (BeginPointerGesture(args.ChangedButton, region, args.Timestamp, x, y, () => Mouse.Capture(this)))
            args.Handled = true;
    }

    // The routed handlers use these exact transitions; tests supply only capture/coordinates, never a real mouse.
    internal bool BeginPointerGesture(MouseButton button, IslandHitRegion region, int timestamp,
        int x, int y, Func<bool> capture)
    {
        if (CaptureOccluded) return false;
        if (button is not (MouseButton.Left or MouseButton.Right) || region == IslandHitRegion.None) return false;
        if (lastPointerDown == (button, timestamp)) { TracePointer("duplicate-down", timestamp); return true; }
        CancelPointerGesture("new-gesture");
        lastPointerDown = (button, timestamp);
        if (!capture()) { TracePointer("capture-failed", timestamp); return false; }
        gestureButton = button; gestureRegion = region;
        pointerDown = true; userDragging = false;
        RefreshResetFooter();
        FinishExtensionTransitions();
        dragOriginBounds = physicalBounds;
        dragOriginX = x; dragOriginY = y;
        TracePointer("down-" + region, timestamp);
        OverlayLifecycleDiagnostics.Local.Window(this, button == MouseButton.Right
            ? OverlayLifecycleEvent.RightDown : OverlayLifecycleEvent.LeftDown, inputTimestamp: timestamp);
        return true;
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs args)
    {
        UpdateResetStatusPointer(args.GetPosition(Shell));
        if (companion is not null) companion.Gaze(args.GetPosition(companion));
        if (!pointerDown) return;
        var pressed = gestureButton == MouseButton.Left ? args.LeftButton : args.RightButton;
        if (pressed != MouseButtonState.Pressed) { CancelPointerGesture("missing-up"); return; }
        if (!IsMouseCaptured) { CancelPointerGesture("capture-missing"); return; }
        if (!WindowInteropPolicy.TryGetCursorPosition(out var x, out var y)) { CancelPointerGesture("cursor-unavailable"); return; }
        if (MovePointerGesture(true, x, y)) args.Handled = true;
    }

    internal bool MovePointerGesture(bool pressed, int x, int y)
    {
        if (!pointerDown) return false;
        if (!pressed) { CancelPointerGesture("missing-up"); return false; }
        if (gestureButton != MouseButton.Left) return false;
        var dx = x - dragOriginX; var dy = y - dragOriginY;
        if (!userDragging && !ExceedsDragThreshold(dx, dy)) return false;
        if (!userDragging) TracePointer("drag");
        userDragging = true;
        Cursor = System.Windows.Input.Cursors.SizeAll;
        try { MoveByUser(TranslateBounds(dragOriginBounds, dx, dy)); }
        catch { CancelPointerGesture("move-failed"); throw; }
        return true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs args)
    {
        if (EndPointerGesture(args.ChangedButton, PointerRegion(args.GetPosition(Shell), args.ChangedButton), args.Timestamp)) args.Handled = true;
    }

    internal bool EndPointerGesture(MouseButton button, IslandHitRegion releaseRegion, int timestamp)
    {
        if (!pointerDown || button != gestureButton) return false;
        if (CaptureOccluded) { CancelPointerGesture("capture-overlay"); return true; }
        var region = gestureRegion;
        var invoke = !userDragging && region == releaseRegion;
        // Reset before releasing capture or raising callbacks: LostCapture/reentrant input cannot dispatch twice.
        CancelPointerGesture("up");
        if (!invoke) return true;
        TracePointer(button == MouseButton.Right ? "context" : "invoke-" + region, timestamp);
        OverlayLifecycleDiagnostics.Local.Window(this, button == MouseButton.Right
            ? OverlayLifecycleEvent.ContextInvoke : OverlayLifecycleEvent.PrimaryInvoke, inputTimestamp: timestamp);
        if (button == MouseButton.Right) ContextInvoked?.Invoke(this, EventArgs.Empty);
        else if (region == IslandHitRegion.Ball) { companion?.Tap(); CompanionInvoked?.Invoke(this, EventArgs.Empty); }
        else if (region == IslandHitRegion.ResetNotice) InvokeResetFooter();
        else PrimaryInvoked?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal void CancelPointerGesture(string reason)
    {
        if (pointerDown) TracePointer(reason);
        ResetDragState();
        if (IsMouseCaptured) ReleaseMouseCapture(); // Never release another control's capture.
        ReconcileResetStatusPointer();
    }

    private void ResetDragState()
    {
        pointerDown = userDragging = false;
        gestureRegion = IslandHitRegion.None;
        Cursor = System.Windows.Input.Cursors.Arrow;
    }
}
