using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Button = System.Windows.Controls.Button;

namespace CodexQuotaTaskbar.Host.UI;

public partial class TrayMenuWindow
{
    private const double PageOffsetDip = 4;
    private const double PageDimOpacity = .18;
    private readonly TranslateTransform pageTransitionShift = new();
    private long pageTransitionGeneration;
    private bool pageTransitionClosed;
    private MenuSection requestedMenuPage;
    private MenuSection presentedMenuPage;
    private Button? pageTransitionFocusTarget;

    internal bool IsPageTransitionActive { get; private set; }
    internal MenuSection PresentedSection => presentedMenuPage;
    internal long PageTransitionGeneration => pageTransitionGeneration;

    internal static bool PageMotionEnabled(bool visible, bool loaded, bool systemMotion, bool reduceMotion) =>
        visible && loaded && systemMotion && !reduceMotion;

    private bool CanAnimatePage => !pageTransitionClosed && PageMotionEnabled(IsVisible, IsLoaded,
        SystemParameters.ClientAreaAnimation, settings.ReduceCompanionMotion);

    private void NavigatePage(MenuSection target)
    {
        if (target == requestedMenuPage && IsPageTransitionActive && CanAnimatePage) return;
        if (target == requestedMenuPage && presentedMenuPage == target && !IsPageTransitionActive)
        {
            if (!CanAnimatePage) CancelPageTransition();
            return;
        }

        var opacity = Math.Clamp(MenuContent.Opacity, PageDimOpacity, 1);
        var offset = Math.Clamp(pageTransitionShift.X, -PageOffsetDip, PageOffsetDip);
        StopPageClocks();
        var generation = ++pageTransitionGeneration;
        pageTransitionFocusTarget = null;
        requestedMenuPage = target;
        appearanceOpen = target == MenuSection.Appearance;
        preferencesOpen = target == MenuSection.Preferences;
        UpdateSectionStatus();

        if (!CanAnimatePage)
        {
            CommitPage(target);
            NormalizePageSurface();
            return;
        }

        IsPageTransitionActive = true;
        MenuContent.IsHitTestVisible = false;
        MenuContent.IsEnabled = false;
        // Reversing before the outgoing phase commits can return directly to
        // the still-presented page, from its current opacity and offset.
        if (presentedMenuPage == target)
        {
            AnimateIncoming(generation, opacity, offset);
            return;
        }

        var direction = target == MenuSection.None ? -1 : 1;
        AnimatePage(MenuContent, OpacityProperty, opacity, PageDimOpacity, 60,
            EasingMode.EaseIn, () =>
            {
                if (!IsCurrentPageTransition(generation)) return;
                CommitPage(target);
                AnimateIncoming(generation, PageDimOpacity, direction * PageOffsetDip);
            });
        AnimatePage(pageTransitionShift, TranslateTransform.XProperty, offset, -direction * PageOffsetDip,
            60, EasingMode.EaseIn);
    }

    private void AnimateIncoming(long generation, double opacity, double offset)
    {
        if (!IsCurrentPageTransition(generation)) return;
        if (!CanAnimatePage)
        {
            CancelPageTransition();
            return;
        }
        AnimatePage(MenuContent, OpacityProperty, opacity, 1, 140, EasingMode.EaseOut,
            () =>
            {
                if (!IsCurrentPageTransition(generation)) return;
                StopPageClocks();
                NormalizePageSurface();
                var focus = pageTransitionFocusTarget;
                pageTransitionFocusTarget = null;
                if (IsVisible && focus?.IsVisible == true &&
                    (CurrentPageRoot.IsAncestorOf(focus) || focus == PageBackButton)) focus.Focus();
            });
        AnimatePage(pageTransitionShift, TranslateTransform.XProperty, offset, 0, 140, EasingMode.EaseOut);
    }

    private bool IsCurrentPageTransition(long generation) => generation == pageTransitionGeneration &&
        IsPageTransitionActive && !pageTransitionClosed;

    private void CommitPage(MenuSection section)
    {
        var changed = presentedMenuPage != section;
        presentedMenuPage = section;
        var appearance = section == MenuSection.Appearance;
        var preferences = section == MenuSection.Preferences;
        MenuItems.Visibility = section == MenuSection.None ? Visibility.Visible : Visibility.Collapsed;
        PageHeader.Visibility = section == MenuSection.None ? Visibility.Collapsed : Visibility.Visible;
        AppearanceReveal.Visibility = AppearanceRoot.Visibility = appearance ? Visibility.Visible : Visibility.Collapsed;
        PreferencesReveal.Visibility = PreferencesRoot.Visibility = preferences ? Visibility.Visible : Visibility.Collapsed;
        AppearanceReveal.IsHitTestVisible = appearance;
        PreferencesReveal.IsHitTestVisible = preferences;
        KeyboardNavigation.SetTabNavigation(AppearanceReveal, appearance ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.None);
        KeyboardNavigation.SetTabNavigation(PreferencesReveal, preferences ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.None);
        PageTitleText.Text = appearance ? "外观与互动" : "功能设置";
        PrimaryHeader.Visibility = section == MenuSection.None ? Visibility.Visible : Visibility.Collapsed;
        PlacementUnavailable = scrollHeightBudget < MinimumUsablePageHeightDip;
        ApplyScrollHeightLimit();
        if (PlacementUnavailable && IsVisible) Hide();
        if (changed) MenuScroller.ScrollToTop();
        ResetMenuScrollbar();
        RepositionMenu();
    }

    private void FocusWhenPageReady(Button? target)
    {
        if (!IsVisible || target is null) return;
        if (IsPageTransitionActive) pageTransitionFocusTarget = target;
        else if (target.IsVisible) target.Focus();
    }

    private void CancelPageTransition()
    {
        ++pageTransitionGeneration;
        StopPageClocks();
        pageTransitionFocusTarget = null;
        CommitPage(requestedMenuPage);
        NormalizePageSurface();
    }

    private void StopPageClocks()
    {
        MenuContent.BeginAnimation(OpacityProperty, null);
        pageTransitionShift.BeginAnimation(TranslateTransform.XProperty, null);
    }

    private void NormalizePageSurface()
    {
        IsPageTransitionActive = false;
        MenuContent.Opacity = 1;
        pageTransitionShift.X = 0;
        MenuContent.IsHitTestVisible = true;
        MenuContent.IsEnabled = true;
    }

    private static void AnimatePage(DependencyObject target, DependencyProperty property, double from, double to,
        int milliseconds, EasingMode easing, Action? completed = null)
    {
        target.SetValue(property, to);
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = easing },
            FillBehavior = FillBehavior.Stop,
        };
        if (completed is not null) animation.Completed += (_, _) => completed();
        if (target is UIElement element) element.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        else if (target is Animatable animatable) animatable.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
