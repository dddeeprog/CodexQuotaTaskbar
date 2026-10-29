using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using CodexQuotaTaskbar.Host.Resets;
using ToolTip = System.Windows.Controls.ToolTip;

namespace CodexQuotaTaskbar.Host.UI;

internal enum ResetFooterKind { None, Schedule, Status }

public partial class QuotaCapsuleWindow
{
    internal const double ResetScheduleRowHeight = 42;
    internal const double ResetStatusRowHeight = 26;
    internal const double CompactResetExtraHeight = 4;
    internal double AppliedCompactResetExtraHeight => CompactResetNotice.Visibility == Visibility.Visible
        && ResetFooter == ResetFooterKind.Schedule ? CompactResetExtraHeight : 0;
    internal double RequiredResetFooterHeight => ResetFooter == ResetFooterKind.Schedule ? ResetScheduleRowHeight
        : ResetFooter == ResetFooterKind.Status ? ResetStatusRowHeight : 0;
    private ResetScheduleSnapshot resetSchedule = ResetScheduleSnapshot.Loading;
    private ResetScheduleRowPresentation? resetScheduleView;
    private bool resetStatusHovered;
    private System.Windows.Point? resetStatusPointer;
    private ToolTip? resetScheduleTooltip;
    private string? resetScheduleTooltipText;
    internal ResetFooterKind ResetFooter { get; private set; }
    private bool resetSchedulePresentationAllowed = true;
    private bool compactResetPresentationAllowed = true;
    private double resetFooterAvailableHeight = double.PositiveInfinity;
    private DateTimeOffset? confirmedResetUntil;
    private QuotaResetTracker? recoveryTracker;
    private ResetQueryState resetQueryState = ResetQueryState.Idle;
    internal void ApplyResetQueryState(ResetQueryState state)
    {
        if (state.Revision < resetQueryState.Revision) return;
        resetQueryState = state;
        RefreshResetFooter();
        RefreshCompanionResetNotice();
    }
    internal void SetResetTracker(QuotaResetTracker tracker) => recoveryTracker = tracker;
    private double ResetScheduleHeight => appliedResetExtensionHeight;
    internal bool ResetNoticeActive => !bongoWindowClosing && !extensionMotionClosed && ResetFooter == ResetFooterKind.Schedule && (ShouldUseResetNoticeColor(
        resetScheduleView is { IsVisible: true, IsTimingKnown: true }, resetSchedulePresentationAllowed,
        ResetScheduleRow.Visibility == Visibility.Visible, appliedResetExtensionHeight, IsVisible)
        || resetScheduleView is { IsVisible: true, IsTimingKnown: true } && CompactResetNotice.Visibility == Visibility.Visible && IsVisible);

    internal static bool ShouldUseResetNoticeColor(bool scheduleVisible, bool presentationAllowed,
        bool rowVisible, double appliedHeight, bool ownerVisible) => scheduleVisible && presentationAllowed
        && rowVisible && double.IsFinite(appliedHeight) && appliedHeight > .001 && ownerVisible;

    internal void ApplyConfirmedReset(DateTimeOffset? until)
    {
        confirmedResetUntil = until;
        RefreshResetSchedule(DateTimeOffset.UtcNow);
    }

    private void RefreshCompanionResetNotice(DateTimeOffset? now = null) => companion?.SetResetState(
        !bongoWindowClosing && !extensionMotionClosed && IsVisible
            ? resetQueryState.Select(ResetNoticeActive, confirmedResetUntil, now ?? DateTimeOffset.UtcNow)
            : ResetVisualState.None);

    internal void ApplyResetSchedule(ResetScheduleSnapshot snapshot) => ApplyResetSchedule(snapshot, DateTimeOffset.UtcNow);

    internal void ApplyResetSchedule(ResetScheduleSnapshot snapshot, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        resetSchedule = snapshot;
        RefreshResetSchedule(now, zone);
    }

    internal void RefreshResetSchedule(DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        var projected = recoveryTracker?.ProjectSchedule(resetSchedule, now) ?? resetSchedule;
        var view = ResetScheduleRowPresentation.Create(projected, now, zone);
        if (recoveryTracker?.UsesLatchedSchedule != true && recoveryTracker?.IsNoticeConsumed(resetSchedule.Schedule) == true
            || QuotaResetTracker.Select(false, confirmedResetUntil, now) == ResetVisualState.Confirmed)
            view = view with { IsVisible = false };
        if (!IsUnifiedPanel) view = view with { IsVisible = false };
        // Reuse the coordinator's existing 750 ms reconciliation, even when minute text is unchanged.
        if (view != resetScheduleView) resetScheduleView = view;
        ReconcileResetStatusPointer(now);
        RefreshCompanionResetNotice(now);
    }

    internal void SetResetStatusHintHovered(bool hovered, DateTimeOffset? now = null)
    {
        if (resetStatusHovered == hovered) return;
        resetStatusHovered = hovered;
        RefreshResetFooter(now);
    }

    // Raw pointer intent is independent of temporary Dot/gesture/space suppression.
    // The non-clickable footer is deliberately not part of InputBounds().
    internal static bool IsResetStatusHoverPoint(System.Windows.Point point, Rect ball, Rect card, bool retain)
    {
        if (ball.IsEmpty) return false;
        if (ball.Contains(point)) return true;
        if (!retain || card.IsEmpty) return false;
        var left = Math.Min(ball.Left, card.Left);
        var right = Math.Max(ball.Right, card.Right);
        var bottom = Math.Max(ball.Bottom, card.Bottom);
        return new Rect(left, ball.Top, right - left, bottom - ball.Top).Contains(point);
    }

    internal void UpdateResetStatusPointer(System.Windows.Point point)
    {
        resetStatusPointer = point;
        var card = GlassBorder.TransformToAncestor(Shell).TransformBounds(new Rect(GlassBorder.RenderSize));
        SetResetStatusHintHovered(IsResetStatusHoverPoint(point, StatusBallBoundsDip, card, resetStatusHovered));
    }

    private void ClearResetStatusPointer()
    {
        resetStatusPointer = null;
        SetResetStatusHintHovered(false);
    }

    private void ReconcileResetStatusPointer(DateTimeOffset? now = null, bool? fallback = null)
    {
        // Existing input events and the existing 750 ms reconciliation are enough.
        // An unshown synthetic test window never reads the physical mouse.
        if (IsVisible && PresentationSource.FromVisual(this) is not null)
            UpdateResetStatusPointer(System.Windows.Input.Mouse.GetPosition(Shell));
        else if (resetStatusPointer is { } point) UpdateResetStatusPointer(point);
        else if (fallback is { } hovered) resetStatusHovered = hovered;
        RefreshResetFooter(now);
    }

    private void RefreshResetFooter(DateTimeOffset? now = null, bool animate = true)
    {
        var state = resetQueryState.Select(resetScheduleView?.IsVisible == true, confirmedResetUntil, now ?? DateTimeOffset.UtcNow);
        var active = IsUnifiedPanel && !bongoWindowClosing && !extensionMotionClosed;
        var status = active
            && state is ResetVisualState.Confirmed or ResetVisualState.Failed;
        var kind = status ? ResetFooterKind.Status
            : active && resetScheduleView?.IsVisible == true && state == ResetVisualState.Upcoming ? ResetFooterKind.Schedule : ResetFooterKind.None;
        var text = kind == ResetFooterKind.Schedule ? resetScheduleView!.Countdown
            : kind == ResetFooterKind.Status ? state == ResetVisualState.Confirmed ? "额度已回升" : "重置预告查询失败" : string.Empty;
        var detail = kind == ResetFooterKind.Schedule ? resetScheduleView!.LocalTime : string.Empty;
        if (ResetScheduleCountdown.Text != text) ResetScheduleCountdown.Text = text;
        if (ResetScheduleLocalTime.Text != detail) ResetScheduleLocalTime.Text = detail;
        if (CompactResetCountdown.Text != text) CompactResetCountdown.Text = text;
        if (CompactResetLocalTime.Text != detail) CompactResetLocalTime.Text = detail;
        var wasCompact = CompactResetNotice.Visibility == Visibility.Visible;
        var previousCompactExtra = AppliedCompactResetExtraHeight;
        ResetFooter = kind;
        resetSchedulePresentationAllowed = resetFooterAvailableHeight >= RequiredResetFooterHeight;
        compactResetPresentationAllowed = resetFooterAvailableHeight >= (kind == ResetFooterKind.Schedule ? CompactResetExtraHeight : 0);
        var twoLines = kind == ResetFooterKind.Schedule;
        ResetScheduleRow.Height = RequiredResetFooterHeight;
        ((Grid)ResetScheduleLocalTime.Parent).RowDefinitions[1].Height = new GridLength(twoLines ? 16 : 0);
        ResetScheduleLocalTime.Visibility = twoLines ? Visibility.Visible : Visibility.Collapsed;
        CompactResetLocalTime.Visibility = twoLines ? Visibility.Visible : Visibility.Collapsed;
        CompactResetNotice.Height = twoLines ? 28 : 14;
        var compact = kind != ResetFooterKind.None && !resetSchedulePresentationAllowed && compactResetPresentationAllowed;
        CompactResetNotice.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        CompactResetNotice.IsHitTestVisible = compact && kind == ResetFooterKind.Schedule;
        var accessibleText = string.IsNullOrEmpty(detail) ? text : text + " · " + detail;
        AutomationProperties.SetName(CompactResetNotice, "空间受限 · " + accessibleText);
        if (IsUnifiedPanel)
        {
            QuotaHeader.MaxHeight = compact ? 24 : double.PositiveInfinity;
            QuotaHeader.VerticalAlignment = compact ? VerticalAlignment.Top : VerticalAlignment.Stretch;
            SessionToggle.Height = compact ? 24 : 32;
            ((Grid)QuotaHeader.Parent).RowDefinitions[0].Height = new GridLength(48 + AppliedCompactResetExtraHeight);
        }
        if (kind == ResetFooterKind.Schedule)
        {
            if (resetScheduleTooltip is null || resetScheduleTooltipText != resetScheduleView!.Tooltip)
            {
                resetScheduleTooltipText = resetScheduleView!.Tooltip;
                resetScheduleTooltip = CreateResetScheduleTooltip(resetScheduleTooltipText);
            }
            ResetScheduleRow.ToolTip = resetScheduleTooltip;
        }
        else
        {
            if (resetScheduleTooltip is not null) resetScheduleTooltip.IsOpen = false;
            ResetScheduleRow.ToolTip = null;
        }

        AutomationProperties.SetName(ResetScheduleRow, accessibleText);
        // A status description is not a link, including a stale Schedule mouse-up.
        ResetScheduleRow.IsHitTestVisible = kind == ResetFooterKind.Schedule && resetSchedulePresentationAllowed;
        var visible = kind != ResetFooterKind.None && resetSchedulePresentationAllowed;
        if (visible) ResetScheduleRow.Visibility = Visibility.Visible;
        var compactLayoutChanged = wasCompact != compact || previousCompactExtra != AppliedCompactResetExtraHeight;
        RetargetResetExtension(visible ? RequiredResetFooterHeight : 0, animate && !compactLayoutChanged);
        if (!visible && ResetScheduleHeight <= .001) ResetScheduleRow.Visibility = Visibility.Collapsed;
        if (compactLayoutChanged)
        {
            RefreshBongoBodyLayout();
            RefreshStandaloneWindowHeight();
            PresentationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void InvokeResetFooter()
    {
        if (ResetFooter == ResetFooterKind.Schedule && resetScheduleView?.IsVisible == true
            && (resetSchedulePresentationAllowed && ResetScheduleRow.Visibility == Visibility.Visible
                || compactResetPresentationAllowed && CompactResetNotice.Visibility == Visibility.Visible))
            OpenResetSourceRequested?.Invoke(CodexResetFeedClient.SiteUri);
    }

    // Status fits one line in the original header. A dated schedule needs four extra DIPs.
    internal void SetResetSchedulePresentationAllowed(bool allowed, bool compactAllowed = true) =>
        SetResetScheduleAvailableHeight(allowed ? double.PositiveInfinity : compactAllowed ? CompactResetExtraHeight : double.NegativeInfinity);

    internal void SetResetScheduleAvailableHeight(double available)
    {
        VerifyAccess();
        if (resetFooterAvailableHeight == available) return;
        resetFooterAvailableHeight = available;
        RefreshResetFooter(animate: false);
        RefreshCompanionResetNotice();
    }

    private ToolTip CreateResetScheduleTooltip(string text)
    {
        var content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 280, FontFamily = OverlayTypography.Text };
        content.Inlines.Add(new LineBreak());
        var source = new Hyperlink(new Run("Codex Resets")) { NavigateUri = CodexResetFeedClient.SiteUri };
        source.Click += (_, _) => InvokeResetFooter();
        content.Inlines.Add(source);
        return new ToolTip { Content = content, FontFamily = OverlayTypography.Text, Focusable = false, StaysOpen = true };
    }

    private void ApplyResetScheduleAppearance(bool highContrast)
    {
        ResetScheduleCountdown.Foreground = ResetScheduleLocalTime.Foreground = highContrast
            ? System.Windows.SystemColors.WindowTextBrush : normalLabelForeground;
        CompactResetCountdown.Foreground = CompactResetLocalTime.Foreground = ResetScheduleCountdown.Foreground;
    }
}
