using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

// Hidden managed windows only: no Show/ShowAt, native handles, account data,
// clock seeking or claims about actual rendered timing. The isolated native
// proof covers the visible popup animation and its real-clock interruptions.
[Collection("Native UI isolation")]
public sealed class TrayMenuPopupMotionTests
{
    [Theory]
    [InlineData(true, true, true, false, true)]
    [InlineData(false, true, true, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, true, true, true, false)]
    [InlineData(false, false, false, true, false)]
    public void Popup_motion_requires_visibility_loading_and_both_motion_policies(bool visible, bool loaded,
        bool systemMotion, bool reduced, bool expected) =>
        Assert.Equal(expected, TrayMenuWindow.PopupMotionEnabled(visible, loaded, systemMotion, reduced));

    [Fact]
    public void Popup_contract_matches_the_existing_left_popup_without_changing_page_timing()
    {
        Assert.Equal(220, TrayMenuWindow.PopupEntranceAnimationMilliseconds);
        Assert.Equal(130, TrayMenuWindow.PopupExitAnimationMilliseconds);
        Assert.Equal(10, TrayMenuWindow.PopupMotionOffsetDip);
        Assert.Equal(EasingMode.EaseOut, TrayMenuWindow.PopupExitEasingMode);
        Assert.Equal(QuotaPopoverWindow.EntranceAnimationMilliseconds, TrayMenuWindow.PopupEntranceAnimationMilliseconds);
        Assert.Equal(QuotaPopoverWindow.ExitAnimationMilliseconds, TrayMenuWindow.PopupExitAnimationMilliseconds);
        Assert.Equal(QuotaPopoverWindow.ExitEasingMode, TrayMenuWindow.PopupExitEasingMode);
    }

    [Fact]
    public void Hidden_entry_finishes_synchronously_for_all_four_offset_directions() => Sta(menu =>
    {
        var layout = LayoutContract(menu);
        foreach (var offset in new[] { (10d, 0d), (-10d, 0d), (0d, 10d), (0d, -10d) })
        {
            menu.PlayPopupEntrance(offset);
            AssertNormalized(menu);
            Assert.Equal(layout, LayoutContract(menu));
            AssertManagedOnly(menu);
        }
    });

    [Fact]
    public void Repeated_hidden_entries_invalidate_old_generations_without_actions_or_layout_changes() => Sta(menu =>
    {
        var saves = 0; var launches = 0;
        menu.SettingsChangeRequested += (_, _) => saves++;
        menu.OpenCodexRequested += (_, _) => launches++;
        var layout = LayoutContract(menu);
        var generation = menu.PopupTransitionGeneration;
        for (var index = 0; index < 16; index++)
        {
            menu.PlayPopupEntrance(index % 2 == 0 ? (10, 0) : (0, -10));
            Assert.True(menu.PopupTransitionGeneration > generation);
            generation = menu.PopupTransitionGeneration;
            AssertNormalized(menu);
        }
        Assert.Equal(0, saves);
        Assert.Equal(0, launches);
        Assert.Equal(layout, LayoutContract(menu));
        AssertManagedOnly(menu);
    });

    [Fact]
    public void Reduced_motion_normalizes_the_popup_surface_and_preserves_the_selected_page() => Sta(menu =>
    {
        menu.OpenSection(MenuSection.Appearance);
        var shell = Named<FrameworkElement>(menu, "MenuShell");
        var shift = Named<TranslateTransform>(menu, "MenuPopupTranslate");
        var scale = Named<ScaleTransform>(menu, "MenuPopupScale");
        shell.Opacity = .28; shift.X = 8; shift.Y = -5;
        scale.ScaleX = .97; scale.ScaleY = .98;

        menu.ApplySettings(AppSettings.Default with { ReduceCompanionMotion = true });

        AssertNormalized(menu);
        Assert.Equal(MenuSection.Appearance, menu.CurrentSection);
        Assert.Equal(MenuSection.Appearance, menu.PresentedSection);
        menu.PlayPopupEntrance((0, -10));
        AssertNormalized(menu);
        AssertManagedOnly(menu);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Hidden_hide_is_immediate_repeatable_and_does_not_start_an_outro(bool reduced) => Sta(menu =>
    {
        menu.ApplySettings(AppSettings.Default with { ReduceCompanionMotion = reduced });
        menu.PlayPopupEntrance((10, 0));
        menu.RequestHide();
        AssertNormalized(menu);
        menu.RequestHide();
        AssertNormalized(menu);
        AssertManagedOnly(menu);
    });

    [Fact]
    public void Immediate_hide_keeps_a_hidden_popup_at_exact_final_values() => Sta(menu =>
    {
        menu.PlayPopupEntrance((0, -10));
        menu.Hide();
        AssertNormalized(menu);
        AssertManagedOnly(menu);
    });

    [Fact]
    public void Closing_invalidates_popup_generation_and_queued_entries_cannot_reopen_it() => Sta(menu =>
    {
        menu.PlayPopupEntrance((10, 0));
        var generation = menu.PopupTransitionGeneration;
        menu.Close();
        Assert.True(menu.PopupTransitionGeneration > generation);
        AssertNormalized(menu);
        generation = menu.PopupTransitionGeneration;
        menu.PlayPopupEntrance((0, -10));
        menu.RequestHide();
        Assert.Equal(generation, menu.PopupTransitionGeneration);
        AssertNormalized(menu);
        AssertManagedOnly(menu);
    });

    [Fact]
    public void Popup_entry_does_not_own_or_rewrite_the_existing_page_transition_surface() => Sta(menu =>
    {
        menu.OpenSection(MenuSection.Preferences);
        var content = Named<FrameworkElement>(menu, "MenuContent");
        var pageShift = Assert.IsType<TranslateTransform>(content.RenderTransform);
        // Deliberate values test surface ownership; no animation clock is simulated.
        content.Opacity = .53; pageShift.X = 2;
        var pageGeneration = menu.PageTransitionGeneration;

        menu.PlayPopupEntrance((0, 10));

        AssertNormalized(menu);
        Assert.Equal(.53, content.Opacity);
        Assert.Equal(2, pageShift.X);
        Assert.Equal(pageGeneration, menu.PageTransitionGeneration);
        Assert.Equal(MenuSection.Preferences, menu.CurrentSection);
        Assert.Equal(MenuSection.Preferences, menu.PresentedSection);
        Assert.NotSame(pageShift, Named<TranslateTransform>(menu, "MenuPopupTranslate"));
        AssertManagedOnly(menu);
    });

    [Fact]
    public void Popup_preserves_accepted_semibold_font_width_and_bounded_height() => Sta(menu =>
    {
        var before = LayoutContract(menu);
        menu.PlayPopupEntrance((0, -10));
        Assert.Equal(before, LayoutContract(menu));
        Assert.Equal(272, menu.Width);
        Assert.Equal(460, menu.MaxHeight);
        Assert.Equal(SizeToContent.Height, menu.SizeToContent);
        Assert.Equal(FontWeights.SemiBold, menu.FontWeight);
        Assert.Equal(OverlayTypography.Interface, menu.FontFamily);
        var title = Named<TextBlock>(menu, "TitleText");
        Assert.Equal(13, title.FontSize);
        Assert.Equal(FontWeights.SemiBold, title.FontWeight);
        var back = Assert.IsType<TextBlock>(Named<Button>(menu, "PageBackButton").Content);
        var runs = back.Inlines.OfType<Run>().ToArray();
        Assert.Equal(2, runs.Length);
        Assert.Equal(FontWeights.Normal, runs[0].FontWeight);
        Assert.Equal(FontWeights.SemiBold, runs[1].FontWeight);
        AssertNormalized(menu);
        AssertManagedOnly(menu);
    });

    private static T Named<T>(TrayMenuWindow menu, string name) where T : class =>
        Assert.IsAssignableFrom<T>(menu.FindName(name));

    private static void AssertNormalized(TrayMenuWindow menu)
    {
        var shell = Named<FrameworkElement>(menu, "MenuShell");
        var shift = Named<TranslateTransform>(menu, "MenuPopupTranslate");
        var scale = Named<ScaleTransform>(menu, "MenuPopupScale");
        Assert.False(menu.IsPopupTransitionActive);
        Assert.False(menu.IsPopupClosing);
        Assert.Equal(1, shell.Opacity);
        Assert.Equal(0, shift.X); Assert.Equal(0, shift.Y);
        Assert.Equal(1, scale.ScaleX); Assert.Equal(1, scale.ScaleY);
        Assert.False(DependencyPropertyHelper.GetValueSource(shell, UIElement.OpacityProperty).IsAnimated);
        Assert.False(shift.HasAnimatedProperties);
        Assert.False(scale.HasAnimatedProperties);
        Assert.True(shell.LayoutTransform.Value.IsIdentity);
    }

    private static object LayoutContract(TrayMenuWindow menu) => new
    {
        menu.Width, menu.Height, menu.MinWidth, menu.MinHeight, menu.MaxHeight,
        menu.SizeToContent, menu.WindowStyle, menu.ResizeMode, menu.FontWeight,
        Margin = Named<FrameworkElement>(menu, "MenuShell").Margin,
        Layout = Named<FrameworkElement>(menu, "MenuShell").LayoutTransform.Value,
    };

    private static void AssertManagedOnly(TrayMenuWindow menu)
    {
        Assert.False(menu.IsVisible);
        Assert.False(menu.IsLoaded);
        Assert.False(menu.IsKeyboardFocusWithin);
        Assert.Null(PresentationSource.FromVisual(menu));
    }

    private static void Sta(Action<TrayMenuWindow> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            TrayMenuWindow? menu = null;
            try { menu = new TrayMenuWindow(AppSettings.Default); action(menu); }
            catch (Exception error) { failure = error; }
            finally { menu?.Close(); dispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Hidden menu popup check did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
