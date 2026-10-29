using System.Windows;
using System.Windows.Controls;

namespace CodexQuotaTaskbar.Host.UI;

internal enum CompanionStatusDock { QuotaCorner, StageCorner }

public partial class QuotaCapsuleWindow
{
    private static readonly System.Windows.Media.Transform CoexistingStatusBallScale = MakeCoexistingStatusBallScale();
    internal const double StatusBallLiftDip = 2;
    private static readonly System.Windows.Media.Transform StatusBallLift = MakeStatusBallLift();
    private CompanionBallControl? companion;
    private bool companionTyping;
    private bool companionReduced;
    private bool companionObservePointer = true;
    private CompanionStatusDock companionStatusDock;
    internal bool StatusBallVisibleForLayout => CompanionReady && companion?.Visibility == Visibility.Visible;
    internal double StatusBallTopOffsetForLayout => DotTopExtensionHeight + (BongoHasFrame
        ? GlassBorder.Margin.Top + GlassBorder.BorderThickness.Top + (companionStatusDock == CompanionStatusDock.QuotaCorner
            ? BongoStageHeight + 1 : BongoIslandSurface.ModelTopInsetDip) : 0);
    internal Rect StatusBallBoundsDip => StatusBallVisibleForLayout
        && Shell.IsAncestorOf(companion!)
        ? companion!.TransformToAncestor(Shell).TransformBounds(new Rect(0, 0, companion.Width, companion.Height)) : Rect.Empty;
    internal void SetCompanionStatusDock(CompanionStatusDock dock)
    {
        if (!Enum.IsDefined(dock)) throw new ArgumentOutOfRangeException(nameof(dock));
        companionStatusDock = dock;
        UpdateCompanionSpacing();
    }
    internal void SetPointerObservationAllowed(bool allowed)
    {
        companionObservePointer = allowed; companion?.SetPointerObservationAllowed(allowed);
    }
    private bool CompanionReady => companion?.Ready == true;
    internal bool CanAnimateTyping => !CaptureOccluded && IsVisible && companion?.CanAnimateTyping == true;
    internal event EventHandler? CompanionInvoked;
    internal event Action<System.Windows.Point>? CompanionGazeChanged;
    internal void SetExternalCompanion(bool active)
    {
        if (companion is null) return;
        // The model no longer takes over the Codex status ball. Its own keyboard
        // is the typing presentation while both characters share the card.
        companion.SetExternalAppearance(false);
        companion.SetTypingInteraction(companionTyping && !active);
        companion.IsHitTestVisible = CompanionReady;
    }
    internal void SetCompanionFailure(string? reason) => companion?.SetExternalFailure(reason);
    internal System.Windows.Point CompanionAnchor
    {
        get
        {
            if (BongoHasFrame) return new System.Windows.Point(
                BongoModelBounds.Left + BongoModelBounds.Width / 2, physicalBounds.Top);
            if (companion is { IsVisible: true }) return companion.PointToScreen(new System.Windows.Point(companion.ActualWidth / 2, 0));
            return new System.Windows.Point(AlignmentBounds.Right - 20, PhysicalBounds.Top);
        }
    }

    internal void SetCompanion(bool enabled, bool reduced)
    {
        companionReduced = reduced;
        if (reduced) FinishExtensionTransitions();
        if (!enabled)
        {
            ClearResetStatusPointer();
            if (companion is not null)
            {
                Shell.Children.Remove(companion);
                companion.Dispose();
                companion = null;
            }
            RestoreStatusBadge();
            return;
        }
        if (companion is null)
        {
            companion = new CompanionBallControl
            {
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 0, 0),
                IsHitTestVisible = false,
                RenderTransform = StatusBallLift,
            };
            System.Windows.Controls.Panel.SetZIndex(companion, 5);
            companion.AvailabilityChanged += (_, _) =>
            {
                companion.IsHitTestVisible = CompanionReady;
                if (CompanionReady)
                {
                    SessionBadge.Visibility = Visibility.Collapsed;
                    UpdateCompanionSpacing();
                }
                else RestoreStatusBadge();
            };
            companion.MouseEnter += (_, _) => companion?.Hover();
            companion.ResetHintHoverChanged += hovered => ReconcileResetStatusPointer(fallback: hovered);
            companion.Unloaded += (_, _) => ClearResetStatusPointer();
            companion.GazeChanged += point => CompanionGazeChanged?.Invoke(point);
            companion.Apply(sessionsSnapshot);
            Shell.Children.Add(companion);
        }
        companion.SetReducedMotion(reduced);
        companion.SetPointerObservationAllowed(companionObservePointer);
        companion.SetExternalAppearance(false);
        companion.SetTypingInteraction(companionTyping && !BongoHasFrame);
        UpdateCompanionSpacing();
        RefreshCompanionResetNotice();
    }

    private void RestoreStatusBadge()
    {
        UpdateCompanionSpacing();
        SessionBadge.Visibility = !CompanionReady && waitingCount + runningCount + completedCount > 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void ToggleCompanionTour() => companion?.ToggleTour();
    internal void SetCompanionTyping(bool enabled)
    {
        companionTyping = enabled;
        companion?.SetTypingInteraction(enabled && !BongoHasFrame);
        UpdateCompanionSpacing();
    }
    internal void KeyboardActivity(Platform.CompanionInputEvent value) => companion?.KeyboardActivity(value);

    private void UpdateCompanionSpacing()
    {
        if (companion is not null)
        {
            // Scale the complete visual and its process badge, rather than clip
            // a 56-DIP drawing. This leaves the first real task hit area clear.
            var scale = BongoHasFrame ? CoexistingStatusBallScale : System.Windows.Media.Transform.Identity;
            if (!ReferenceEquals(companion.LayoutTransform, scale)) companion.LayoutTransform = scale;
            companion.Margin = new Thickness(0, StatusBallTopOffsetForLayout, 0, 0);
        }
        var reservesQuotaCorner = CompanionReady && (!BongoHasFrame || companionStatusDock == CompanionStatusDock.QuotaCorner);
        var margin = CompanionQuotaMargin(IsUnifiedPanel, reservesQuotaCorner, companionTyping && !BongoHasFrame,
            companionReduced, SystemParameters.ClientAreaAnimation);
        if (BongoHasFrame && reservesQuotaCorner && !IsUnifiedPanel)
            margin = new Thickness(margin.Left, margin.Top, 46, margin.Bottom);
        if (QuotaHeader.Margin != margin) QuotaHeader.Margin = margin;
        // Readiness/appearance can change after the unread marker was created.
        // Re-anchor it immediately, without waiting for another Dot message.
        RefreshDotMarkerPlacement();
    }

    private static System.Windows.Media.Transform MakeCoexistingStatusBallScale()
    {
        var scale = new System.Windows.Media.ScaleTransform(52d / 56d, 52d / 56d);
        scale.Freeze();
        return scale;
    }

    private static System.Windows.Media.Transform MakeStatusBallLift()
    {
        var transform = new System.Windows.Media.TranslateTransform(0, -StatusBallLiftDip);
        transform.Freeze();
        return transform;
    }

    internal static Thickness CompanionQuotaMargin(bool unified, bool ballReady, bool typing, bool reduced, bool animations) =>
        unified ? new Thickness(14, 0, ballReady ? 46 : 10, 0)
            : new Thickness(8, 5, ballReady && typing && !reduced && animations ? 32 : 14, 5);
}
