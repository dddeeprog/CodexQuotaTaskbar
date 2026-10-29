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
    internal const double ResetScheduleRowHeight = 26;
    private ResetScheduleSnapshot resetSchedule = ResetScheduleSnapshot.Loading;
    private ResetScheduleRowPresentation? resetScheduleView;
    private bool resetStatusHovered;
    private System.Windows.Point? resetStatusPointer;
    private ToolTip? resetScheduleTooltip;
    private string? resetScheduleTooltipText;
    internal ResetFooterKind ResetFooter { get; private set; }
    private bool resetSchedulePresentationAllowed = true;
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
    internal bool ResetNoticeActive => !bongoWindowClosing && !extensionMotionClosed && ShouldUseResetNoticeColor(
        resetScheduleView is { IsVisible: true, IsTimingKnown: true }, resetSchedulePresentationAllowed,
        ResetScheduleRow.Visibility == Visibility.Visible, appliedResetExtensionHeight, IsVisible);

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
        var status = resetStatusHovered && IsUnifiedPanel && CompanionReady && !DotTopExtensionVisible
            && !HasPointerGesture && !bongoWindowClosing && !extensionMotionClosed && resetSchedulePresentationAllowed
            && state is ResetVisualState.Confirmed or ResetVisualState.Failed;
        var kind = status ? ResetFooterKind.Status
            : resetScheduleView?.IsVisible == true && state == ResetVisualState.Upcoming ? ResetFooterKind.Schedule : ResetFooterKind.None;
        var text = kind == ResetFooterKind.Schedule ? resetScheduleView!.Countdown
            : kind == ResetFooterKind.Status ? state == ResetVisualState.Confirmed ? "额度已回升" : "重置预告查询失败" : string.Empty;
        var detail = kind == ResetFooterKind.Schedule ? resetScheduleView!.LocalTime
            : kind == ResetFooterKind.Status ? state == ResetVisualState.Confirmed ? " · 提示保留 1 小时" : " · 暂无法确认" : string.Empty;
        if (ResetScheduleCountdown.Text != text) ResetScheduleCountdown.Text = text;
        if (ResetScheduleLocalTime.Text != detail) ResetScheduleLocalTime.Text = detail;
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
        ResetFooter = kind;
        AutomationProperties.SetName(ResetScheduleRow, text + detail);
        // A status description is not a link, including a stale Schedule mouse-up.
        ResetScheduleRow.IsHitTestVisible = kind == ResetFooterKind.Schedule && resetSchedulePresentationAllowed;
        var visible = kind != ResetFooterKind.None && resetSchedulePresentationAllowed;
        if (visible) ResetScheduleRow.Visibility = Visibility.Visible;
        RetargetResetExtension(visible ? ResetScheduleRowHeight : 0, animate);
        if (!visible && ResetScheduleHeight <= .001) ResetScheduleRow.Visibility = Visibility.Collapsed;
    }

    internal void InvokeResetFooter()
    {
        if (ResetFooter == ResetFooterKind.Schedule && resetSchedulePresentationAllowed && resetScheduleView?.IsVisible == true)
            OpenResetSourceRequested?.Invoke(CodexResetFeedClient.SiteUri);
    }

    // An optional footer is never clipped into a half-readable line. A short
    // working area may suppress only its presentation while keeping valid data.
    internal void SetResetSchedulePresentationAllowed(bool allowed)
    {
        VerifyAccess();
        if (resetSchedulePresentationAllowed == allowed) return;
        resetSchedulePresentationAllowed = allowed;
        ReconcileResetStatusPointer();
        if (!allowed) RefreshResetFooter(animate: false);
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
    }
}
