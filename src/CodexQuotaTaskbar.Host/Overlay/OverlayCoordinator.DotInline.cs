using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Overlay;

internal sealed partial class OverlayCoordinator
{
    private enum DotTopKind { None, Notice, Reply }
    private bool inlineDotMode;
    private DotInlineState? inlineDotState;
    private DotInlineView? inlineDotView;
    private DotTopNoticeView? dotTopNotice;
    private QuotaCapsuleWindow? inlineDotOwner;
    private bool inlineDotWanted;
    private DotTopKind dotTopKind;
    private readonly Stopwatch dotNotificationClock = Stopwatch.StartNew();
    private DispatcherTimer? dotNotificationTimer;
    private TimeSpan? dotNotificationDeadline;
    private bool dotNotificationWanted;
    private bool dotNotificationInteracted, dotPresentationUpdating;
    private string? dotNotificationId;
    private readonly Dictionary<string, ScreenRect> dotBaseBounds = new(StringComparer.Ordinal);
    private readonly HashSet<string> dotPlacing = new(StringComparer.Ordinal);
    private QuotaCapsuleWindow? prospectiveDotOwner;
    private double prospectiveDotHeight;
    private bool? publishedDotReplyAllowed;
    private string? dotReadIntentId;
    private DotTopNoticeView? dotReadIntentView;
    private QuotaCapsuleWindow? dotReadIntentOwner;
    private DotInlineAction? dotReadIntentAction;
    internal event Action<DotInlineAction>? InlineDotActionRequested;
    internal event Action<bool>? DotReplyAvailabilityChanged;
    // Both appearances are notification-only. Retained send state belongs to
    // the receiver and is never cancelled by this presentation policy.
    internal bool DotReplyAllowed => false;

    internal void SetInlineDotMode(bool enabled)
    {
        if (inlineDotMode == enabled) return;
        inlineDotMode = enabled; dotLanes.Clear();
        if (!enabled) { CloseInlineDot(); HideDotNotification(); }
        PublishDotReplyAvailability(); RefreshInlineDotPresentation(); PublishDotAnchor();
    }

    private void PublishDotReplyAvailability()
    {
        var allowed = DotReplyAllowed;
        if (publishedDotReplyAllowed == allowed) return;
        publishedDotReplyAllowed = allowed;
        // Consume only the old presentation before detaching. It must not reattach
        // from a bounds event while a mode switch is restoring the base card.
        HideDotNotification();
        CloseInlineDot();
        DotReplyAvailabilityChanged?.Invoke(allowed);
        foreach (var owner in windows.Values) owner.SetDotReplyAffordance(allowed);
    }

    internal void ApplyInlineDotState(DotInlineState state) => OnOwnerThread(() =>
    {
        if (inlineDotState is { } previous && previous.SessionId == state.SessionId && state.Sequence <= previous.Sequence) return;
        var newNotification = state.Online && state.NotificationSerial > 0 && state.NotificationId is not null
            && (inlineDotState?.SessionId != state.SessionId || state.NotificationSerial > inlineDotState.NotificationSerial);
        inlineDotState = state;
        inlineDotView?.ApplyState(state);
        if (!state.Online) inlineDotView?.SetOffline();
        if (newNotification && !inlineDotWanted && inlineDotView?.IsInteracting != true)
        {
            dotNotificationId = state.NotificationId; dotNotificationWanted = true;
            dotNotificationInteracted = false; dotNotificationDeadline = null; dotNotificationTimer?.Stop();
        }
        if (!state.Online) HideDotNotification();
        else if (state.UnreadCount == 0) HideDotNotification(animate: true);
        RefreshInlineDotPresentation();
    });

    private DotInlineView EnsureInlineDotView()
    {
        if (inlineDotView is not null) return inlineDotView;
        var view = new DotInlineView(); inlineDotView = view;
        view.ActionRequested += action =>
        {
            // Even a queued UI event cannot expose a cat-mode send or recovery control.
            if (!DotReplyAllowed && action.Kind is "send" or "reviewed_sent" or "reviewed_not_sent") return;
            InlineDotActionRequested?.Invoke(action); RefreshDotMarkers();
        };
        view.CloseRequested += (_, _) => CloseInlineDot();
        view.InteractionChanged += (_, _) => { if (view.IsInteracting) HideDotNotification(); };
        if (inlineDotState is { } state) view.ApplyState(state);
        return view;
    }

    internal static DotInlineAction? CurrentNoticeAcknowledgement(DotInlineState? state) =>
        state is { Online: true, UnreadCount: > 0, ReadSequence: > 0 }
        && Guid.TryParseExact(state.ReadToken, "N", out _)
        && Guid.TryParseExact(state.SessionId, "N", out _)
            ? new DotInlineAction("ack-notice", ReadToken: state.ReadToken,
                ReadSessionId: state.SessionId, ReadSequence: state.ReadSequence) : null;

    internal void HandleDotMarkerInvoked(QuotaCapsuleWindow owner)
    {
        if (disposed || dotPresentationUpdating || !inlineDotMode || !dotPresentationEnabled) return;
        dotOwnerId = owner.MonitorId;
        if (!owner.BongoHasFrame)
        {
            // A missing/failed model keeps the existing white-ball entry point.
            OpenDotChatRequested?.Invoke(owner, EventArgs.Empty);
            return;
        }
        // Capture the receiver's current signed batch at click time. Never clear
        // the local count optimistically or repurpose the parent display sequence.
        var action = CurrentNoticeAcknowledgement(inlineDotState);
        if (action is null) return;
        CancelDisplayedNoticeRead();
        InlineDotActionRequested?.Invoke(action);
    }

    internal void OpenInlineDot(QuotaCapsuleWindow? preferredOwner = null, bool focus = true)
    {
        if (disposed || dotPresentationUpdating || !inlineDotMode || !dotPresentationEnabled) return;
        var owner = preferredOwner ?? windows.GetValueOrDefault(dotOwnerId ?? "") ?? windows.Values.FirstOrDefault();
        if (owner is null) return;
        dotOwnerId = owner.MonitorId;
        CloseContextRequested?.Invoke(this, EventArgs.Empty); popover?.Close();
        if (!DotReplyAllowed)
        {
            // Cat mode has only the temporary single-line notice. Explicit marker,
            // model and notice clicks may replay that summary, never a reader or reply.
            // Deliberate replay can acknowledge only the fully visible short
            // message after the intro. A long/streaming summary remains unread.
            inlineDotWanted = false;
            inlineDotView?.Hidden();
            // An explicit replay resets only the quiet deadline. Retain an
            // attached notice so a click during its animation cannot flash
            // through zero or restart the same visual clock.
            dotNotificationTimer?.Stop(); dotNotificationDeadline = null;
            var state = inlineDotState;
            var message = state is { Online: true, UnreadCount: > 0 }
                ? state.Messages.FirstOrDefault(item => item.Id == dotNotificationId && item.Unread)
                    ?? state.Messages.LastOrDefault(item => item.Unread)
                : null;
            if (message is not null)
            {
                dotNotificationId = message.Id; dotNotificationWanted = true;
                dotNotificationInteracted = false;
                UpdateDotNotificationPlacement();
                if (focus) RequestDisplayedNoticeRead(owner, message.Id);
            }
            else if (state is { Online: true, EarlierUnreadCount: > 0 })
            {
                // This row explicitly represents a count, never fabricated
                // message text. It offers only an intentional batch mark.
                dotNotificationId = null; dotNotificationWanted = true;
                dotNotificationInteracted = false;
                UpdateDotNotificationPlacement();
            }
            else HideDotNotification();
            RefreshDotMarkers();
            return;
        }
        HideDotNotification();
        inlineDotWanted = true;
        AttachInlineDot(owner);
        if (focus && DotReplyAllowed && observePointer && owner.DotInlineVisible && owner.IsVisible)
        { owner.EnableExplicitReplyActivation(); owner.Activate(); inlineDotView!.ActivateForReply(); }
    }

    private double AvailableDotTopHeight(QuotaCapsuleWindow owner, double requestedTopHeightDip = 0,
        double minimumTopHeightDip = 0)
    {
        var work = WorkAreaFor(owner); var scale = DpiFor(owner) / 96d;
        var stack = !owner.IsUnifiedPanel && sessions.Sessions.Count > 0 ? Math.Max(0, ReservedSessionHeight(sessions.Sessions.Count) - 16) : 0;
        var budget = Math.Max(0, (work.Height - 8) / scale - owner.CoreBodyReservedHeight - stack);
        var previousOwner = prospectiveDotOwner; var previousHeight = prospectiveDotHeight;
        prospectiveDotOwner = owner;
        prospectiveDotHeight = budget >= minimumTopHeightDip ? Math.Min(requestedTopHeightDip, budget) : 0;
        try
        {
            // Applying the footer limit can synchronously raise placement. Keep
            // this prospective top reservation visible during that reentry;
            // otherwise the nested placement could immediately restore it.
            UpdateFooterAvailability(owner, work, scale, (int)Math.Round(stack * scale));
            return Math.Max(0, budget - FooterReservation(owner) - 1);
        }
        finally { prospectiveDotOwner = previousOwner; prospectiveDotHeight = previousHeight; }
    }

    private void UpdateFooterAvailability(QuotaCapsuleWindow owner, ScreenRect work, double scale, int trailingPixels)
    {
        // A new/retained message takes precedence over the optional footer on
        // an exceptionally short work area. A closing top still occupies its
        // current height; an opening top reserves its target before it moves.
        var top = Math.Max(ReferenceEquals(prospectiveDotOwner, owner) ? prospectiveDotHeight : 0,
            Math.Max(owner.DotTopExtensionHeight, owner.DotTopExtensionTargetHeight));
        var available = (work.Height - 8 - trailingPixels) / scale - owner.CoreBodyDesiredHeight - top;
        owner.SetResetSchedulePresentationAllowed(available >= QuotaCapsuleWindow.ResetScheduleRowHeight);
    }

    // A disappearing footer still occupies its full reservation until its
    // short outro completes. Otherwise a large white reply would be resized
    // on every footer frame rather than once at the final available height.
    private static double FooterReservation(QuotaCapsuleWindow owner) =>
        owner.ResetScheduleHeightForLayout > 0 || owner.ResetScheduleTargetHeightForLayout > 0
            ? QuotaCapsuleWindow.ResetScheduleRowHeight : 0;

    private void AttachInlineDot(QuotaCapsuleWindow owner)
    {
        // A late reconciliation after a mode switch must not construct a cat reader,
        // nor keep inlineWanted set and suppress subsequent single-line notifications.
        if (!DotReplyAllowed) { inlineDotWanted = false; return; }
        var reply = EnsureInlineDotView();
        reply.RecalculatePreferredHeight(owner.DotReplyContentWidthDip);
        if (!reply.SetAvailableHeight(AvailableDotTopHeight(owner, reply.PreferredHeightDip + 1, reply.MinimumRequiredHeightDip + 1)))
        { NoTopRoom(owner); return; }
        if (inlineDotOwner is { } old && !ReferenceEquals(old, owner)) DetachInlineDotOwner(old);
        inlineDotOwner = owner; owner.ToolTip = null; dotTopKind = DotTopKind.Reply;
        owner.ShowDotTopExtension(reply, reply.PreferredHeightDip, acceptsReply: true);
        reply.Shown();
        if (observePointer) owner.EnableExplicitReplyActivation();
        UpdatePanelPlacement(owner); RefreshDotMarkers();
    }

    private void NoTopRoom(QuotaCapsuleWindow owner)
    {
        if (inlineDotOwner is { } previous) DetachInlineDotOwner(previous);
        owner.ToolTip = "当前工作区高度不足以展开完整内容；未读和草稿保留，可移动点岛后再试。";
    }

    internal static bool CanFitInlineDot(int workHeightPixels, double scale, double baseHeightDip, double inlineHeightDip) =>
        double.IsFinite(scale) && scale > 0 && double.IsFinite(baseHeightDip) && baseHeightDip > 0
        && double.IsFinite(inlineHeightDip) && inlineHeightDip > 0
        && (baseHeightDip + inlineHeightDip) * scale <= workHeightPixels - 8;

    internal void CloseInlineDot()
    {
        HideDotNotification();
        inlineDotWanted = false;
        if (inlineDotOwner is { } owner) DetachInlineDotOwner(owner);
        inlineDotView?.Hidden();
        dotTopKind = DotTopKind.None; RefreshDotMarkers();
    }

    private void DetachInlineDotOwner(QuotaCapsuleWindow owner)
    {
        if (ReferenceEquals(owner, inlineDotOwner)) inlineDotOwner = null;
        owner.HideDotTopExtension();
        inlineDotView?.Hidden();
        if (!disposed && owner.IsVisible) UpdatePanelPlacement(owner);
    }

    private void PrepareInlineDotOwnerForClose(QuotaCapsuleWindow owner)
    {
        if (!ReferenceEquals(inlineDotOwner, owner)) return;
        var wasUpdating = dotPresentationUpdating;
        dotPresentationUpdating = true;
        try
        {
            inlineDotOwner = null;
            owner.HideDotTopExtension();
            inlineDotView?.Hidden();
        }
        finally { dotPresentationUpdating = wasUpdating; }
    }

    private void RefreshInlineDotPresentation()
    {
        if (disposed || dotPresentationUpdating) return;
        PublishDotReplyAvailability(); RefreshDotMarkers();
        if (!inlineDotMode || !dotPresentationEnabled)
        {
            HideDotNotification(); if (inlineDotOwner is not null) CloseInlineDot(); return;
        }
        if (inlineDotWanted && inlineDotOwner is null)
        {
            var owner = windows.GetValueOrDefault(dotOwnerId ?? "") ?? windows.Values.FirstOrDefault();
            if (owner is not null) AttachInlineDot(owner);
        }
        UpdateDotNotificationPlacement();
    }

    private void RefreshDotMarkers()
    {
        var enabled = inlineDotMode && dotPresentationEnabled;
        var unread = enabled ? inlineDotState?.UnreadCount ?? 0 : 0;
        foreach (var owner in windows.Values)
        { owner.SetDotReplyAffordance(false); owner.SetDotMarker(unread, canAcknowledge: CurrentNoticeAcknowledgement(inlineDotState) is not null); }
    }

    private void EnsureDotNotification()
    {
        if (dotTopNotice is not null) return;
        dotTopNotice = new DotTopNoticeView();
        dotTopNotice.OpenRequested += (_, _) => OpenInlineDot(inlineDotOwner);
        dotTopNotice.InteractionChanged += (_, _) => HandleDotNotificationInteraction(dotTopNotice.IsInteracting);
        dotNotificationTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) };
        dotNotificationTimer.Tick += (_, _) => CheckDotNotificationTimeout(dotNotificationClock.Elapsed);
    }

    internal void HandleDotNotificationInteraction(bool interacting)
    {
        dotNotificationInteracted = interacting;
        if (interacting) { dotNotificationTimer?.Stop(); dotNotificationDeadline = null; }
        else if (dotNotificationWanted) UpdateDotNotificationPlacement();
    }

    private void UpdateDotNotificationPlacement()
    {
        if (dotPresentationUpdating) return;
        dotPresentationUpdating = true;
        try { UpdateDotTopNoticeCore(); }
        finally { dotPresentationUpdating = false; }
    }
    private void UpdateDotTopNoticeCore()
    {
        if (!inlineDotMode || !dotPresentationEnabled || !dotNotificationWanted || inlineDotWanted
            || inlineDotState is not { Online: true } state || state.UnreadCount == 0)
        { HideDotNotificationVisual(); return; }
        if (dotNotificationDeadline is { } due && dotNotificationClock.Elapsed >= due) { HideDotNotification(animate: true); return; }
        var message = state.Messages.FirstOrDefault(item => item.Id == dotNotificationId && item.Unread);
        var earlierOnly = message is null && state.EarlierUnreadCount > 0 && !state.Messages.Any(item => item.Unread);
        var owner = windows.GetValueOrDefault(dotOwnerId ?? "") ?? windows.Values.FirstOrDefault();
        if (message is null && !earlierOnly || owner is null || !owner.IsVisible || sessionLocked)
        { HideDotNotificationVisual(); return; }
        EnsureDotNotification();
        if (AvailableDotTopHeight(owner, dotTopNotice!.PreferredHeightDip + 1, dotTopNotice.PreferredHeightDip + 1) < dotTopNotice.PreferredHeightDip)
        { HideDotNotificationVisual(); return; }
        if (earlierOnly) dotTopNotice.DisplayEarlier(state);
        else dotTopNotice.Display(message!, state);
        if (dotReadIntentId is not null && dotReadIntentId != message?.Id) CancelDisplayedNoticeRead();
        if (inlineDotOwner is { } old && !ReferenceEquals(old, owner)) DetachInlineDotOwner(old);
        inlineDotOwner = owner; dotTopKind = DotTopKind.Notice;
        if (!ReferenceEquals(owner.DotTopContent, dotTopNotice)
            || owner.DotTopExtensionTargetHeight != dotTopNotice.PreferredHeightDip + 1)
            owner.ShowDotTopExtension(dotTopNotice, dotTopNotice.PreferredHeightDip);
        UpdatePanelPlacement(owner);
        if (dotNotificationDeadline is null && !dotTopNotice.IsInteracting && !dotNotificationInteracted)
        {
            dotNotificationDeadline = dotNotificationClock.Elapsed + TimeSpan.FromSeconds(10);
            dotNotificationTimer!.Interval = TimeSpan.FromSeconds(10); dotNotificationTimer.Start();
        }
    }

    internal void CheckDotNotificationTimeout(TimeSpan elapsed)
    {
        if (dotNotificationDeadline is not { } deadline) return;
        if (elapsed < deadline)
        {
            dotNotificationTimer?.Stop();
            if (dotNotificationTimer is not null) { dotNotificationTimer.Interval = deadline - elapsed; dotNotificationTimer.Start(); }
            return;
        }
        if (dotTopNotice?.IsInteracting == true) { dotNotificationDeadline = null; dotNotificationTimer?.Stop(); return; }
        HideDotNotification(animate: true);
    }

    private void HideDotNotification(bool animate = false)
    { CancelDisplayedNoticeRead(); dotNotificationWanted = false; dotNotificationDeadline = null; HideDotNotificationVisual(animate); }

    private void RequestDisplayedNoticeRead(QuotaCapsuleWindow owner, string id)
    {
        CancelDisplayedNoticeRead();
        if (dotTopNotice is null) return;
        var action = dotTopNotice.CreateAcknowledgement(singleDisplayedMessage: true);
        if (action is null) return;
        dotReadIntentId = id; dotReadIntentView = dotTopNotice; dotReadIntentOwner = owner;
        dotReadIntentAction = action;
        owner.PresentationChanged += OnDisplayedNoticeReadLayout;
        dotTopNotice.LayoutUpdated += OnDisplayedNoticeReadLayout;
        TryReadDisplayedNotice();
    }

    private void OnDisplayedNoticeReadLayout(object? sender, EventArgs args) => TryReadDisplayedNotice();

    private void TryReadDisplayedNotice()
    {
        if (dotReadIntentId is not { } id || dotReadIntentOwner is not { } owner || dotReadIntentView is not { } view) return;
        var state = inlineDotState;
        if (!dotNotificationWanted || dotTopKind != DotTopKind.Notice || state is not { Online: true, Ready: true, Busy: false }
            || !ReferenceEquals(owner, inlineDotOwner) || view.DisplayedMessageId != id
            || state.ReadToken != dotReadIntentAction?.ReadToken || state.SessionId != dotReadIntentAction?.ReadSessionId
            || state.Messages.FirstOrDefault(item => item.Id == id) is not { Unread: true })
        { CancelDisplayedNoticeRead(); return; }
        if (!owner.IsVisible) { CancelDisplayedNoticeRead(); return; }
        if (!owner.DotTopExtensionVisible || !ReferenceEquals(owner.DotTopContent, view)
            || owner.DotTopExtensionHeight < view.PreferredHeightDip + .99
            || owner.DotTopExtensionTargetHeight < view.PreferredHeightDip + .99
            || !view.MessageLayoutReady || !view.IsFullyVisibleWithin(owner)) return;
        // No read of an ellipsized or receiver-truncated body. Explicit marking
        // remains available, but no new large reader or web window is opened.
        if (!view.DisplayedTextIsComplete) { CancelDisplayedNoticeRead(); return; }
        var action = dotReadIntentAction;
        CancelDisplayedNoticeRead();
        if (action is not null) InlineDotActionRequested?.Invoke(action);
    }

    private void CancelDisplayedNoticeRead()
    {
        if (dotReadIntentOwner is { } owner) owner.PresentationChanged -= OnDisplayedNoticeReadLayout;
        if (dotReadIntentView is { } view) view.LayoutUpdated -= OnDisplayedNoticeReadLayout;
        dotReadIntentId = null; dotReadIntentOwner = null; dotReadIntentView = null; dotReadIntentAction = null;
    }

    private void HideDotNotificationVisual(bool animate = false)
    {
        CancelDisplayedNoticeRead();
        dotNotificationTimer?.Stop();
        if (dotTopKind != DotTopKind.Notice) return;
        dotTopKind = DotTopKind.None;
        if (inlineDotOwner is { } owner)
        {
            if (animate)
            {
                // Retain the visual owner during the outro. A new message can
                // reverse its clock; transfer/mode/lifecycle paths still use
                // the immediate detach and cannot leave a shared child behind.
                owner.HideDotTopExtension(animate: true);
                UpdatePanelPlacement(owner);
            }
            else DetachInlineDotOwner(owner);
        }
    }

    // The desired base rectangle is stored separately. Temporary top-edge movement
    // never becomes a user position and repeated reconciliation cannot climb upward.
    internal static ScreenRect CalculateTopExtensionBounds(ScreenRect work, ScreenRect baseBounds, int extensionPixels,
        int trailingReservePixels = 0) => CalculateExtensionBounds(work, baseBounds, extensionPixels, 0, trailingReservePixels);

    // The middle body is independent of both animated ends. Clamp its position
    // against the current extents, not the targets: each clock tick therefore
    // keeps it fixed in free space and moves it continuously at screen edges.
    internal static ScreenRect CalculateExtensionBounds(ScreenRect work, ScreenRect coreBounds, int topPixels,
        int footerPixels, int trailingReservePixels = 0)
    {
        if (work.Width <= 0 || work.Height <= 0 || coreBounds.Width <= 0 || coreBounds.Height <= 0
            || topPixels < 0 || footerPixels < 0 || trailingReservePixels < 0 || coreBounds.Width > work.Width
            || (long)coreBounds.Height + topPixels + footerPixels + trailingReservePixels > work.Height)
            throw new ArgumentOutOfRangeException(nameof(topPixels));
        var left = (int)Math.Clamp((long)coreBounds.Left, work.Left, (long)work.Right - coreBounds.Width);
        var coreTop = (int)Math.Clamp((long)coreBounds.Top, (long)work.Top + topPixels,
            (long)work.Bottom - coreBounds.Height - footerPixels - trailingReservePixels);
        return new(left, coreTop - topPixels, left + coreBounds.Width,
            checked(coreTop + coreBounds.Height + footerPixels));
    }

    private void DisposeInlineDot()
    {
        CancelDisplayedNoticeRead();
        inlineDotMode = false; dotNotificationWanted = false; dotNotificationTimer?.Stop(); dotNotificationDeadline = null;
        var wasUpdating = dotPresentationUpdating;
        dotPresentationUpdating = true;
        try
        {
            if (inlineDotOwner is { } owner) owner.HideDotTopExtension();
            inlineDotOwner = null; inlineDotView?.Hidden(); dotTopKind = DotTopKind.None;
        }
        finally { dotPresentationUpdating = wasUpdating; }
    }

    internal object InlineDotProofState() => new
    {
        enabled = inlineDotMode && dotPresentationEnabled, inlineWanted = inlineDotWanted,
        inlineVisible = inlineDotOwner?.DotInlineVisible == true, ownerId = inlineDotOwner?.MonitorId,
        notificationVisible = dotTopKind == DotTopKind.Notice && inlineDotOwner?.DotTopExtensionVisible == true,
        notificationWanted = dotNotificationWanted, notificationBounds = (ScreenRect?)null,
        timeoutArmed = dotNotificationDeadline is not null, elapsedMilliseconds = dotNotificationClock.Elapsed.TotalMilliseconds,
        unreadCount = inlineDotState?.UnreadCount ?? 0, hasDraft = inlineDotView?.HasLocalDraft == true,
        activeMarkers = windows.Values.Count(owner => owner.DotMarkerVisible), topKind = dotTopKind.ToString(),
        topHeightDip = inlineDotOwner?.DotTopExtensionHeight ?? 0, replyAllowed = DotReplyAllowed,
        topTargetHeightDip = inlineDotOwner?.DotTopExtensionTargetHeight ?? 0,
        footerHeightDip = inlineDotOwner?.ResetScheduleHeightForLayout ?? 0,
        footerTargetHeightDip = inlineDotOwner?.ResetScheduleTargetHeightForLayout ?? 0,
        topologyCaptures = placementTopologyCaptureCount,
        topContentType = inlineDotOwner?.DotTopContent?.GetType().Name,
    };
}
