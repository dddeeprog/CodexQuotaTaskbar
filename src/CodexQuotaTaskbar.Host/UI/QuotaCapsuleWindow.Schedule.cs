using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using CodexQuotaTaskbar.Host.Resets;
using ToolTip = System.Windows.Controls.ToolTip;

namespace CodexQuotaTaskbar.Host.UI;

public partial class QuotaCapsuleWindow
{
    internal const double ResetScheduleRowHeight = 26;
    private ResetScheduleSnapshot resetSchedule = ResetScheduleSnapshot.Loading;
    private ResetScheduleRowPresentation? resetScheduleView;
    private bool resetSchedulePresentationAllowed = true;
    private DateTimeOffset? confirmedResetUntil;
    private QuotaResetTracker? recoveryTracker;
    private ResetQueryState resetQueryState = ResetQueryState.Idle;
    internal void ApplyResetQueryState(ResetQueryState state)
    {
        if (state.Revision < resetQueryState.Revision) return;
        resetQueryState = state;
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
        if (view == resetScheduleView) { RefreshCompanionResetNotice(now); return; }
        if (resetScheduleView?.Countdown != view.Countdown) ResetScheduleCountdown.Text = view.Countdown;
        if (resetScheduleView?.LocalTime != view.LocalTime) ResetScheduleLocalTime.Text = view.LocalTime;
        if (resetScheduleView?.Tooltip != view.Tooltip)
            ResetScheduleRow.ToolTip = view.IsVisible ? CreateResetScheduleTooltip(view.Tooltip) : null;
        AutomationProperties.SetName(ResetScheduleRow, view.IsVisible ? view.Countdown + view.LocalTime : string.Empty);
        resetScheduleView = view;
        // Invalid/cancelled/expired data stops being presented immediately. The
        // existing outer row may finish a short empty-shell exit transition.
        ResetScheduleRow.IsHitTestVisible = view.IsVisible;
        if (!view.IsVisible)
        {
            ResetScheduleCountdown.Text = string.Empty;
            ResetScheduleLocalTime.Text = string.Empty;
            ResetScheduleRow.ToolTip = null;
        }
        else if (resetSchedulePresentationAllowed) ResetScheduleRow.Visibility = Visibility.Visible;
        RetargetResetExtension(view.IsVisible && resetSchedulePresentationAllowed ? ResetScheduleRowHeight : 0);
        RefreshCompanionResetNotice(now);
    }

    // An optional footer is never clipped into a half-readable line. A short
    // working area may suppress only its presentation while keeping valid data.
    internal void SetResetSchedulePresentationAllowed(bool allowed)
    {
        VerifyAccess();
        if (resetSchedulePresentationAllowed == allowed) return;
        resetSchedulePresentationAllowed = allowed;
        var visible = allowed && resetScheduleView?.IsVisible == true;
        if (visible) ResetScheduleRow.Visibility = Visibility.Visible;
        RetargetResetExtension(visible ? ResetScheduleRowHeight : 0, animate: allowed);
        if (!visible && ResetScheduleHeight <= .001) ResetScheduleRow.Visibility = Visibility.Collapsed;
        RefreshCompanionResetNotice();
    }

    private ToolTip CreateResetScheduleTooltip(string text)
    {
        var content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 280, FontFamily = OverlayTypography.Text };
        content.Inlines.Add(new LineBreak());
        var source = new Hyperlink(new Run("Codex Resets")) { NavigateUri = CodexResetFeedClient.SiteUri };
        source.Click += (_, _) => OpenResetSourceRequested?.Invoke(CodexResetFeedClient.SiteUri);
        content.Inlines.Add(source);
        return new ToolTip { Content = content, FontFamily = OverlayTypography.Text, Focusable = false, StaysOpen = true };
    }

    private void ApplyResetScheduleAppearance(bool highContrast)
    {
        ResetScheduleCountdown.Foreground = ResetScheduleLocalTime.Foreground = highContrast
            ? System.Windows.SystemColors.WindowTextBrush : normalLabelForeground;
    }
}
