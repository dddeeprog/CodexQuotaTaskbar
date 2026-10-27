using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.UI;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace CodexQuotaTaskbar.Host.Tests.UI;

/// <summary>
/// Native WPF controls with synthetic settings only. Never shows a window,
/// initializes a controller/browser, injects input, or writes user settings.
/// </summary>
public sealed class CompactTrayMenuTests
{
    private static AppSettings DistinctSettings => new(false, false, false, 31,
        true, false, 37, true, true, false, false, false, false, "bongo-cat", 192);

    [Fact]
    public void Primary_page_includes_workbench_without_dot_or_preview_entries() => Sta(menu =>
    {
        var primary = Named<StackPanel>(menu, "MenuItems");
        Assert.Equal(new[]
        {
            "OpenCodexButton", "OpenWorkbenchButton", "RefreshButton", "AppearanceButton",
            "PreferencesButton", "CheckUpdatesButton", "ExitButton",
        }, primary.Children.OfType<Button>().Select(button => button.Name));

        var appearance = Named<FrameworkElement>(menu, "AppearanceRoot");
        var preferences = Named<FrameworkElement>(menu, "PreferencesRoot");
        foreach (var name in new[] { "DotRealtimeButton", "SubscriptionLoginButton", "TrayIconButton",
                     "NotificationsButton", "SubscriptionDetailsButton", "AutomaticUpdatesButton", "AutostartButton" })
            Assert.True(preferences.IsAncestorOf(Named<Button>(menu, name)), name);
        foreach (var name in new[] { "ClassicStyleButton", "UnifiedStyleButton", "AllTaskbarsButton",
                     "CompanionWhiteBallButton", "CompanionBongoCatButton", "CompanionSignalLightButton",
                     "BongoSize160Button", "BongoSize192Button", "BongoSize240Button",
                     "CompanionMotionButton", "CompanionTypingButton" })
            Assert.True(appearance.IsAncestorOf(Named<Button>(menu, name)), name);
        Assert.True(appearance.IsAncestorOf(Named<Slider>(menu, "MaterialTransparencySlider")));
        Assert.Equal(MenuSection.None, menu.CurrentSection);
        Assert.Null(menu.FindName("DotChatButton"));
        Assert.Null(menu.FindName("CompanionPreviewButton"));
        AssertNoNativeWindow(menu);
    });

    [Theory]
    [InlineData("ClassicStyleButton")]
    [InlineData("UnifiedStyleButton")]
    [InlineData("AllTaskbarsButton")]
    [InlineData("TrayIconButton")]
    [InlineData("NotificationsButton")]
    [InlineData("AutomaticUpdatesButton")]
    [InlineData("SubscriptionDetailsButton")]
    [InlineData("AutostartButton")]
    [InlineData("DotRealtimeButton")]
    [InlineData("CompanionWhiteBallButton")]
    [InlineData("CompanionBongoCatButton")]
    [InlineData("CompanionSignalLightButton")]
    [InlineData("BongoSize160Button")]
    [InlineData("BongoSize192Button")]
    [InlineData("BongoSize240Button")]
    [InlineData("CompanionMotionButton")]
    [InlineData("CompanionTypingButton")]
    public void Every_settings_choice_preserves_unrelated_preferences_and_independent_displays(string name)
    {
        var before = name switch
        {
            "UnifiedStyleButton" => DistinctSettings with { UnifiedPanel = false },
            "CompanionWhiteBallButton" => DistinctSettings with { CompanionBall = false, BongoCatVisible = true },
            "CompanionBongoCatButton" => DistinctSettings with { CompanionAppearance = "white-ball", BongoCatVisible = false },
            "CompanionSignalLightButton" => DistinctSettings with { BongoCatVisible = true },
            "BongoSize192Button" => DistinctSettings with { BongoSizeDip = 160 },
            _ => DistinctSettings,
        };
        var expected = name switch
        {
            "ClassicStyleButton" => before with { UnifiedPanel = false },
            "UnifiedStyleButton" => before with { UnifiedPanel = true },
            "AllTaskbarsButton" => before with { ShowAllTaskbars = true },
            "TrayIconButton" => before with { ShowTrayIcon = true },
            "NotificationsButton" => before with { LowQuotaNotifications = true },
            "AutomaticUpdatesButton" => before with { AutomaticUpdates = false },
            "SubscriptionDetailsButton" => before with { SubscriptionDetails = true },
            "AutostartButton" => before with { StartWithWindows = true },
            "DotRealtimeButton" => before with { DotRealtime = true },
            "CompanionWhiteBallButton" => before with { CompanionBall = true, BongoCatVisible = true },
            "CompanionBongoCatButton" => before with { BongoCatVisible = true },
            "CompanionSignalLightButton" => before with { CompanionBall = false, BongoCatVisible = true },
            "BongoSize160Button" => before with { BongoSizeDip = 160 },
            "BongoSize192Button" => before with { BongoSizeDip = 192 },
            "BongoSize240Button" => before with { BongoSizeDip = 240 },
            "CompanionMotionButton" => before with { ReduceCompanionMotion = true },
            "CompanionTypingButton" => before with { CompanionTyping = true },
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
        Sta(menu =>
        {
            var changes = new List<AppSettings>();
            menu.SettingsChangeRequested += (_, value) => changes.Add(value);
            var button = Named<Button>(menu, name);
            Assert.True(button.IsEnabled);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(expected, Assert.Single(changes));
            AssertNoNativeWindow(menu);
        }, before);
    }

    [Theory]
    [InlineData("RefreshButton")]
    [InlineData("CheckUpdatesButton")]
    [InlineData("OpenCodexButton")]
    [InlineData("SubscriptionLoginButton")]
    [InlineData("ExitButton")]
    public void Every_existing_action_keeps_its_event_without_modifying_settings(string name) => Sta(menu =>
    {
        var hits = new List<string>();
        var settingsChanged = 0;
        menu.RefreshRequested += (_, _) => hits.Add("RefreshButton");
        menu.CheckUpdatesRequested += (_, _) => hits.Add("CheckUpdatesButton");
        menu.OpenCodexRequested += (_, _) => hits.Add("OpenCodexButton");
        menu.OpenSubscriptionLoginRequested += (_, _) => hits.Add("SubscriptionLoginButton");
        menu.ExitRequested += (_, _) => hits.Add("ExitButton");
        menu.SettingsChangeRequested += (_, _) => settingsChanged++;
        var button = Named<Button>(menu, name);
        Assert.True(button.IsEnabled);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(name, Assert.Single(hits));
        Assert.Equal(0, settingsChanged);
        AssertNoNativeWindow(menu);
    }, DistinctSettings with { CompanionAppearance = "white-ball" });

    [Fact]
    public void Transparency_previews_immediately_and_flushes_once_without_resetting_other_preferences() => Sta(menu =>
    {
        var changes = new List<AppSettings>();
        var previews = new List<int>();
        menu.SettingsChangeRequested += (_, value) => changes.Add(value);
        menu.MaterialTransparencyPreviewRequested += (_, value) => previews.Add(value);
        Named<Slider>(menu, "MaterialTransparencySlider").Value = 42;
        Assert.Equal(42, Assert.Single(previews));
        Assert.Empty(changes);
        menu.FlushMaterialTransparencyChange();
        Assert.Equal(DistinctSettings with { MaterialTransparencyPercent = 42 }, Assert.Single(changes));
        menu.FlushMaterialTransparencyChange();
        Assert.Single(changes);
        menu.ApplySettings(DistinctSettings with { MaterialTransparencyPercent = 63 });
        Assert.Single(previews);
        Assert.Single(changes);
        AssertNoNativeWindow(menu);
    });

    [Fact]
    public void Menu_pages_remain_the_same_width_and_never_accumulate_each_others_height() => Sta(menu =>
    {
        var changes = new List<AppSettings>();
        menu.SettingsChangeRequested += (_, value) => changes.Add(value);
        var primaryHeight = Arrange(menu);
        Assert.Equal(272, menu.Width);
        foreach (var section in new[] { MenuSection.Appearance, MenuSection.Preferences, MenuSection.Appearance })
        {
            menu.OpenSection(section);
            Assert.Equal(section, menu.CurrentSection);
            Assert.Equal(272, menu.Width);
            var height = Arrange(menu);
            Assert.InRange(height, 32, 520);
            AssertSinglePage(menu, section);
            menu.ReturnToPrimary();
            Assert.Equal(MenuSection.None, menu.CurrentSection);
            Assert.Equal(primaryHeight, Arrange(menu), 3);
            Assert.Equal(272, menu.Width);
            AssertSinglePage(menu, MenuSection.None);
        }
        Assert.Empty(changes);
        AssertNoNativeWindow(menu);
    });

    [Fact]
    public void Secondary_page_back_button_returns_in_place_without_changing_preferences() => Sta(menu =>
    {
        var changes = new List<AppSettings>();
        menu.SettingsChangeRequested += (_, value) => changes.Add(value);
        foreach (var section in new[] { MenuSection.Appearance, MenuSection.Preferences })
        {
            menu.OpenSection(section);
            Arrange(menu);
            var header = Named<Border>(menu, "PageHeader");
            var back = Named<Button>(menu, "PageBackButton");
            Assert.Equal(Visibility.Visible, header.Visibility);
            Assert.True(header.IsAncestorOf(back));
            Assert.False(Named<ScrollViewer>(menu, "MenuScroller").IsAncestorOf(back));
            Assert.True(back.ActualHeight >= 32 && back.ActualWidth >= 32);
            Assert.Equal("返回主菜单", AutomationProperties.GetName(back));
            back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(MenuSection.None, menu.CurrentSection);
            Assert.Equal(Visibility.Collapsed, header.Visibility);
            AssertSinglePage(menu, MenuSection.None);
            Assert.Equal(272, menu.Width);
        }
        Assert.Empty(changes);
        AssertNoNativeWindow(menu);
    });

    [Theory]
    [InlineData(1, Key.Escape)]
    [InlineData(1, Key.Left)]
    [InlineData(2, Key.Escape)]
    [InlineData(2, Key.Left)]
    public void Hidden_page_keyboard_returns_without_focusing_or_creating_a_window(int section, Key key) => Sta(menu =>
    {
        var focus = Keyboard.FocusedElement;
        menu.OpenSection((MenuSection)section);
        Arrange(menu);
        Assert.True(menu.HandleMenuKey(Key.Down));
        Assert.Equal((MenuSection)section, menu.CurrentSection);
        Assert.True(menu.HandleMenuKey(key));
        Assert.Equal(MenuSection.None, menu.CurrentSection);
        Assert.False(menu.HandleMenuKey(Key.Left));
        Assert.True(menu.HandleMenuKey(Key.Escape));
        Assert.False(menu.HandleMenuKey(Key.F1));
        Assert.Same(focus, Keyboard.FocusedElement);
        AssertNoNativeWindow(menu);
    });

    [Fact]
    public void Short_page_scrolls_to_the_last_control_and_keeps_the_return_header_outside_content() => Sta(menu =>
    {
        Assert.True(menu.TrySetPageHeightLimit(200));
        menu.OpenSection(MenuSection.Appearance);
        var actualHeight = Arrange(menu);
        var scroll = Named<ScrollViewer>(menu, "MenuScroller");
        var header = Named<Border>(menu, "PageHeader");
        var back = Named<Button>(menu, "PageBackButton");
        Assert.InRange(actualHeight, 64, 200.5);
        Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
        Assert.True(scroll.ScrollableHeight > .5);
        Assert.Equal(Visibility.Visible, header.Visibility);
        Assert.False(scroll.IsAncestorOf(back));
        var content = (FrameworkElement)menu.Content;
        var backTop = back.TranslatePoint(default(Point), content).Y;
        Assert.InRange(backTop, 0, actualHeight - back.ActualHeight);
        Assert.True(back.ActualHeight >= 32);
        var last = Named<Button>(menu, "CompanionTypingButton");
        ScrollIntoView(menu, last);
        var viewportBounds = new Rect(scroll.TranslatePoint(default(Point), content), scroll.RenderSize);
        var lastBounds = new Rect(last.TranslatePoint(default(Point), content), last.RenderSize);
        Assert.True(lastBounds.Top >= viewportBounds.Top - .5 && lastBounds.Bottom <= viewportBounds.Bottom + .5);
        Assert.Equal(backTop, back.TranslatePoint(default(Point), content).Y, 3);
        Assert.True(last.ActualHeight >= 32 && last.ActualWidth >= 32);
        Assert.Equal(13, last.FontSize);
        Assert.Equal(272, menu.Width);
        AssertNoNativeWindow(menu);
    });

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void Horizontal_choices_keep_normal_type_and_hit_areas_inside_the_fixed_width_page(int dpi) => Sta(menu =>
    {
        menu.OpenSection(MenuSection.Appearance);
        var content = (FrameworkElement)menu.Content;
        VisualTreeHelper.SetRootDpi(content, new DpiScale(dpi / 96d, dpi / 96d));
        Arrange(menu);
        foreach (var group in new[]
        {
            new[] { "ClassicStyleButton", "UnifiedStyleButton" },
            new[] { "CompanionWhiteBallButton", "CompanionSignalLightButton" },
            new[] { "BongoSize160Button", "BongoSize192Button", "BongoSize240Button" },
        })
        {
            ScrollIntoView(menu, Named<Button>(menu, group[0]));
            Rect? previous = null;
            foreach (var name in group)
            {
                var button = Named<Button>(menu, name);
                var point = button.TranslatePoint(default(Point), content);
                var bounds = new Rect(point, button.RenderSize);
                Assert.InRange(button.FontSize, 13, 13);
                Assert.True(bounds.Width >= 32 && bounds.Height >= 32, name);
                Assert.InRange(bounds.Left, 0, menu.Width);
                Assert.InRange(bounds.Right, 0, menu.Width + .5);
                var viewport = Named<ScrollViewer>(menu, "MenuScroller");
                var viewportBounds = new Rect(viewport.TranslatePoint(default(Point), content), viewport.RenderSize);
                Assert.True(bounds.Top >= viewportBounds.Top - .5 && bounds.Bottom <= viewportBounds.Bottom + .5,
                    $"{name}; targetDpi={dpi}; bounds={bounds}; viewport={viewportBounds}; offset={viewport.VerticalOffset}; scroll={viewport.ScrollableHeight}; contentDpi={VisualTreeHelper.GetDpi(content).DpiScaleX}; buttonDpi={VisualTreeHelper.GetDpi(button).DpiScaleX}; groupDpi={VisualTreeHelper.GetDpi((Visual)button.Parent).DpiScaleX}; actualHeight={button.ActualHeight}; groupHeight={((FrameworkElement)button.Parent).ActualHeight}; rounding={button.UseLayoutRounding}");
                if (previous is { } before)
                {
                    Assert.InRange(Math.Abs(before.Top - bounds.Top), 0, 1);
                    Assert.True(before.Right <= bounds.Left + .5,
                        $"{name}; targetDpi={dpi}; previous={before}; current={bounds}; overlap={before.Right - bounds.Left}; groupWidth={((FrameworkElement)button.Parent).ActualWidth}; buttonWidth={button.ActualWidth}; groupDpi={VisualTreeHelper.GetDpi((Visual)button.Parent).DpiScaleX}; buttonDpi={VisualTreeHelper.GetDpi(button).DpiScaleX}; rounding={button.UseLayoutRounding}; margin={button.Margin}");
                }
                previous = bounds;
                foreach (var text in Descendants<TextBlock>(button).Where(item => !string.IsNullOrWhiteSpace(item.Text)))
                {
                    var formatted = new FormattedText(text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
                        new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
                        text.FontSize, text.Foreground, dpi / 96d);
                    Assert.True(formatted.WidthIncludingTrailingWhitespace <= text.ActualWidth + 1, name + ": " + text.Text);
                }
            }
        }
        AssertNoNativeWindow(menu);
    });

    [Fact]
    public void Applying_model_preferences_preserves_input_and_selection_and_disabled_choices() => Sta(menu =>
    {
        var changes = new List<AppSettings>();
        menu.SettingsChangeRequested += (_, value) => changes.Add(value);
        menu.ApplySettings(DistinctSettings with { CompanionAppearance = "white-ball" });
        Assert.Equal("已选中", AutomationProperties.GetItemStatus(Named<Button>(menu, "CompanionWhiteBallButton")));
        Assert.Equal("已关闭", AutomationProperties.GetItemStatus(Named<Button>(menu, "CompanionBongoCatButton")));
        Assert.Null(menu.FindName("CompanionPreviewButton"));
        foreach (var size in new[] { 160, 192, 240 }) Assert.False(Named<Button>(menu, $"BongoSize{size}Button").IsEnabled);
        menu.ApplySettings(DistinctSettings with { BongoSizeDip = 160 });
        Assert.Null(menu.FindName("CompanionPreviewButton"));
        Assert.Equal("已选中", AutomationProperties.GetItemStatus(Named<Button>(menu, "CompanionWhiteBallButton")));
        Assert.Equal("已开启", AutomationProperties.GetItemStatus(Named<Button>(menu, "CompanionBongoCatButton")));
        foreach (var size in new[] { 160, 192, 240 }) Assert.True(Named<Button>(menu, $"BongoSize{size}Button").IsEnabled);
        Assert.Equal("已选中", AutomationProperties.GetItemStatus(Named<Button>(menu, "BongoSize160Button")));
        Assert.Equal("未选中", AutomationProperties.GetItemStatus(Named<Button>(menu, "CompanionTypingButton")));
        Assert.Empty(changes);
        AssertNoNativeWindow(menu);
    });

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void Tall_secondary_page_uses_a_bounded_same_width_lane_without_covering_the_whole_island(int dpi)
    {
        var scale = dpi / 96d;
        int Px(double value) => checked((int)Math.Ceiling(value * scale));
        var work = new ScreenRect(0, 0, Px(600), Px(400));
        var island = new ScreenRect(Px(310), Px(8), Px(590), Px(340));
        Assert.True(TrayMenuWindow.TryCalculateIslandBounds(work, island, (uint)dpi, 480,
            out var bounds, out var maximum));
        Assert.Equal(Px(272), bounds.Width);
        Assert.InRange(maximum, 120, 400);
        Assert.True(maximum < 480);
        AssertInside(work, bounds, Px(8));
        Assert.False(Intersects(island, bounds));
    }

    private static void AssertSinglePage(TrayMenuWindow menu, MenuSection section)
    {
        var appearance = Named<FrameworkElement>(menu, "AppearanceRoot");
        var preferences = Named<FrameworkElement>(menu, "PreferencesRoot");
        Assert.Equal(section == MenuSection.Appearance, appearance.Visibility == Visibility.Visible);
        Assert.Equal(section == MenuSection.Preferences, preferences.Visibility == Visibility.Visible);
        Assert.Equal(section == MenuSection.None, Named<StackPanel>(menu, "MenuItems").Visibility == Visibility.Visible);
    }

    private static double Arrange(TrayMenuWindow menu)
    {
        var content = (FrameworkElement)menu.Content;
        for (var pass = 0; pass < 3; pass++)
        {
            content.Measure(new Size(menu.Width, double.PositiveInfinity));
            content.Arrange(new Rect(0, 0, menu.Width, content.DesiredSize.Height));
            content.UpdateLayout();
        }
        return content.DesiredSize.Height;
    }

    private static void ScrollIntoView(TrayMenuWindow menu, FrameworkElement element)
    {
        Arrange(menu);
        var viewport = Named<ScrollViewer>(menu, "MenuScroller");
        var relativeTop = element.TranslatePoint(default(Point), viewport).Y;
        var centeredOffset = viewport.VerticalOffset + relativeTop - (viewport.ActualHeight - element.ActualHeight) / 2;
        viewport.ScrollToVerticalOffset(Math.Clamp(centeredOffset, 0, viewport.ScrollableHeight));
        Arrange(menu);
    }

    private static T Named<T>(TrayMenuWindow menu, string name) where T : DependencyObject =>
        Assert.IsAssignableFrom<T>(menu.FindName(name));

    private static void AssertNoNativeWindow(TrayMenuWindow menu)
    {
        Assert.False(menu.IsVisible);
        Assert.Equal(nint.Zero, new WindowInteropHelper(menu).Handle);
    }

    private static bool Intersects(ScreenRect a, ScreenRect b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    private static void AssertInside(ScreenRect work, ScreenRect bounds, int margin)
    {
        Assert.InRange(bounds.Left, work.Left + margin, work.Right - margin);
        Assert.InRange(bounds.Right, work.Left + margin, work.Right - margin);
        Assert.InRange(bounds.Top, work.Top + margin, work.Bottom - margin);
        Assert.InRange(bounds.Bottom, work.Top + margin, work.Bottom - margin);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Sta(Action<TrayMenuWindow> action, AppSettings? settings = null)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            TrayMenuWindow? menu = null;
            try { menu = new TrayMenuWindow(settings ?? DistinctSettings); action(menu); }
            catch (Exception error) { failure = error; }
            finally { menu?.Close(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Offscreen compact menu test did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
