using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;

namespace CodexQuotaTaskbar.Host.UI;

public partial class TrayMenuWindow
{
    public static readonly DependencyProperty ScrollbarVisualOpacityProperty = DependencyProperty.Register(
        nameof(ScrollbarVisualOpacity), typeof(double), typeof(TrayMenuWindow), new PropertyMetadata(0d));
    private readonly DispatcherTimer scrollbarIdleTimer = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(800) };
    private ScrollBar? menuVerticalScrollBar;
    private bool scrollbarHovered;
    private bool scrollbarDragging;
    private double scrollHeightBudget = MenuPageHeightLimitDip;

    public double ScrollbarVisualOpacity => (double)GetValue(ScrollbarVisualOpacityProperty);
    internal bool IsScrollbarIdleTimerRunning => scrollbarIdleTimer.IsEnabled;
    internal bool IsScrollbarTransitionActive { get; private set; }
    internal double MinimumUsablePageHeightDip => HeaderHeightDip() + 21 + 12 + 32;

    private void InitializeMenuScrolling()
    {
        scrollbarIdleTimer.Tick += OnScrollbarIdle;
        MenuScroller.ScrollChanged += OnMenuScrollChanged;
        MenuScroller.ApplyTemplate();
        menuVerticalScrollBar = MenuScroller.Template.FindName("PART_VerticalScrollBar", MenuScroller) as ScrollBar;
        if (menuVerticalScrollBar is not null)
        {
            menuVerticalScrollBar.MouseEnter += OnScrollbarMouseEnter;
            menuVerticalScrollBar.MouseLeave += OnScrollbarMouseLeave;
            menuVerticalScrollBar.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnScrollbarDragStarted));
            menuVerticalScrollBar.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnScrollbarDragCompleted));
        }
    }

    internal bool TrySetPageHeightLimit(double maximumHeightDip)
    {
        if (!double.IsFinite(maximumHeightDip) || maximumHeightDip < MinimumUsablePageHeightDip)
        {
            if (IsVisible) Hide();
            PlacementUnavailable = true;
            return false;
        }
        scrollHeightBudget = Math.Min(MenuPageHeightLimitDip, maximumHeightDip);
        MaxHeight = scrollHeightBudget;
        PlacementUnavailable = false;
        ApplyScrollHeightLimit();
        return true;
    }

    private double HeaderHeightDip()
    {
        MenuHeader.Measure(new System.Windows.Size(251, double.PositiveInfinity));
        return MenuHeader.DesiredSize.Height;
    }

    // Fractional-DPI borders can round the content outward by one physical pixel.
    // Keep that pixel inside the fixed work-area budget rather than enlarging the menu.
    private void ApplyScrollHeightLimit() =>
        MenuScroller.MaxHeight = Math.Max(32 + 12, Math.Min(MenuPageHeightLimitDip, scrollHeightBudget) - 22 - HeaderHeightDip());

    private void OnMenuScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0 && MenuScroller.ScrollableHeight > 0) RevealMenuScrollbar();
    }

    private void OnScrollbarMouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => SetScrollbarHover(true);
    private void OnScrollbarMouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => SetScrollbarHover(false);
    private void OnScrollbarDragStarted(object sender, DragStartedEventArgs e)
    {
        scrollbarDragging = true;
        RevealMenuScrollbar();
    }
    private void OnScrollbarDragCompleted(object sender, DragCompletedEventArgs e)
    {
        scrollbarDragging = false;
        FinishScrollbarInteraction();
    }

    internal void SetScrollbarHover(bool hovered)
    {
        scrollbarHovered = hovered;
        if (hovered) RevealMenuScrollbar();
        else FinishScrollbarInteraction();
    }

    internal void RevealMenuScrollbar()
    {
        if (pageTransitionClosed) return;
        scrollbarIdleTimer.Stop();
        StopScrollbarClock();
        SetValue(ScrollbarVisualOpacityProperty, 1d);
        if (IsVisible && !scrollbarHovered && !scrollbarDragging) scrollbarIdleTimer.Start();
    }

    internal void FinishScrollbarInteraction()
    {
        if (scrollbarHovered || scrollbarDragging) return;
        scrollbarIdleTimer.Stop();
        if (IsVisible && !pageTransitionClosed) scrollbarIdleTimer.Start();
        else ResetMenuScrollbar();
    }

    private void OnScrollbarIdle(object? sender, EventArgs e)
    {
        scrollbarIdleTimer.Stop();
        if (scrollbarHovered || scrollbarDragging) return;
        StopScrollbarClock();
        var from = ScrollbarVisualOpacity;
        SetValue(ScrollbarVisualOpacityProperty, 0d);
        if (!CanAnimatePage || from <= 0) return;
        IsScrollbarTransitionActive = true;
        var fade = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        fade.Completed += (_, _) => StopScrollbarClock();
        BeginAnimation(ScrollbarVisualOpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    private void StopScrollbarClock()
    {
        BeginAnimation(ScrollbarVisualOpacityProperty, null);
        IsScrollbarTransitionActive = false;
    }

    private void NormalizeScrollbarMotion()
    {
        scrollbarIdleTimer.Stop();
        StopScrollbarClock();
        SetValue(ScrollbarVisualOpacityProperty, scrollbarHovered || scrollbarDragging ? 1d : 0d);
    }

    private void ResetMenuScrollbar()
    {
        scrollbarIdleTimer.Stop();
        StopScrollbarClock();
        scrollbarHovered = scrollbarDragging = false;
        SetValue(ScrollbarVisualOpacityProperty, 0d);
    }

    private void DisposeMenuScrolling()
    {
        ResetMenuScrollbar();
        scrollbarIdleTimer.Tick -= OnScrollbarIdle;
        MenuScroller.ScrollChanged -= OnMenuScrollChanged;
        if (menuVerticalScrollBar is null) return;
        menuVerticalScrollBar.MouseEnter -= OnScrollbarMouseEnter;
        menuVerticalScrollBar.MouseLeave -= OnScrollbarMouseLeave;
        menuVerticalScrollBar.RemoveHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnScrollbarDragStarted));
        menuVerticalScrollBar.RemoveHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnScrollbarDragCompleted));
    }
}
