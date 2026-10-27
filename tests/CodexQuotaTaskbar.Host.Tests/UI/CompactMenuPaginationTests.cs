using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit.Abstractions;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.UI;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace CodexQuotaTaskbar.Host.Tests.UI;

// Keeps the historical file identity while verifying the restored single-page
// scrolling design. No window is shown and no settings are persisted.
public sealed class CompactMenuPaginationTests
{
    private readonly ITestOutputHelper output;
    public CompactMenuPaginationTests(ITestOutputHelper output) => this.output = output;
    public static IEnumerable<object[]> LayoutCases()
    {
        foreach (var dpi in new[] { 96, 120, 144, 192 })
        foreach (var height in new[] { 200, 240, 460 })
        foreach (var section in new[] { 0, 1, 2 }) yield return [dpi, height, section];
    }

    [Theory]
    [MemberData(nameof(LayoutCases))]
    public void Every_existing_control_can_scroll_wholly_into_view_at_normal_type_and_hit_size(int dpi, int height, int section) => Sta(menu =>
    {
        var content = (FrameworkElement)menu.Content;
        // Detached content does not inherit the hidden Window's system DPI.
        // Both roots must model the same monitor, as they do when actually shown.
        VisualTreeHelper.SetRootDpi(menu, new DpiScale(dpi / 96d, dpi / 96d));
        VisualTreeHelper.SetRootDpi(content, new DpiScale(dpi / 96d, dpi / 96d));
        Assert.True(menu.TrySetPageHeightLimit(height));
        menu.OpenSection((MenuSection)section);
        var actualHeight = Arrange(menu, dpi is 96 or 120 && height == 460 && section is 0 or 2
            ? message => output.WriteLine($"dpi={dpi}; height={height}; section={section}; {message}") : null);
        Assert.InRange(actualHeight, 32, height + .5);
        Assert.Equal(272, menu.Width);
        var viewport = Named<ScrollViewer>(menu, "MenuScroller");
        Assert.Equal(ScrollBarVisibility.Auto, viewport.VerticalScrollBarVisibility);
        Assert.Equal(ScrollBarVisibility.Disabled, viewport.HorizontalScrollBarVisibility);
        Assert.InRange(viewport.ScrollableWidth, 0, .5);
        // Auto uses positive overflow, not our half-DIP hit-bound tolerance.
        // Mixed-DPI template rounding can legitimately leave 1/3 DIP scrollable.
        Assert.Equal(viewport.ScrollableHeight > 0 ? Visibility.Visible : Visibility.Collapsed,
            viewport.ComputedVerticalScrollBarVisibility);
        var saved = 0;
        menu.SettingsChangeRequested += (_, _) => saved++;
        var header = Named<FrameworkElement>(menu, "PageHeader");
        var back = Named<Button>(menu, "PageBackButton");
        var initialBack = section == 0 ? default : Bounds(back, content);
        foreach (var name in Expected(section))
        {
            var element = Named<FrameworkElement>(menu, name);
            Assert.True(LocallyVisible(element, menu), "An existing control was hidden: " + name);
            ScrollWholeControlIntoView(menu, viewport, element);
            var bounds = Bounds(element, content);
            Assert.True(bounds.Width >= 32 && bounds.Height >= 32, name);
            AssertInside(ViewportBounds(viewport, content), bounds, name);
            if (element is Button button) Assert.Equal(13, button.FontSize);
            Assert.Equal((MenuSection)section, menu.CurrentSection);
            if (section != 0)
            {
                Assert.Equal(Visibility.Visible, header.Visibility);
                Assert.False(viewport.IsAncestorOf(back));
                Assert.Equal(initialBack, Bounds(back, content));
            }
        }
        Assert.Null(menu.FindName("PaginationFooter"));
        Assert.Null(menu.FindName("PreviousPageButton"));
        Assert.Null(menu.FindName("NextPageButton"));
        if (section != 0)
        {
            back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(MenuSection.None, menu.CurrentSection);
        }
        Assert.Equal(0, saved);
        Assert.False(menu.IsScrollbarIdleTimerRunning);
        Assert.False(menu.IsScrollbarTransitionActive);
        Assert.InRange(menu.ScrollbarVisualOpacity, 0, 1);
        Assert.False(menu.IsVisible);
        Assert.Equal(IntPtr.Zero, new WindowInteropHelper(menu).Handle);
    });

    [Fact]
    public void Impossible_height_is_reported_instead_of_hiding_the_return_header_or_a_complete_control() => Sta(menu =>
    {
        Assert.False(menu.TrySetPageHeightLimit(60));
        Assert.True(menu.PlacementUnavailable);
        Assert.Equal(IntPtr.Zero, new WindowInteropHelper(menu).Handle);
        Assert.True(menu.TrySetPageHeightLimit(460));
        Assert.False(menu.PlacementUnavailable);
        menu.OpenSection(MenuSection.Appearance);
        Assert.True(Arrange(menu) <= 460.5);
    });

    [Fact]
    public void Transparency_slider_is_retained_and_reachable_by_scrolling_without_manual_pages() => Sta(menu =>
    {
        Assert.True(menu.TrySetPageHeightLimit(200));
        menu.OpenSection(MenuSection.Appearance);
        Arrange(menu);
        var slider = Named<Slider>(menu, "MaterialTransparencySlider");
        var viewport = Named<ScrollViewer>(menu, "MenuScroller");
        ScrollWholeControlIntoView(menu, viewport, slider);
        Assert.True(LocallyVisible(slider, menu));
        Assert.True(slider.ActualHeight >= 32);
        AssertInside(ViewportBounds(viewport, (FrameworkElement)menu.Content), Bounds(slider, (FrameworkElement)menu.Content), slider.Name);
        var previews = new List<int>();
        menu.MaterialTransparencyPreviewRequested += (_, value) => previews.Add(value);
        slider.Value = 42;
        Assert.Equal(42, Assert.Single(previews));
        menu.FlushMaterialTransparencyChange();
    });

    [Fact]
    public void Appearance_has_all_options_on_one_scrollable_page_and_return_resets_the_offset() => Sta(menu =>
    {
        Assert.True(menu.TrySetPageHeightLimit(200));
        menu.OpenSection(MenuSection.Appearance);
        Arrange(menu);
        var viewport = Named<ScrollViewer>(menu, "MenuScroller");
        Assert.True(viewport.ScrollableHeight > 0);
        Assert.Null(menu.FindName("InterfaceTabButton"));
        Assert.Null(menu.FindName("CompanionTabButton"));
        viewport.ScrollToVerticalOffset(viewport.ScrollableHeight);
        Arrange(menu);
        Assert.True(viewport.VerticalOffset > 0);
        menu.ReturnToPrimary();
        Arrange(menu);
        Assert.Equal(0, viewport.VerticalOffset);
        menu.OpenSection(MenuSection.Appearance);
        Arrange(menu);
        Assert.Equal(0, viewport.VerticalOffset);
        foreach (var name in Expected(1)) Assert.True(LocallyVisible(Named<FrameworkElement>(menu, name), menu), name);
    });

    private static string[] Expected(int section) => section switch
    {
        0 => ["OpenCodexButton", "RefreshButton", "AppearanceButton", "PreferencesButton", "CheckUpdatesButton", "ExitButton"],
        1 => ["ClassicStyleButton", "UnifiedStyleButton", "MaterialTransparencySlider", "AllTaskbarsButton",
            "CompanionWhiteBallButton", "CompanionSignalLightButton", "CompanionBongoCatButton", "BongoSize160Button", "BongoSize192Button", "BongoSize240Button",
            "CompanionMotionButton", "CompanionTypingButton"],
        2 => ["DotRealtimeButton", "SubscriptionLoginButton", "TrayIconButton", "NotificationsButton", "SubscriptionDetailsButton", "AutomaticUpdatesButton", "AutostartButton"],
        _ => throw new ArgumentOutOfRangeException(nameof(section)),
    };

    private static void ScrollWholeControlIntoView(TrayMenuWindow menu, ScrollViewer viewport, FrameworkElement element)
    {
        var content = (FrameworkElement)menu.Content;
        var visible = ViewportBounds(viewport, content);
        var before = Bounds(element, content);
        var desired = viewport.VerticalOffset + before.Top - visible.Top - Math.Max(0, (visible.Height - before.Height) / 2);
        viewport.ScrollToVerticalOffset(Math.Clamp(desired, 0, viewport.ScrollableHeight));
        Arrange(menu);
    }
    private static Rect ViewportBounds(ScrollViewer viewport, FrameworkElement content)
    {
        var presenter = viewport.Template.FindName("PART_ScrollContentPresenter", viewport) as FrameworkElement ?? viewport;
        return Bounds(presenter, content);
    }
    private static Rect Bounds(FrameworkElement element, FrameworkElement content) => new(element.TranslatePoint(default(Point), content), element.RenderSize);
    private static bool LocallyVisible(FrameworkElement element, TrayMenuWindow menu)
    {
        for (DependencyObject? current = element; current is not null && current != menu; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement item && item.Visibility != Visibility.Visible) return false;
        return true;
    }
    private static void AssertInside(Rect outer, Rect inner, string name)
    {
        Assert.True(inner.Left >= outer.Left - .5 && inner.Top >= outer.Top - .5 &&
            inner.Right <= outer.Right + .5 && inner.Bottom <= outer.Bottom + .5,
            name + ": a complete control could not scroll into the viewport.");
    }
    private static T Named<T>(TrayMenuWindow menu, string name) where T : FrameworkElement => Assert.IsAssignableFrom<T>(menu.FindName(name));
    private static double Arrange(TrayMenuWindow menu, Action<string>? trace = null)
    {
        var content = (FrameworkElement)menu.Content;
        MeasureAndArrange();
        var viewport = Named<ScrollViewer>(menu, "MenuScroller");
        var before = Snapshot();
        // Auto-scrollbar visibility is settled through ScrollViewer's deferred
        // Dispatcher work, not by repeating synchronous UpdateLayout alone.
        // This pumps only this hidden test thread; no HWND, focus, sleep or input.
        var frame = new DispatcherFrame();
        _ = menu.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        MeasureAndArrange();
        trace?.Invoke("auto-layout before=" + before + "; after=" + Snapshot());
        return content.DesiredSize.Height;

        void MeasureAndArrange()
        {
            for (var index = 0; index < 3; index++)
            {
                content.Measure(new Size(menu.Width, double.PositiveInfinity));
                content.Arrange(new Rect(0, 0, menu.Width, content.DesiredSize.Height));
                content.UpdateLayout();
            }
        }
        string Snapshot() => System.Text.Json.JsonSerializer.Serialize(new
        {
            viewport.ExtentHeight, viewport.ViewportHeight, viewport.ScrollableHeight, viewport.VerticalOffset,
            computed = viewport.ComputedVerticalScrollBarVisibility.ToString(), viewport.ActualHeight,
            viewport.IsMeasureValid, viewport.IsArrangeValid,
            menuDpiX = VisualTreeHelper.GetDpi(menu).DpiScaleX,
            menuDpiY = VisualTreeHelper.GetDpi(menu).DpiScaleY,
            contentDpiX = VisualTreeHelper.GetDpi(content).DpiScaleX,
            contentDpiY = VisualTreeHelper.GetDpi(content).DpiScaleY,
            viewerDpiX = VisualTreeHelper.GetDpi(viewport).DpiScaleX,
            viewerDpiY = VisualTreeHelper.GetDpi(viewport).DpiScaleY,
            contentHasVisualParent = VisualTreeHelper.GetParent(content) is not null,
            menuHasVisualParent = VisualTreeHelper.GetParent(menu) is not null,
        });
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Hidden scrolling menu test did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
