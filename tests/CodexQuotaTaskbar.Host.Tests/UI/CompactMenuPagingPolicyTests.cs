using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.UI;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using Size = System.Windows.Size;

namespace CodexQuotaTaskbar.Host.Tests.UI;

// Historical filename retained; manual pagination has been removed from this
// candidate. These checks use only hidden controls and synthetic preferences.
public sealed class CompactMenuPagingPolicyTests
{
    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void Thin_visual_thumb_has_a_wider_transparent_hit_region_without_narrowing_menu_controls(int dpi) => Sta(menu =>
    {
        VisualTreeHelper.SetRootDpi((FrameworkElement)menu.Content, new DpiScale(dpi / 96d, dpi / 96d));
        Assert.True(menu.TrySetPageHeightLimit(200));
        menu.OpenSection(MenuSection.Appearance);
        Arrange(menu);
        var viewer = Named<ScrollViewer>(menu, "MenuScroller");
        Assert.True(viewer.ScrollableHeight > 0);
        var bar = VerticalBar(viewer);
        var track = Assert.IsType<Track>(bar.Template.FindName("PART_Track", bar));
        var thumb = Assert.IsType<Thumb>(track.Thumb);
        thumb.ApplyTemplate();
        var visual = Assert.IsType<Border>(thumb.Template.FindName("ScrollbarThumbVisual", thumb));
        var geometry = $"targetDpi={dpi}; contentDpi={VisualTreeHelper.GetDpi((FrameworkElement)menu.Content).DpiScaleX}; barWidth={bar.Width}; barActual={bar.ActualWidth}; barDpi={VisualTreeHelper.GetDpi(bar).DpiScaleX}; thumbWidth={thumb.Width}; thumbActual={thumb.ActualWidth}; thumbDpi={VisualTreeHelper.GetDpi(thumb).DpiScaleX}; trackWidth={track.ActualWidth}; trackDpi={VisualTreeHelper.GetDpi(track).DpiScaleX}; barRounding={bar.UseLayoutRounding}; thumbRounding={thumb.UseLayoutRounding}";
        try
        {
            Assert.InRange(bar.ActualWidth, 11.99, 12.01);
            Assert.InRange(thumb.ActualWidth, 11.99, 12.01);
        }
        catch (Xunit.Sdk.XunitException error)
        {
            throw new Xunit.Sdk.XunitException(geometry + "; " + error.Message);
        }
        Assert.True(thumb.ActualHeight >= 24);
        Assert.Equal(3, visual.Width);
        Assert.InRange(Math.Abs((visual.ActualWidth - 3) * dpi / 96d), 0, 1);
        Assert.Equal(new CornerRadius(1.5), visual.CornerRadius);
        var grid = Assert.IsType<Grid>(track.Parent);
        var background = Assert.IsType<SolidColorBrush>(grid.Background);
        Assert.Equal(0, background.Color.A);
        Assert.True(grid.IsHitTestVisible);
        Assert.True(bar.IsHitTestVisible);
        Assert.False(bar.Focusable);
        var centered = visual.TranslatePoint(default, thumb).X + visual.ActualWidth / 2;
        Assert.InRange(Math.Abs(centered - thumb.ActualWidth / 2), 0, .5);
        Assert.Equal(272, menu.Width);
        Assert.Equal(IntPtr.Zero, new WindowInteropHelper(menu).Handle);
    });

    [Fact]
    public void Hidden_hover_reveal_and_leave_are_synchronous_and_do_not_arm_background_work() => Sta(menu =>
    {
        menu.TrySetPageHeightLimit(200);
        menu.OpenSection(MenuSection.Appearance);
        Arrange(menu);
        var changes = 0;
        menu.SettingsChangeRequested += (_, _) => changes++;
        menu.SetScrollbarHover(true);
        Assert.Equal(1, menu.ScrollbarVisualOpacity);
        Assert.False(menu.IsScrollbarIdleTimerRunning);
        Assert.False(menu.IsScrollbarTransitionActive);
        menu.SetScrollbarHover(false);
        Assert.Equal(0, menu.ScrollbarVisualOpacity);
        Assert.False(menu.IsScrollbarIdleTimerRunning);
        Assert.False(menu.IsScrollbarTransitionActive);
        Assert.Equal(MenuSection.Appearance, menu.CurrentSection);
        Assert.Equal(0, changes);
        Assert.False(menu.IsKeyboardFocusWithin);
        Assert.Equal(IntPtr.Zero, new WindowInteropHelper(menu).Handle);
    });

    [Fact]
    public void Hidden_scrolling_reveals_a_position_indicator_without_starting_an_idle_timer() => Sta(menu =>
    {
        Assert.True(menu.TrySetPageHeightLimit(200));
        menu.OpenSection(MenuSection.Appearance);
        Arrange(menu);
        var viewer = Named<ScrollViewer>(menu, "MenuScroller");
        viewer.ScrollToVerticalOffset(viewer.ScrollableHeight);
        Arrange(menu);
        Assert.True(viewer.VerticalOffset > 0);
        Assert.Equal(1, menu.ScrollbarVisualOpacity);
        Assert.False(menu.IsScrollbarIdleTimerRunning);
        Assert.False(menu.IsScrollbarTransitionActive);
        menu.FinishScrollbarInteraction();
        Assert.Equal(0, menu.ScrollbarVisualOpacity);
        Assert.Equal(IntPtr.Zero, new WindowInteropHelper(menu).Handle);
    });

    [Fact]
    public void Closing_a_hidden_menu_clears_scrollbar_reveal_timer_and_clock_state() => Sta(menu =>
    {
        menu.OpenSection(MenuSection.Appearance);
        Arrange(menu);
        menu.SetScrollbarHover(true);
        Assert.Equal(1, menu.ScrollbarVisualOpacity);
        menu.Close();
        Assert.Equal(0, menu.ScrollbarVisualOpacity);
        Assert.False(menu.IsScrollbarIdleTimerRunning);
        Assert.False(menu.IsScrollbarTransitionActive);
    });

    [Fact]
    public void A_page_that_fits_does_not_reserve_a_blank_scrollbar_gutter() => Sta(menu =>
    {
        Assert.True(menu.TrySetPageHeightLimit(460));
        menu.ReturnToPrimary();
        Arrange(menu);
        var viewer = Named<ScrollViewer>(menu, "MenuScroller");
        Assert.InRange(viewer.ScrollableHeight, 0, .5);
        Assert.Equal(ScrollBarVisibility.Auto, viewer.VerticalScrollBarVisibility);
        Assert.Equal(Visibility.Collapsed, viewer.ComputedVerticalScrollBarVisibility);
        var bar = VerticalBar(viewer);
        Assert.Equal(Visibility.Collapsed, bar.Visibility);
        var presenter = Assert.IsType<ScrollContentPresenter>(viewer.Template.FindName("PART_ScrollContentPresenter", viewer));
        Assert.InRange(Math.Abs(viewer.ActualWidth - presenter.ActualWidth), 0, .5);
        Assert.Null(menu.FindName("PaginationFooter"));
        Assert.Null(menu.FindName("InterfaceTabButton"));
        Assert.Null(menu.FindName("CompanionTabButton"));
    });

    private static ScrollBar VerticalBar(ScrollViewer viewer)
    {
        viewer.ApplyTemplate();
        return Assert.IsType<ScrollBar>(viewer.Template.FindName("PART_VerticalScrollBar", viewer));
    }
    private static T Named<T>(TrayMenuWindow menu, string name) where T : FrameworkElement => Assert.IsAssignableFrom<T>(menu.FindName(name));
    private static void Arrange(TrayMenuWindow menu)
    {
        var root = (FrameworkElement)menu.Content;
        for (var index = 0; index < 3; index++)
        {
            root.Measure(new Size(menu.Width, double.PositiveInfinity));
            root.Arrange(new Rect(0, 0, menu.Width, root.DesiredSize.Height));
            root.UpdateLayout();
        }
    }
    private static void Sta(Action<TrayMenuWindow> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            TrayMenuWindow? menu = null;
            try
            {
                menu = new TrayMenuWindow(AppSettings.Default with { CompanionBall = true, BongoCatVisible = true, CompanionTyping = false });
                action(menu);
            }
            catch (Exception error) { failure = error; }
            finally { menu?.Close(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Hidden scrollbar structure test did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
