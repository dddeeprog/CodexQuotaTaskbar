using System.Windows;
using System.Windows.Controls;

namespace CodexQuotaTaskbar.Host.UI;

public partial class QuotaCapsuleWindow
{
    private CompanionBallControl? companion;
    private bool companionPointerDown;
    private bool CompanionReady => companion?.Ready == true;
    internal event EventHandler? CompanionInvoked;
    internal System.Windows.Point CompanionAnchor
    {
        get
        {
            if (companion is { IsVisible: true }) return companion.PointToScreen(new System.Windows.Point(companion.ActualWidth / 2, 0));
            return new System.Windows.Point(AlignmentBounds.Right - 20, PhysicalBounds.Top);
        }
    }

    internal void SetCompanion(bool enabled, bool reduced)
    {
        if (!enabled)
        {
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
            };
            System.Windows.Controls.Panel.SetZIndex(companion, 5);
            companion.AvailabilityChanged += (_, _) =>
            {
                companion.IsHitTestVisible = CompanionReady;
                if (CompanionReady)
                {
                    SessionBadge.Visibility = Visibility.Collapsed;
                    if (IsUnifiedPanel) QuotaHeader.Margin = new Thickness(14, 0, 46, 0);
                }
                else RestoreStatusBadge();
            };
            companion.MouseEnter += (_, _) => companion?.Hover();
            companion.Apply(sessionsSnapshot);
            Shell.Children.Add(companion);
        }
        companion.SetReducedMotion(reduced);
    }

    private void RestoreStatusBadge()
    {
        if (IsUnifiedPanel) QuotaHeader.Margin = new Thickness(14, 0, 10, 0);
        SessionBadge.Visibility = waitingCount + runningCount + completedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void ToggleCompanionTour() => companion?.ToggleTour();
}
