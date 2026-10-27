using System.Windows.Threading;
using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Host.Platform;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.Resets;
using CodexQuotaTaskbar.Host.Shortcuts;
using CodexQuotaTaskbar.Host.UI;
using Microsoft.Win32;

namespace CodexQuotaTaskbar.Host.Overlay;

internal sealed partial class OverlayCoordinator : IDisposable
{
    private const double CapsuleWidth = QuotaCapsuleWindow.WindowWidth;
    private const double CapsuleHeight = QuotaCapsuleWindow.WindowHeight;
    private const double PopoverWidth = 360;
    private const double CapsuleFarMargin = 0;
    private const double CapsuleEdgeGap = -4;
    private readonly TaskbarTopologySource topology = new();
    private IReadOnlyList<TaskbarAnchor> placementTopology = [];
    private long placementTopologyCaptureCount;
    private readonly Dictionary<string, QuotaCapsuleWindow> windows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionStackWindow> sessionWindows = new(StringComparer.Ordinal);
    private bool unifiedPanel;
    private bool companionEnabled;
    private bool companionReduced;
    private bool companionTyping;
    private readonly bool observePointer;
    private bool typingAvailable;
    private readonly Dictionary<string, ScreenRect> userPositions = new(StringComparer.Ordinal);
    private readonly DispatcherTimer timer;
    private readonly Dispatcher ownerDispatcher = Dispatcher.CurrentDispatcher;
    private volatile bool disposed;
    private bool showAllTaskbars;
    private int materialTransparency;
    private bool sessionLocked;
    private QuotaSnapshot snapshot = QuotaSnapshot.Unavailable("正在连接 Codex…");
    private CodexSessionsSnapshot sessions = CodexSessionsSnapshot.Empty;
    private ResetFeedSnapshot resetFeed = ResetFeedSnapshot.Loading;
    private ResetScheduleSnapshot resetSchedule = ResetScheduleSnapshot.Loading;
    private ResetQueryState resetQueryState = ResetQueryState.Idle;
    private DateTimeOffset? confirmedResetUntil;
    private WebShortcutsSnapshot webShortcuts = new([], null, true);
    private readonly Dictionary<string, WebShortcutIcon> shortcutIcons = new(StringComparer.Ordinal);
    private QuotaPopoverWindow? popover;
    private QuotaCapsuleWindow? popoverOwner;
    private string? dotOwnerId;
    internal event Action<DotAnchor>? DotAnchorChanged;
    internal event Action<bool>? TypingAvailabilityChanged;
    internal bool CanAnimateTyping => !disposed && !bongoStopping && !companionReduced && !sessionLocked
        && System.Windows.SystemParameters.ClientAreaAnimation
        && (bongoSelected && !bongoFailed && windows.Values.Any(window => window.BongoVisible)
            || windows.Values.Any(window => window.CanAnimateTyping));

    internal OverlayCoordinator(bool showAllTaskbars, int materialTransparency = 0, bool unifiedPanel = false,
        Func<CodexQuotaTaskbar.Host.Bongo.BongoRuntime>? bongoRuntimeFactory = null, bool observePointer = true)
    {
        this.showAllTaskbars = showAllTaskbars;
        this.unifiedPanel = unifiedPanel;
        this.materialTransparency = Math.Clamp(materialTransparency, 0, 100);
        createBongoRuntime = bongoRuntimeFactory ?? (() => new CodexQuotaTaskbar.Host.Bongo.BongoRuntime());
        this.observePointer = observePointer;
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
    internal event EventHandler? CloseContextRequested;
    internal event Action<string>? OpenSessionRequested;
    internal event Action<Uri>? OpenResetSourceRequested;
    internal event Action<Uri>? OpenWebShortcutRequested;
    internal event Action<WebShortcutAddRequest>? AddWebShortcutRequested;
    internal event Action<string>? DeleteWebShortcutRequested;
    internal event Action<string, string>? ChangeWebShortcutIconRequested;
    internal event Action<string>? RestoreAutomaticWebShortcutIconRequested;

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
        RefreshBongoSurfaces();
        PublishTypingAvailability();
    }

    internal void SetCompanionTyping(bool enabled)
    {
        companionTyping = enabled;
        foreach (var window in windows.Values) window.SetCompanionTyping(enabled);
        if (!enabled) bongoRuntime?.ResetInput();
        PublishTypingAvailability();
    }

    internal void KeyboardActivity(CompanionInputEvent value)
    {
        if (value.Kind != CompanionInputKind.Reset && (!companionTyping || !CanAnimateTyping)) return;
        if (value.Kind == CompanionInputKind.Reset)
        {
            bongoRuntime?.Accept(value);
            foreach (var window in windows.Values) window.KeyboardActivity(value);
        }
        else if (bongoSelected && !bongoFailed && windows.Values.Any(window => window.BongoVisible)) bongoRuntime?.Accept(value);
        else foreach (var window in windows.Values) window.KeyboardActivity(value);
    }

    private void PublishTypingAvailability()
    {
        var available = CanAnimateTyping;
        if (typingAvailable == available) return;
        typingAvailable = available;
        TypingAvailabilityChanged?.Invoke(available);
    }

    internal void ToggleCompanionTour()
    {
        foreach (var window in windows.Values) window.ToggleCompanionTour();
    }

    internal void SetUnifiedPanel(bool value)
    {
        if (unifiedPanel == value) return;
        // Hiding a top row synchronously raises layout/bounds events. Keep its
        // wanted/deadline state, but prevent those events reattaching it to a card
        // that is about to close. Reconcile transfers it after all old owners go.
        var wasUpdating = dotPresentationUpdating;
        dotPresentationUpdating = true;
        try
        {
            CloseBongoViews();
            popover?.Close();
            foreach (var stack in sessionWindows.Values) stack.Close();
            sessionWindows.Clear();
            foreach (var window in windows.Values)
                if (window.PhysicalBounds.Width > 0) userPositions[window.MonitorId] = BaseBoundsBeforeExtension(window);
            foreach (var window in windows.Values) { PrepareInlineDotOwnerForClose(window); window.Close(); }
            windows.Clear();
            unifiedPanel = value;
        }
        finally { dotPresentationUpdating = wasUpdating; }
        Reconcile();
    }

    internal void SetMaterialTransparency(int value)
    {
        materialTransparency = Math.Clamp(value, 0, 100);
        foreach (var window in windows.Values) window.SetMaterialTransparency(materialTransparency);
        foreach (var stack in sessionWindows.Values) stack.SetMaterialTransparency(materialTransparency);
        popover?.SetMaterialTransparency(materialTransparency);
    }

    private QuotaResetTracker? recoveryTracker;
    internal void SetResetTracker(QuotaResetTracker tracker)
    {
        recoveryTracker = tracker;
        tracker.SetNotice(resetSchedule.Schedule);
        foreach (var window in windows.Values) window.SetResetTracker(tracker);
    }

    internal void ApplyConfirmedReset(DateTimeOffset? until)
    {
        confirmedResetUntil = until;
        foreach (var window in windows.Values) window.ApplyConfirmedReset(until);
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
    }

    internal void ApplyShortcuts(WebShortcutsSnapshot value)
    {
        foreach (var id in shortcutIcons.Keys.ToArray())
        {
            var previous = webShortcuts.Items.FirstOrDefault(item => item.Id == id);
            var next = value.Items.FirstOrDefault(item => item.Id == id);
            if (previous is null || next is null || previous.TargetUri != next.TargetUri
                || previous.ManualIconKey != next.ManualIconKey) shortcutIcons.Remove(id);
        }
        webShortcuts = value;
        popover?.ApplyShortcuts(value);
        foreach (var (id, icon) in shortcutIcons) popover?.ApplyShortcutIcon(id, icon);
        if (popoverOwner is { } owner)
        {
            popover?.UpdateLayout();
            RepositionPopover(owner);
        }
    }

    internal void ApplyShortcutIcon(string id, WebShortcutIcon icon)
    {
        if (!webShortcuts.Items.Any(item => item.Id == id)) return;
        shortcutIcons[id] = icon;
        popover?.ApplyShortcutIcon(id, icon);
    }

    internal void ApplyResetSchedule(ResetScheduleSnapshot value)
    {
        if (resetSchedule == value) return;
        resetSchedule = value;
        recoveryTracker?.SetNotice(value.Schedule);
        foreach (var window in windows.Values) window.ApplyResetSchedule(value);
    }

    internal void ApplyResetQueryState(ResetQueryState value)
    {
        if (value.Revision < resetQueryState.Revision) return;
        resetQueryState = value;
        foreach (var window in windows.Values) window.ApplyResetQueryState(value);
    }

    private void Reconcile()
    {
        if (disposed) return;
        var availableAnchors = CapturePlacementTopology();
        var anchors = availableAnchors
            .Where(anchor => showAllTaskbars || anchor.IsPrimary)
            .ToDictionary(anchor => anchor.MonitorId, StringComparer.Ordinal);

        foreach (var stale in windows.Keys.Except(anchors.Keys, StringComparer.Ordinal).ToArray())
        {
            var wasUpdating = dotPresentationUpdating;
            dotPresentationUpdating = true;
            try
            {
                PrepareInlineDotOwnerForClose(windows[stale]);
                OverlayLifecycleDiagnostics.Local.Window(windows[stale], OverlayLifecycleEvent.TopologyRemoved);
                RemoveBongoView(stale);
                if (sessionWindows.Remove(stale, out var staleStack)) staleStack.Close();
                windows[stale].Close();
                windows.Remove(stale);
                userPositions.Remove(stale);
                dotBaseBounds.Remove(stale);
            }
            finally { dotPresentationUpdating = wasUpdating; }
        }

        foreach (var anchor in anchors.Values)
        {
            if (!windows.TryGetValue(anchor.MonitorId, out var window))
            {
                window = CreateWindow(anchor.MonitorId);
                OverlayLifecycleDiagnostics.Local.Window(window, OverlayLifecycleEvent.OwnerCreated);
                windows.Add(anchor.MonitorId, window);
                if (anchor.IsPrimary || dotOwnerId is null) dotOwnerId = anchor.MonitorId;
                window.Apply(snapshot);
                window.ApplySessions(sessions);
                if (recoveryTracker is not null) window.SetResetTracker(recoveryTracker);
                window.ApplyResetSchedule(resetSchedule);
                window.ApplyResetQueryState(resetQueryState);
                window.ApplyConfirmedReset(confirmedResetUntil);
                window.Show();
                if (!unifiedPanel)
                {
                    var stack = new SessionStackWindow(anchor.MonitorId);
                    if (!observePointer)
                    {
                        stack.Opacity = 0;
                        stack.IsHitTestVisible = false;
                        stack.SourceInitialized += (_, _) =>
                        {
                            WindowInteropPolicy.AttachNoActivate(stack);
                            WindowInteropPolicy.AttachInputTransparent(stack);
                        };
                    }
                    stack.SetMaterialTransparency(materialTransparency);
                    stack.OpenSessionRequested += id => OpenSessionRequested?.Invoke(id);
                    stack.ContextInvoked += (_, _) => ContextRequested?.Invoke(window, EventArgs.Empty);
                    stack.PresentationChanged += (_, _) => UpdatePanelPlacement(window);
                    sessionWindows.Add(anchor.MonitorId, stack);
                    stack.Apply(sessions);
                }
            }

            window.ApplyResets(resetFeed);
            window.SetCompanionTyping(companionTyping);

            var visibilityReason = sessionLocked ? OverlayVisibilityReason.Locked
                : !anchor.IsVisible ? OverlayVisibilityReason.TaskbarHidden
                : TaskbarTopologySource.IsMonitorFullscreen(anchor) ? OverlayVisibilityReason.Fullscreen : OverlayVisibilityReason.Available;
            var shouldShow = visibilityReason == OverlayVisibilityReason.Available;
            OverlayLifecycleDiagnostics.Local.Window(window, OverlayLifecycleEvent.VisibilityPolicy,
                visibilityReason, deduplicate: true);
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
            // Reuse the existing timer; the compact row changes text only by the minute.
            window.RefreshResetSchedule(DateTimeOffset.UtcNow);
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
        RefreshInlineDotPresentation();
        RefreshBongoSurfaces();
        PublishDotAnchor();
        PublishTypingAvailability();
    }

    private void PublishDotAnchor()
    {
        if (disposed) return;
        UpdateDotNotificationPlacement();
        var owner = windows.GetValueOrDefault(dotOwnerId ?? "") ?? windows.Values.FirstOrDefault();
        if (owner is null) return;
        var obstruction = GroupBounds(owner);
        var point = new System.Windows.Point(obstruction.Left + obstruction.Width / 2, obstruction.Top);
        var work = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)point.X, (int)point.Y)).WorkingArea;
        var dpi = NativeMethods.GetDpiForWindow(new System.Windows.Interop.WindowInteropHelper(owner).Handle);
        var lane = ResolveDotLane(owner, new(work.Left, work.Top, work.Right, work.Bottom), dpi);
        DotAnchorChanged?.Invoke(new((int)point.X, (int)point.Y, obstruction.Bottom, work.Left, work.Top,
            work.Right, work.Bottom, dpi, owner.IsVisible && !sessionLocked, materialTransparency, lane));
    }

    private QuotaCapsuleWindow CreateWindow(string monitorId)
    {
        var window = new QuotaCapsuleWindow(monitorId, unifiedPanel)
        {
            UsesCoordinatedPlacement = true,
        };
        window.SetPointerObservationAllowed(observePointer);
        if (!observePointer)
        {
            // Marked demo/proof surfaces may exercise real native lifecycles
            // without displaying a second island on the user's desktop.
            window.Opacity = 0;
            window.IsHitTestVisible = false;
            window.SourceInitialized += (_, _) => WindowInteropPolicy.AttachInputTransparent(window);
        }
        window.SetMaterialTransparency(materialTransparency);
        window.SetCompanion(companionEnabled, companionReduced);
        window.SetCompanionTyping(companionTyping);
        window.IsVisibleChanged += (_, _) => PublishTypingAvailability();
        window.PrimaryInvoked += (_, _) => TogglePopover(window);
        window.CompanionInvoked += (_, _) =>
        {
            InvokeCompanion(window);
        };
        window.CompanionGazeChanged += point =>
        {
            if (bongoSelected && companionTyping && CanAnimateTyping) bongoRuntime?.SetPointer(point.X, point.Y);
        };
        window.ContextInvoked += (_, _) =>
        {
            dotOwnerId = window.MonitorId;
            popover?.Close();
            ContextRequested?.Invoke(window, EventArgs.Empty);
        };
        window.UserMoveRequested += requested => OnWindowMoveRequested(window, requested);
        window.DotMarkerInvoked += (_, _) => HandleDotMarkerInvoked(window);
        window.BoundsChanged += (_, _) => UpdateDotNotificationPlacement();
        window.OpenSessionRequested += threadId => OpenSessionRequested?.Invoke(threadId);
        window.OpenResetSourceRequested += uri => OpenResetSourceRequested?.Invoke(uri);
        window.PresentationChanged += (_, _) =>
        {
            UpdatePanelPlacement(window);
            // An outro retains its owner only while the shared child remains
            // attached. Its final presentation event releases the idle owner.
            if (dotTopKind == DotTopKind.None && !inlineDotWanted
                && ReferenceEquals(inlineDotOwner, window) && !window.DotTopExtensionVisible)
                inlineDotOwner = null;
        };
        return window;
    }

    private void OnWindowMoveRequested(QuotaCapsuleWindow window, ScreenRect requested)
    {
        dotOwnerId = window.MonitorId;
        UpdatePanelPlacement(window, requestedOwner: requested);
        // Persist the position that was actually applied, not the off-screen pointer request.
        userPositions[window.MonitorId] = window.CorePanelBounds;
        dotBaseBounds[window.MonitorId] = window.CorePanelBounds;
        PublishDotAnchor();
        if (popover is null || !ReferenceEquals(popoverOwner, window))
        {
            return;
        }
        RepositionPopover(window);
    }

    private ScreenRect BaseBoundsBeforeExtension(QuotaCapsuleWindow owner) =>
        dotBaseBounds.TryGetValue(owner.MonitorId, out var original) ? original : owner.CorePanelBounds;

    private static ScreenRect WorkingAreaFromAnchor(TaskbarAnchor anchor) => anchor.Edge switch
    {
        TaskbarEdge.Top => anchor.MonitorBounds with { Top = Math.Max(anchor.MonitorBounds.Top, anchor.TaskbarBounds.Bottom) },
        TaskbarEdge.Bottom => anchor.MonitorBounds with { Bottom = Math.Min(anchor.MonitorBounds.Bottom, anchor.TaskbarBounds.Top) },
        TaskbarEdge.Left => anchor.MonitorBounds with { Left = Math.Max(anchor.MonitorBounds.Left, anchor.TaskbarBounds.Right) },
        TaskbarEdge.Right => anchor.MonitorBounds with { Right = Math.Min(anchor.MonitorBounds.Right, anchor.TaskbarBounds.Left) },
        _ => anchor.MonitorBounds,
    };

    private IReadOnlyList<TaskbarAnchor> CapturePlacementTopology()
    {
        placementTopologyCaptureCount++;
        placementTopology = topology.Capture();
        return placementTopology;
    }

    private void UpdatePanelPlacement(QuotaCapsuleWindow owner, TaskbarAnchor? knownAnchor = null, ScreenRect? requestedOwner = null)
    {
        if (!owner.IsVisible || !dotPlacing.Add(owner.MonitorId)) return;
        try
        {
            // WPF clocks already provide frame callbacks. Their placement work
            // uses the most recent reconciliation instead of enumerating all
            // desktop HWNDs once per frame. Explicit moves and environment /
            // reconciliation updates still capture fresh monitor information.
            var availableAnchors = knownAnchor is not null ? placementTopology
                : requestedOwner is not null || placementTopology.Count == 0
                    ? CapturePlacementTopology() : placementTopology;
            var anchor = knownAnchor ?? SelectAnchor(availableAnchors, owner.MonitorId, requestedOwner ?? owner.PhysicalBounds);
            if (anchor is null) return;
            var scale = anchor.Dpi / 96d;
            var currentScale = owner.PhysicalBounds.Width > 0 ? owner.PhysicalBounds.Width / owner.LayoutWidth : scale;
            var oldExtra = (int)Math.Round(owner.DotTopExtensionHeight * currentScale);
            var oldFooter = (int)Math.Round(owner.ResetScheduleHeightForLayout * currentScale);
            ScreenRect? requestedBase = requestedOwner is { } requested
                ? requested with { Top = requested.Top + oldExtra, Bottom = requested.Bottom - oldFooter } : null;
            var desiredBase = requestedBase ?? (owner.IsUserDragging ? owner.CorePanelBounds : DesiredOwnerBounds(owner, anchor));
            var work = WorkingAreaFromAnchor(anchor);
            var trailing = !owner.IsUnifiedPanel && sessions.Sessions.Count > 0 && sessionWindows.ContainsKey(owner.MonitorId)
                ? (int)Math.Round(Math.Max(0, ReservedSessionHeight(sessions.Sessions.Count) - 16) * scale) : 0;
            UpdateFooterAvailability(owner, work, scale, trailing);
            var maximumTop = Math.Max(0, (work.Height - trailing - 8) / scale
                - owner.CoreBodyReservedHeight - FooterReservation(owner) - 1);
            // A reply is content-sized. Reapply only its maximum when the work
            // area changes, including restoring natural height on a larger screen.
            // This must not reopen it or repeat Shown()/focus behavior.
            if (owner.DotTopExtensionVisible && (owner.DotTopContent is DotInlineView
                || owner.DotTopExtensionTargetHeight - 1 > maximumTop))
            {
                var content = owner.DotTopContent;
                var fits = content switch
                {
                    DotInlineView reply => FitAdaptiveReply(owner, reply, maximumTop),
                    DotTopNoticeView notice => notice.PreferredHeightDip <= maximumTop,
                    _ => false,
                };
                if (fits && content is DotInlineView resizedReply)
                    owner.ShowDotTopExtension(resizedReply, resizedReply.PreferredHeightDip, acceptsReply: true);
                else if (!fits)
                {
                    if (ReferenceEquals(inlineDotOwner, owner)) inlineDotOwner = null;
                    owner.HideDotTopExtension(); inlineDotView?.Hidden();
                    if (dotTopKind != DotTopKind.Notice) dotTopKind = DotTopKind.None;
                    owner.ToolTip = "当前工作区高度不足，提醒标记与草稿保留。";
                }
            }
            var extra = (int)Math.Round(owner.DotTopExtensionHeight * scale);
            var footer = (int)Math.Round(owner.ResetScheduleHeightForLayout * scale);
            var basePlacement = owner.IsUnifiedPanel
                ? CalculatePanelBounds(anchor, desiredBase, owner.CoreBodyDesiredHeight, owner.CoreBodyReservedHeight)
                : ResizeOwner(anchor, desiredBase, owner.LayoutWidth, owner.CoreBodyDesiredHeight);
            var reservedTail = owner.IsUnifiedPanel
                ? Math.Max(0, (int)Math.Round((owner.CoreBodyReservedHeight - owner.CoreBodyDesiredHeight) * scale)) : trailing;
            // Reservation is for a future expanded task area, not visible
            // content. On a very short work area it must not turn an otherwise
            // fitting current card into an invalid animated rectangle.
            reservedTail = Math.Min(reservedTail, Math.Max(0, work.Height - basePlacement.Height - extra - footer));
            // This rectangle contains the middle body only. Neither animated end
            // becomes a saved position or feeds back into the next frame.
            dotBaseBounds[owner.MonitorId] = basePlacement;
            var bounds = CalculateExtensionBounds(work, basePlacement, extra, footer, reservedTail);
            owner.Place(bounds);
            if (!owner.IsUnifiedPanel && sessions.Sessions.Count > 0 && sessionWindows.TryGetValue(owner.MonitorId, out var stack))
            {
                // Tasks remain under the base card; a top extension never steals their viewport.
                var placement = SessionStackPlacementCalculator.CalculateBelow(anchor, owner.BasePanelBounds,
                    SessionStackWindow.StackWidth, ReservedSessionHeight(sessions.Sessions.Count), -16);
                if (stack.IsVisible) stack.Place(ResizeStackHeight(placement.Stack, stack.DesiredHeight, anchor.Dpi));
            }
            PublishDotAnchor();
            if (ReferenceEquals(popoverOwner, owner)) RepositionPopover(owner);
            var afterExtra = (int)Math.Round(owner.DotTopExtensionHeight * scale);
            var afterFooter = (int)Math.Round(owner.ResetScheduleHeightForLayout * scale);
            if (afterExtra != extra || afterFooter != footer)
            {
                // A pending notice may become presentable while the base is being placed.
                // Apply that new top extent in this turn, not at the next 750ms reconciliation.
                dotPlacing.Remove(owner.MonitorId);
                UpdatePanelPlacement(owner, anchor, basePlacement with
                {
                    Top = basePlacement.Top - afterExtra,
                    Bottom = basePlacement.Bottom + afterFooter,
                });
            }
        }
        finally { dotPlacing.Remove(owner.MonitorId); }
    }

    private static bool FitAdaptiveReply(QuotaCapsuleWindow owner, DotInlineView reply, double maximumHeightDip)
    {
        reply.RecalculatePreferredHeight(owner.DotReplyContentWidthDip);
        return reply.SetAvailableHeight(maximumHeightDip);
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

    private ScreenRect BaseGroupBounds(QuotaCapsuleWindow owner) =>
        sessionWindows.TryGetValue(owner.MonitorId, out var stack) && stack.IsVisible
            ? Union(owner.PhysicalBounds, stack.PhysicalBounds) : owner.PhysicalBounds;

    private ScreenRect GroupBounds(QuotaCapsuleWindow owner) => BaseGroupBounds(owner);

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
        if (!TryCalculatePopoverBounds(owner, popover, out var bounds))
        {
            popover.Close();
            return;
        }
        popoverBounds = bounds;
        UpdateDotNotificationPlacement();
        _ = NativeMethods.SetWindowPos(new System.Windows.Interop.WindowInteropHelper(popover).Handle, NativeMethods.HwndTopmost,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    private void TogglePopover(QuotaCapsuleWindow owner)
    {
        OverlayLifecycleDiagnostics.Local.Window(owner, OverlayLifecycleEvent.DetailsToggle);
        if (popover is not null)
        {
            popover.RequestClose();
            return;
        }

        CloseContextRequested?.Invoke(this, EventArgs.Empty);
        dotOwnerId = owner.MonitorId;
        var details = new QuotaPopoverWindow { Owner = owner };
        OverlayLifecycleDiagnostics.Local.Window(details, OverlayLifecycleEvent.DetailsCreated);
        details.SetMaterialTransparency(materialTransparency);
        popover = details;
        popoverOwner = owner;
        details.Apply(snapshot);
        details.ApplyShortcuts(webShortcuts);
        foreach (var (id, icon) in shortcutIcons) details.ApplyShortcutIcon(id, icon);
        details.OpenWebShortcutRequested += uri => OpenWebShortcutRequested?.Invoke(uri);
        details.AddWebShortcutRequested += input => AddWebShortcutRequested?.Invoke(input);
        details.DeleteWebShortcutRequested += id => DeleteWebShortcutRequested?.Invoke(id);
        details.ChangeWebShortcutIconRequested += (id, path) => ChangeWebShortcutIconRequested?.Invoke(id, path);
        details.RestoreAutomaticWebShortcutIconRequested += id => RestoreAutomaticWebShortcutIconRequested?.Invoke(id);
        details.RefreshRequested += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        details.OpenCodexRequested += (_, _) => OpenCodexRequested?.Invoke(this, EventArgs.Empty);
        details.OpenSubscriptionLoginRequested += (_, _) => OpenSubscriptionLoginRequested?.Invoke(this, EventArgs.Empty);
        details.Closed += (_, _) =>
        {
            OverlayLifecycleDiagnostics.Local.Window(details, OverlayLifecycleEvent.DetailsClosed);
            popover = null;
            popoverOwner = null;
            popoverBounds = null;
            PublishDotAnchor();
        };
        if (!TryCalculatePopoverBounds(owner, details, out var bounds))
        {
            details.Close();
            owner.ToolTip = "当前屏幕空间不足以展开详情，请先移动点岛或收起其他页面。";
            return;
        }
        owner.ToolTip = null;
        popoverBounds = bounds;
        var detailsHandle = new System.Windows.Interop.WindowInteropHelper(details).EnsureHandle();
        _ = NativeMethods.SetWindowPos(detailsHandle, NativeMethods.HwndTopmost,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height, NativeMethods.SwpNoActivate);
        details.ShowActivated = observePointer;
        if (!observePointer)
        {
            details.Opacity = 0;
            details.IsHitTestVisible = false;
            WindowInteropPolicy.AttachNoActivate(details);
            WindowInteropPolicy.AttachInputTransparent(details);
        }
        details.Show();
        _ = NativeMethods.SetWindowPos(detailsHandle, NativeMethods.HwndTopmost,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height, NativeMethods.SwpShowWindow | NativeMethods.SwpNoActivate);
        var motionAnchor = PopupAnchorBounds(owner);
        var surfaceInset = (int)Math.Ceiling(details.PlacementSurfaceInsetDip * DpiFor(owner) / 96d);
        var visibleDetails = new ScreenRect(bounds.Left + surfaceInset, bounds.Top + surfaceInset,
            bounds.Right - surfaceInset, bounds.Bottom - surfaceInset);
        details.PlayEntrance(QuotaPopoverWindow.CalculateEntranceOffset(motionAnchor, visibleDetails));
        if (observePointer) details.Activate();
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

    private void OnEnvironmentChanged(object? sender, EventArgs eventArgs) => OnOwnerThread(Reconcile);

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs eventArgs)
    {
        OnOwnerThread(() =>
        {
            sessionLocked = eventArgs.Reason == SessionSwitchReason.SessionLock
                || (sessionLocked && eventArgs.Reason != SessionSwitchReason.SessionUnlock);
            Reconcile();
        });
    }

    private void OnOwnerThread(Action action)
    {
        if (disposed || ownerDispatcher.HasShutdownStarted) return;
        void Apply() { if (!disposed) action(); }
        if (ownerDispatcher.CheckAccess()) Apply();
        else
        {
            try { ownerDispatcher.BeginInvoke((Action)Apply); }
            catch (InvalidOperationException) when (disposed || ownerDispatcher.HasShutdownStarted) { }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        DisposeInlineDot();
        disposed = true;
        DisposeBongo();
        PublishTypingAvailability();
        timer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnEnvironmentChanged;
        SystemEvents.UserPreferenceChanged -= OnEnvironmentChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        popover?.Close();
        foreach (var stack in sessionWindows.Values) stack.Close();
        sessionWindows.Clear();
        foreach (var window in windows.Values)
        {
            PrepareInlineDotOwnerForClose(window);
            window.Close();
        }
        windows.Clear();
    }
}
