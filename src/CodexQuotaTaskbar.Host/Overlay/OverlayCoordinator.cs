using System.Windows.Threading;
using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Host.Platform;
using CodexQuotaTaskbar.Host.UI;
using Microsoft.Win32;

namespace CodexQuotaTaskbar.Host.Overlay;

internal sealed class OverlayCoordinator : IDisposable
{
    private const double CapsuleWidth = QuotaCapsuleWindow.WindowWidth;
    private const double CapsuleHeight = QuotaCapsuleWindow.WindowHeight;
    private const double PopoverWidth = 328;
    private const double CapsuleFarMargin = 0;
    private const double CapsuleEdgeGap = -4;
    private const double SessionStackGap = -16;
    private readonly TaskbarTopologySource topology = new();
    private readonly Dictionary<string, QuotaCapsuleWindow> windows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionStackWindow> sessionWindows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScreenRect> userPositions = new(StringComparer.Ordinal);
    private readonly DispatcherTimer timer;
    private bool showAllTaskbars;
    private bool sessionLocked;
    private QuotaSnapshot snapshot = QuotaSnapshot.Unavailable("正在连接 Codex…");
    private CodexSessionsSnapshot sessions = CodexSessionsSnapshot.Empty;
    private QuotaPopoverWindow? popover;
    private QuotaCapsuleWindow? popoverOwner;

    internal OverlayCoordinator(bool showAllTaskbars)
    {
        this.showAllTaskbars = showAllTaskbars;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(750), DispatcherPriority.Background, (_, _) => Reconcile(), Dispatcher.CurrentDispatcher);
        SystemEvents.DisplaySettingsChanged += OnEnvironmentChanged;
        SystemEvents.UserPreferenceChanged += OnEnvironmentChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    internal event EventHandler? RefreshRequested;
    internal event EventHandler? OpenCodexRequested;
    internal event EventHandler? ContextRequested;
    internal event Action<string>? OpenSessionRequested;

    internal void Start()
    {
        Reconcile();
        timer.Start();
    }

    internal void SetShowAllTaskbars(bool value)
    {
        showAllTaskbars = value;
        Reconcile();
    }

    internal void Apply(QuotaSnapshot value)
    {
        snapshot = value;
        foreach (var window in windows.Values)
        {
            window.Apply(value);
        }
        popover?.Apply(value);
    }

    internal void ApplySessions(CodexSessionsSnapshot value)
    {
        sessions = value;
        foreach (var window in windows.Values)
        {
            window.ApplySessions(value);
        }
        foreach (var (monitorId, sessionWindow) in sessionWindows)
        {
            sessionWindow.Apply(value);
            if (windows.TryGetValue(monitorId, out var owner))
            {
                UpdateSessionStack(owner, sessionWindow);
            }
        }
    }

    private void Reconcile()
    {
        var anchors = topology.Capture()
            .Where(anchor => showAllTaskbars || anchor.IsPrimary)
            .ToDictionary(anchor => anchor.MonitorId, StringComparer.Ordinal);

        foreach (var stale in windows.Keys.Except(anchors.Keys, StringComparer.Ordinal).ToArray())
        {
            windows[stale].Close();
            if (sessionWindows.Remove(stale, out var sessionWindow))
            {
                sessionWindow.Close();
            }
            windows.Remove(stale);
            userPositions.Remove(stale);
        }

        foreach (var anchor in anchors.Values)
        {
            if (!windows.TryGetValue(anchor.MonitorId, out var window))
            {
                window = CreateWindow(anchor.MonitorId);
                windows.Add(anchor.MonitorId, window);
                var sessionWindow = CreateSessionWindow(window);
                sessionWindows.Add(anchor.MonitorId, sessionWindow);
                window.Apply(snapshot);
                window.ApplySessions(sessions);
                sessionWindow.Apply(sessions);
                window.Show();
            }

            var shouldShow = anchor.IsVisible && !sessionLocked && !TaskbarTopologySource.IsMonitorFullscreen(anchor);
            if (!shouldShow)
            {
                window.Hide();
                if (popover?.Owner == window)
                {
                    popover.Close();
                }
                sessionWindows[anchor.MonitorId].Hide();
                continue;
            }

            if (!window.IsVisible)
            {
                window.Show();
            }
            if (!window.IsUserDragging)
            {
                if (sessions.Sessions.Count == 0)
                {
                    window.Place(DesiredOwnerBounds(window, anchor));
                }
            }
            UpdateSessionStack(window, sessionWindows[anchor.MonitorId], anchor);
        }
    }

    private QuotaCapsuleWindow CreateWindow(string monitorId)
    {
        var window = new QuotaCapsuleWindow(monitorId);
        window.PrimaryInvoked += (_, _) => TogglePopover(window);
        window.ContextInvoked += (_, _) => ContextRequested?.Invoke(window, EventArgs.Empty);
        window.UserMoved += (previous, current) => OnWindowMoved(window, previous, current);
        return window;
    }

    private SessionStackWindow CreateSessionWindow(QuotaCapsuleWindow owner)
    {
        var window = new SessionStackWindow(owner.MonitorId);
        window.OpenSessionRequested += threadId => OpenSessionRequested?.Invoke(threadId);
        window.ContextInvoked += (_, _) => ContextRequested?.Invoke(window, EventArgs.Empty);
        window.PresentationChanged += (_, _) => UpdateSessionStack(owner, window);
        return window;
    }

    private void OnWindowMoved(QuotaCapsuleWindow window, ScreenRect _previous, ScreenRect current)
    {
        userPositions[window.MonitorId] = current;
        if (sessionWindows.TryGetValue(window.MonitorId, out var sessionWindow))
        {
            UpdateSessionStack(window, sessionWindow);
        }
        if (popover is null || !ReferenceEquals(popoverOwner, window))
        {
            return;
        }
        RepositionPopover(window);
    }

    private void UpdateSessionStack(QuotaCapsuleWindow owner, SessionStackWindow sessionWindow, TaskbarAnchor? knownAnchor = null)
    {
        if (!owner.IsVisible || sessions.Sessions.Count == 0)
        {
            sessionWindow.Hide();
            return;
        }

        if (!sessionWindow.IsVisible)
        {
            sessionWindow.Show();
        }
        var anchor = knownAnchor ?? SelectAnchor(topology.Capture(), owner.MonitorId, owner.PhysicalBounds);
        if (anchor is null)
        {
            return;
        }
        var desiredOwner = owner.IsUserDragging ? owner.PhysicalBounds : DesiredOwnerBounds(owner, anchor);
        var reservedSessionHeight = ReservedSessionHeight(sessions.Sessions.Count);
        var placement = SessionStackPlacementCalculator.CalculateBelow(
            anchor,
            desiredOwner,
            SessionStackWindow.StackWidth,
            reservedSessionHeight,
            SessionStackGap);
        if (!owner.IsUserDragging)
        {
            owner.Place(placement.Capsule);
        }
        sessionWindow.Place(ResizeStackHeight(placement.Stack, sessionWindow.DesiredHeight, anchor.Dpi));
        if (ReferenceEquals(popoverOwner, owner))
        {
            RepositionPopover(owner);
        }
    }

    private ScreenRect DesiredOwnerBounds(QuotaCapsuleWindow owner, TaskbarAnchor anchor) =>
        userPositions.TryGetValue(owner.MonitorId, out var userPosition)
            ? userPosition
            : OverlayPlacementCalculator.Calculate(anchor, CapsuleWidth, CapsuleHeight, CapsuleFarMargin, CapsuleEdgeGap);

    private void RepositionPopover(QuotaCapsuleWindow owner)
    {
        if (popover is null)
        {
            return;
        }
        var bounds = CalculatePopoverBounds(owner, popover);
        _ = NativeMethods.SetWindowPos(new System.Windows.Interop.WindowInteropHelper(popover).Handle, NativeMethods.HwndTopmost,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    private void TogglePopover(QuotaCapsuleWindow owner)
    {
        if (popover is not null)
        {
            popover.RequestClose();
            return;
        }

        var details = new QuotaPopoverWindow { Owner = owner };
        popover = details;
        popoverOwner = owner;
        details.Apply(snapshot);
        details.RefreshRequested += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        details.OpenCodexRequested += (_, _) => OpenCodexRequested?.Invoke(this, EventArgs.Empty);
        details.Closed += (_, _) =>
        {
            popover = null;
            popoverOwner = null;
        };
        details.Show();
        var bounds = CalculatePopoverBounds(owner, details);
        _ = NativeMethods.SetWindowPos(new System.Windows.Interop.WindowInteropHelper(details).Handle, NativeMethods.HwndTopmost,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height, NativeMethods.SwpShowWindow);
        var motionAnchor = owner.PhysicalBounds;
        if (sessionWindows.TryGetValue(owner.MonitorId, out var motionSessions) && motionSessions.IsVisible)
        {
            motionAnchor = Union(motionAnchor, motionSessions.PhysicalBounds);
        }
        details.PlayEntrance(QuotaPopoverWindow.CalculateEntranceOffset(motionAnchor, bounds));
        details.Activate();
    }

    private ScreenRect CalculatePopoverBounds(QuotaCapsuleWindow owner, QuotaPopoverWindow details)
    {
        var anchor = SelectAnchor(topology.Capture(), owner.MonitorId, owner.PhysicalBounds);
        var dpi = anchor?.Dpi ?? 96;
        var scale = dpi / 96d;
        var width = checked((int)Math.Round(PopoverWidth * scale));
        var heightDip = Math.Max(240, details.ActualHeight);
        var height = checked((int)Math.Round(heightDip * scale));
        var obstruction = owner.PhysicalBounds;
        if (sessionWindows.TryGetValue(owner.MonitorId, out var sessionWindow) && sessionWindow.IsVisible)
        {
            obstruction = Union(obstruction, sessionWindow.PhysicalBounds);
        }
        return anchor is null
            ? new ScreenRect(obstruction.Right - width, obstruction.Top - height, obstruction.Right, obstruction.Top)
            : PopoverPlacementCalculator.Calculate(anchor, obstruction, PopoverWidth, heightDip, 8);
    }

    internal static ScreenRect Union(ScreenRect first, ScreenRect second) => new(
        Math.Min(first.Left, second.Left),
        Math.Min(first.Top, second.Top),
        Math.Max(first.Right, second.Right),
        Math.Max(first.Bottom, second.Bottom));

    internal static double ReservedSessionHeight(int totalCount) =>
        SessionStackWindow.CalculateHeight(totalCount, true);

    internal static ScreenRect ResizeStackHeight(ScreenRect reservedStack, double currentHeightDip, double dpi)
    {
        var height = checked((int)Math.Round(currentHeightDip * dpi / 96d));
        return reservedStack with { Bottom = reservedStack.Top + Math.Max(1, height) };
    }

    internal static TaskbarAnchor? SelectAnchor(IEnumerable<TaskbarAnchor> anchors, string ownerMonitorId, ScreenRect ownerBounds)
    {
        var values = anchors.ToArray();
        var centerX = ownerBounds.Left + ownerBounds.Width / 2;
        var centerY = ownerBounds.Top + ownerBounds.Height / 2;
        return values.FirstOrDefault(anchor =>
                   centerX >= anchor.MonitorBounds.Left && centerX < anchor.MonitorBounds.Right
                   && centerY >= anchor.MonitorBounds.Top && centerY < anchor.MonitorBounds.Bottom)
               ?? values.FirstOrDefault(anchor => string.Equals(anchor.MonitorId, ownerMonitorId, StringComparison.Ordinal));
    }

    private void OnEnvironmentChanged(object? sender, EventArgs eventArgs) => Reconcile();

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs eventArgs)
    {
        sessionLocked = eventArgs.Reason == SessionSwitchReason.SessionLock
            || (sessionLocked && eventArgs.Reason != SessionSwitchReason.SessionUnlock);
        Reconcile();
    }

    public void Dispose()
    {
        timer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnEnvironmentChanged;
        SystemEvents.UserPreferenceChanged -= OnEnvironmentChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        popover?.Close();
        foreach (var window in windows.Values)
        {
            window.Close();
        }
        foreach (var window in sessionWindows.Values)
        {
            window.Close();
        }
        windows.Clear();
        sessionWindows.Clear();
    }
}
