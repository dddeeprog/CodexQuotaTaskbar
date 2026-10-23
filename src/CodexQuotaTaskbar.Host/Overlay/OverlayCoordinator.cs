using System.Windows.Threading;
using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Host.Platform;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.Resets;
using CodexQuotaTaskbar.Host.UI;
using Microsoft.Win32;

namespace CodexQuotaTaskbar.Host.Overlay;

internal sealed class OverlayCoordinator : IDisposable
{
    private const double CapsuleWidth = QuotaCapsuleWindow.WindowWidth;
    private const double CapsuleHeight = QuotaCapsuleWindow.WindowHeight;
    private const double PopoverWidth = 360;
    private const double CapsuleFarMargin = 0;
    private const double CapsuleEdgeGap = -4;
    private readonly TaskbarTopologySource topology = new();
    private readonly Dictionary<string, QuotaCapsuleWindow> windows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionStackWindow> sessionWindows = new(StringComparer.Ordinal);
    private bool unifiedPanel;
    private bool companionEnabled;
    private bool companionReduced;
    private readonly Dictionary<string, ScreenRect> userPositions = new(StringComparer.Ordinal);
    private readonly DispatcherTimer timer;
    private bool showAllTaskbars;
    private int materialTransparency;
    private bool sessionLocked;
    private QuotaSnapshot snapshot = QuotaSnapshot.Unavailable("正在连接 Codex…");
    private CodexSessionsSnapshot sessions = CodexSessionsSnapshot.Empty;
    private ResetFeedSnapshot resetFeed = ResetFeedSnapshot.Loading;
    private QuotaPopoverWindow? popover;
    private QuotaCapsuleWindow? popoverOwner;
    private string? dotOwnerId;
    internal event Action<DotAnchor>? DotAnchorChanged;

    internal OverlayCoordinator(bool showAllTaskbars, int materialTransparency = 0, bool unifiedPanel = false)
    {
        this.showAllTaskbars = showAllTaskbars;
        this.unifiedPanel = unifiedPanel;
        this.materialTransparency = Math.Clamp(materialTransparency, 0, 100);
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(750), DispatcherPriority.Background, (_, _) => Reconcile(), Dispatcher.CurrentDispatcher);
        SystemEvents.DisplaySettingsChanged += OnEnvironmentChanged;
        SystemEvents.UserPreferenceChanged += OnEnvironmentChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    internal event EventHandler? RefreshRequested;
    internal event EventHandler? OpenCodexRequested;
    internal event EventHandler? OpenDotChatRequested;
    internal event EventHandler? OpenSubscriptionLoginRequested;
    internal event EventHandler? ContextRequested;
    internal event Action<string>? OpenSessionRequested;
    internal event Action<Uri>? OpenResetSourceRequested;

    internal void Start()
    {
        Reconcile();
        timer.Start();
    }

    internal void SetShowAllTaskbars(bool value)
    {
        if (showAllTaskbars == value) return;
        showAllTaskbars = value;
        Reconcile();
    }

    internal void SetCompanion(bool enabled, bool reduced)
    {
        companionEnabled = enabled;
        companionReduced = reduced;
        foreach (var window in windows.Values) window.SetCompanion(enabled, reduced);
    }

    internal void ToggleCompanionTour()
    {
        foreach (var window in windows.Values) window.ToggleCompanionTour();
    }

    internal void SetUnifiedPanel(bool value)
    {
        if (unifiedPanel == value) return;
        popover?.Close();
        foreach (var stack in sessionWindows.Values) stack.Close();
        sessionWindows.Clear();
        foreach (var window in windows.Values)
            if (window.PhysicalBounds.Width > 0) userPositions[window.MonitorId] = window.PhysicalBounds;
        foreach (var window in windows.Values) window.Close();
        windows.Clear();
        unifiedPanel = value;
        Reconcile();
    }

    internal void SetMaterialTransparency(int value)
    {
        materialTransparency = Math.Clamp(value, 0, 100);
        foreach (var window in windows.Values) window.SetMaterialTransparency(materialTransparency);
        foreach (var stack in sessionWindows.Values) stack.SetMaterialTransparency(materialTransparency);
        popover?.SetMaterialTransparency(materialTransparency);
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
            if (sessionWindows.TryGetValue(window.MonitorId, out var stack)) stack.Apply(value);
        }
        Reconcile();
    }

    internal void ApplyResets(ResetFeedSnapshot value)
    {
        resetFeed = value;
        foreach (var window in windows.Values) window.ApplyResets(value);
        popover?.ApplyResets(value);
        if (popoverOwner is { } owner)
        {
            popover?.UpdateLayout();
            RepositionPopover(owner);
        }
    }

    private void Reconcile()
    {
        var availableAnchors = topology.Capture();
        var anchors = availableAnchors
            .Where(anchor => showAllTaskbars || anchor.IsPrimary)
            .ToDictionary(anchor => anchor.MonitorId, StringComparer.Ordinal);

        foreach (var stale in windows.Keys.Except(anchors.Keys, StringComparer.Ordinal).ToArray())
        {
            if (sessionWindows.Remove(stale, out var staleStack)) staleStack.Close();
            windows[stale].Close();
            windows.Remove(stale);
            userPositions.Remove(stale);
        }

        foreach (var anchor in anchors.Values)
        {
            if (!windows.TryGetValue(anchor.MonitorId, out var window))
            {
                window = CreateWindow(anchor.MonitorId);
                windows.Add(anchor.MonitorId, window);
                if (anchor.IsPrimary || dotOwnerId is null) dotOwnerId = anchor.MonitorId;
                window.Apply(snapshot);
                window.ApplySessions(sessions);
                window.Show();
                if (!unifiedPanel)
                {
                    var stack = new SessionStackWindow(anchor.MonitorId);
                    stack.SetMaterialTransparency(materialTransparency);
                    stack.OpenSessionRequested += id => OpenSessionRequested?.Invoke(id);
                    stack.ContextInvoked += (_, _) => ContextRequested?.Invoke(window, EventArgs.Empty);
                    stack.PresentationChanged += (_, _) => UpdatePanelPlacement(window);
                    sessionWindows.Add(anchor.MonitorId, stack);
                    stack.Apply(sessions);
                }
            }

            window.ApplyResets(resetFeed);

            var shouldShow = anchor.IsVisible && !sessionLocked && !TaskbarTopologySource.IsMonitorFullscreen(anchor);
            if (!shouldShow)
            {
                window.Hide();
                if (sessionWindows.TryGetValue(anchor.MonitorId, out var hiddenStack)) hiddenStack.Hide();
                if (popover?.Owner == window)
                {
                    popover.Close();
                }
                continue;
            }

            if (!window.IsVisible)
            {
                window.Show();
            }
            var placementAnchor = userPositions.TryGetValue(window.MonitorId, out var movedBounds)
                ? SelectAnchor(availableAnchors, window.MonitorId, movedBounds) ?? anchor
                : anchor;
            if (sessionWindows.TryGetValue(anchor.MonitorId, out var visibleStack))
            {
                if (sessions.Sessions.Count > 0) visibleStack.Show();
                else visibleStack.Hide();
            }
            UpdatePanelPlacement(window, placementAnchor);
        }
        PublishDotAnchor();
    }

    private void PublishDotAnchor()
    {
        var owner = windows.GetValueOrDefault(dotOwnerId ?? "") ?? windows.Values.FirstOrDefault();
        if (owner is null) return;
        var point = owner.CompanionAnchor;
        var work = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)point.X, (int)point.Y)).WorkingArea;
        var dpi = NativeMethods.GetDpiForWindow(new System.Windows.Interop.WindowInteropHelper(owner).Handle);
        DotAnchorChanged?.Invoke(new((int)point.X, (int)point.Y, GroupBounds(owner).Bottom, work.Left, work.Top,
            work.Right, work.Bottom, dpi, owner.IsVisible && !sessionLocked && popover is null, materialTransparency));
    }

    private QuotaCapsuleWindow CreateWindow(string monitorId)
    {
        var window = new QuotaCapsuleWindow(monitorId, unifiedPanel);
        window.SetMaterialTransparency(materialTransparency);
        window.SetCompanion(companionEnabled, companionReduced);
        window.PrimaryInvoked += (_, _) => TogglePopover(window);
        window.CompanionInvoked += (_, _) =>
        {
            dotOwnerId = window.MonitorId; popover?.RequestClose(); PublishDotAnchor();
            OpenDotChatRequested?.Invoke(window, EventArgs.Empty);
        };
        window.ContextInvoked += (_, _) => ContextRequested?.Invoke(window, EventArgs.Empty);
        window.UserMoveRequested += requested => OnWindowMoveRequested(window, requested);
        window.OpenSessionRequested += threadId => OpenSessionRequested?.Invoke(threadId);
        window.OpenResetSourceRequested += uri => OpenResetSourceRequested?.Invoke(uri);
        window.PresentationChanged += (_, _) => UpdatePanelPlacement(window);
        return window;
    }

    private void OnWindowMoveRequested(QuotaCapsuleWindow window, ScreenRect requested)
    {
        dotOwnerId = window.MonitorId;
        UpdatePanelPlacement(window, requestedOwner: requested);
        // Persist the position that was actually applied, not the off-screen pointer request.
        userPositions[window.MonitorId] = window.PhysicalBounds;
        PublishDotAnchor();
        if (popover is null || !ReferenceEquals(popoverOwner, window))
        {
            return;
        }
        RepositionPopover(window);
    }

    private void UpdatePanelPlacement(QuotaCapsuleWindow owner, TaskbarAnchor? knownAnchor = null, ScreenRect? requestedOwner = null)
    {
        if (!owner.IsVisible) return;
        var anchor = knownAnchor ?? SelectAnchor(topology.Capture(), owner.MonitorId, requestedOwner ?? owner.PhysicalBounds);
        if (anchor is null)
        {
            return;
        }
        var desiredOwner = requestedOwner ?? (owner.IsUserDragging ? owner.PhysicalBounds : DesiredOwnerBounds(owner, anchor));
        if (owner.IsUnifiedPanel)
            owner.Place(CalculatePanelBounds(anchor, desiredOwner, owner.DesiredHeight, owner.ReservedHeight));
        else
        {
            var candidate = ResizeOwner(anchor, desiredOwner, owner.LayoutWidth, owner.LayoutBaseHeight);
            if (sessions.Sessions.Count > 0 && sessionWindows.TryGetValue(owner.MonitorId, out var stack))
            {
                var placement = SessionStackPlacementCalculator.CalculateBelow(anchor, candidate,
                    SessionStackWindow.StackWidth, ReservedSessionHeight(sessions.Sessions.Count), -16);
                owner.Place(placement.Capsule);
                if (stack.IsVisible) stack.Place(ResizeStackHeight(placement.Stack, stack.DesiredHeight, anchor.Dpi));
            }
            else owner.Place(SessionStackPlacementCalculator.CalculateBelow(anchor, candidate, owner.LayoutWidth, 0, 0).Capsule);
        }
        if (ReferenceEquals(popoverOwner, owner))
        {
            RepositionPopover(owner);
        }
    }

    internal static ScreenRect CalculatePanelBounds(TaskbarAnchor anchor, ScreenRect requested, double heightDip, double reservedHeightDip)
    {
        var scale = anchor.Dpi / 96d;
        var width = checked((int)Math.Round(CapsuleWidth * scale));
        var height = checked((int)Math.Round(heightDip * scale));
        var candidate = new ScreenRect(requested.Left, requested.Top, requested.Left + width, requested.Top + height);
        // Reserve expansion space without giving the session list a second native window.
        var reservedPixels = checked((int)Math.Round(reservedHeightDip * scale));
        return SessionStackPlacementCalculator.CalculateBelow(anchor, candidate, CapsuleWidth,
            Math.Max(0, reservedPixels - height) / scale, 0).Capsule;
    }

    internal static ScreenRect ResizeOwner(TaskbarAnchor anchor, ScreenRect requested, double widthDip, double heightDip)
    {
        var scale = anchor.Dpi / 96d;
        return new(requested.Left, requested.Top, requested.Left + (int)Math.Round(widthDip * scale),
            requested.Top + (int)Math.Round(heightDip * scale));
    }

    private ScreenRect GroupBounds(QuotaCapsuleWindow owner) =>
        sessionWindows.TryGetValue(owner.MonitorId, out var stack) && stack.IsVisible
            ? Union(owner.PhysicalBounds, stack.PhysicalBounds) : owner.PhysicalBounds;

    private ScreenRect DesiredOwnerBounds(QuotaCapsuleWindow owner, TaskbarAnchor anchor) =>
        userPositions.TryGetValue(owner.MonitorId, out var userPosition)
            ? userPosition
            : OverlayPlacementCalculator.Calculate(anchor, owner.LayoutWidth, owner.LayoutBaseHeight, CapsuleFarMargin, CapsuleEdgeGap);

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
        details.SetMaterialTransparency(materialTransparency);
        popover = details;
        popoverOwner = owner;
        details.Apply(snapshot);
        details.ApplyResets(resetFeed);
        details.OpenResetSourceRequested += uri => OpenResetSourceRequested?.Invoke(uri);
        details.RefreshRequested += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        details.OpenCodexRequested += (_, _) => OpenCodexRequested?.Invoke(this, EventArgs.Empty);
        details.OpenDotChatRequested += (_, _) => OpenDotChatRequested?.Invoke(this, EventArgs.Empty);
        details.OpenSubscriptionLoginRequested += (_, _) => OpenSubscriptionLoginRequested?.Invoke(this, EventArgs.Empty);
        details.Closed += (_, _) =>
        {
            popover = null;
            popoverOwner = null;
        };
        details.Show();
        var bounds = CalculatePopoverBounds(owner, details);
        _ = NativeMethods.SetWindowPos(new System.Windows.Interop.WindowInteropHelper(details).Handle, NativeMethods.HwndTopmost,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height, NativeMethods.SwpShowWindow);
        var motionAnchor = GroupBounds(owner);
        details.PlayEntrance(QuotaPopoverWindow.CalculateEntranceOffset(motionAnchor, bounds));
        details.Activate();
    }

    private ScreenRect CalculatePopoverBounds(QuotaCapsuleWindow owner, QuotaPopoverWindow details)
    {
        var anchor = SelectAnchor(topology.Capture(), owner.MonitorId, owner.PhysicalBounds);
        var dpi = anchor?.Dpi ?? 96;
        var scale = dpi / 96d;
        var width = checked((int)Math.Round(PopoverWidth * scale));
        var obstruction = GroupBounds(owner);
        if (anchor is not null)
            details.FitAvailableHeight(PopoverPlacementCalculator.AvailableHeightDip(anchor, obstruction, 8));
        var heightDip = Math.Max(1, details.ActualHeight);
        var height = checked((int)Math.Round(heightDip * scale));
        var alignment = owner.AlignmentBounds;
        var left = alignment.Left + (alignment.Width - width) / 2;
        return anchor is null
            ? new ScreenRect(left, obstruction.Top - height, left + width, obstruction.Top)
            : PopoverPlacementCalculator.Calculate(anchor, obstruction, PopoverWidth, heightDip, 8, alignment);
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
        foreach (var stack in sessionWindows.Values) stack.Close();
        sessionWindows.Clear();
        foreach (var window in windows.Values)
        {
            window.Close();
        }
        windows.Clear();
    }
}
