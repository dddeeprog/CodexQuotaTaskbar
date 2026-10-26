using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

// The visible-clock path is exercised by CompactMenuMotionProof in its own
// alpha-zero process. These tests never Show, focus, or create a native handle.
public sealed class CompactMenuTransitionTests
{
    [Theory]
    [InlineData(true, true, true, false, true)]
    [InlineData(false, true, true, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, true, true, true, false)]
    [InlineData(false, false, false, true, false)]
    public void Motion_requires_a_loaded_visible_menu_and_both_motion_policies(bool visible, bool loaded,
        bool systemMotion, bool reduceMotion, bool expected) =>
        Assert.Equal(expected, TrayMenuWindow.PageMotionEnabled(visible, loaded, systemMotion, reduceMotion));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Hidden_page_requests_are_synchronous_and_do_not_leave_clocks(int targetValue) => Sta(menu =>
    {
        var target = (MenuSection)targetValue;
        menu.OpenSection(target);
        Assert.Equal(target, menu.CurrentSection);
        Assert.Equal(target, menu.PresentedSection);
        AssertNormalized(menu);
        AssertUniquePage(menu, target);
        Assert.Equal(272, menu.Width);
        Assert.Equal(IntPtr.Zero, new WindowInteropHelper(menu).Handle);
    });

    [Fact]
    public void Hidden_rapid_requests_keep_only_the_latest_page_without_saving_or_focusing() => Sta(menu =>
    {
        var saved = 0;
        menu.SettingsChangeRequested += (_, _) => saved++;
        for (var index = 0; index < 25; index++)
        {
            menu.OpenSection(MenuSection.Appearance);
            menu.ReturnToPrimary();
            menu.OpenSection(MenuSection.Preferences);
        }
        Assert.Equal(MenuSection.Preferences, menu.CurrentSection);
        Assert.Equal(MenuSection.Preferences, menu.PresentedSection);
        AssertUniquePage(menu, MenuSection.Preferences);
        AssertNormalized(menu);
        Assert.Equal(0, saved);
        Assert.False(menu.IsKeyboardFocusWithin);
        Assert.Equal(IntPtr.Zero, new WindowInteropHelper(menu).Handle);
    });

    [Fact]
    public void Applying_reduce_motion_restores_exact_surface_values_without_changing_the_page() => Sta(menu =>
    {
        menu.OpenSection(MenuSection.Appearance);
        var surface = Named<FrameworkElement>(menu, "MenuContent");
        surface.Opacity = .27;
        Assert.IsType<TranslateTransform>(surface.RenderTransform).X = 3;
        menu.ApplySettings(AppSettings.Default with { ReduceCompanionMotion = true });
        AssertNormalized(menu);
        AssertUniquePage(menu, MenuSection.Appearance);
        Assert.Equal(MenuSection.Appearance, menu.PresentedSection);
        Assert.Equal(IntPtr.Zero, new WindowInteropHelper(menu).Handle);
    });

    [Fact]
    public void Return_header_stays_outside_the_animated_content_and_returns_synchronously_when_hidden() => Sta(menu =>
    {
        menu.OpenSection(MenuSection.Preferences);
        var header = Named<FrameworkElement>(menu, "PageHeader");
        var content = Named<FrameworkElement>(menu, "MenuContent");
        Assert.False(content.IsAncestorOf(header));
        Assert.True(header.RenderTransform.Value.IsIdentity);
        menu.ReturnToPrimary();
        Assert.Equal(Visibility.Collapsed, header.Visibility);
        Assert.Equal(MenuSection.None, menu.PresentedSection);
        AssertNormalized(menu);
        AssertUniquePage(menu, MenuSection.None);
    });

    [Fact]
    public void Closing_a_hidden_menu_removes_surface_animation_state() => Sta(menu =>
    {
        menu.OpenSection(MenuSection.Appearance);
        var generation = menu.PageTransitionGeneration;
        menu.Close();
        Assert.True(menu.PageTransitionGeneration > generation);
        AssertNormalized(menu);
        Assert.Equal(MenuSection.None, menu.PresentedSection);
    });

    [Fact]
    public void Invalid_target_does_not_mutate_the_presented_page() => Sta(menu =>
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => menu.OpenSection((MenuSection)99));
        AssertUniquePage(menu, MenuSection.None);
        AssertNormalized(menu);
    });

    private static T Named<T>(TrayMenuWindow menu, string name) where T : FrameworkElement =>
        Assert.IsAssignableFrom<T>(menu.FindName(name));

    private static void AssertNormalized(TrayMenuWindow menu)
    {
        var surface = Named<FrameworkElement>(menu, "MenuContent");
        var shift = Assert.IsType<TranslateTransform>(surface.RenderTransform);
        Assert.False(menu.IsPageTransitionActive);
        Assert.Equal(1, surface.Opacity);
        Assert.Equal(0, shift.X);
        Assert.True(surface.IsHitTestVisible);
        Assert.True(surface.IsEnabled);
        Assert.False(DependencyPropertyHelper.GetValueSource(surface, UIElement.OpacityProperty).IsAnimated);
        Assert.False(shift.HasAnimatedProperties);
    }

    private static void AssertUniquePage(TrayMenuWindow menu, MenuSection target)
    {
        var primary = Named<FrameworkElement>(menu, "MenuItems").Visibility;
        var appearance = Named<FrameworkElement>(menu, "AppearanceReveal").Visibility;
        var preferences = Named<FrameworkElement>(menu, "PreferencesReveal").Visibility;
        Assert.Single(new[] { primary, appearance, preferences }, value => value == Visibility.Visible);
        Assert.Equal(target == MenuSection.None ? Visibility.Visible : Visibility.Collapsed, primary);
        Assert.Equal(target == MenuSection.Appearance ? Visibility.Visible : Visibility.Collapsed, appearance);
        Assert.Equal(target == MenuSection.Preferences ? Visibility.Visible : Visibility.Collapsed, preferences);
    }

    private static void Sta(Action<TrayMenuWindow> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            TrayMenuWindow? menu = null;
            try { menu = new TrayMenuWindow(AppSettings.Default); action(menu); }
            catch (Exception error) { failure = error; }
            finally { menu?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Hidden menu transition test did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
