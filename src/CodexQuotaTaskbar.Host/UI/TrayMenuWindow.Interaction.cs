using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CodexQuotaTaskbar.Host.Platform;
using Button = System.Windows.Controls.Button;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace CodexQuotaTaskbar.Host.UI;

internal enum MenuSection { None, Appearance, Preferences }

public partial class TrayMenuWindow
{
    private bool appearanceOpen;
    private bool preferencesOpen;
    private Button? highlightedButton;
    private System.Drawing.Point menuAnchor;
    private int menuAnchorGap = 8;
    private bool menuOpensAbove;
    private bool menuCenteredOnIsland;
    private bool placingMenu;

    internal const double MenuPageHeightLimitDip = 460;
    internal MenuSection CurrentSection => appearanceOpen ? MenuSection.Appearance
        : preferencesOpen ? MenuSection.Preferences : MenuSection.None;
    internal FrameworkElement CurrentPageRoot => CurrentSection switch
    {
        MenuSection.Appearance => AppearanceRoot,
        MenuSection.Preferences => PreferencesRoot,
        _ => MenuItems,
    };

    private void InitializeMenuInteraction()
    {
        MenuContent.RenderTransform = pageTransitionShift;
        MenuContent.ClipToBounds = true;
        InitializeMenuScrolling();
        SizeChanged += (_, _) => { menuTrace?.Record(MenuTraceKind.SizeChanged); RepositionMenu(); };
        MenuContent.SizeChanged += (_, _) => MoveHighlight(highlightedButton, false);
        PreviewKeyDown += (_, e) => e.Handled = HandleMenuKey(e.Key);
    }

    internal bool HandleMenuKey(Key key)
    {
        if (IsPopupClosing) return true;
        if (key is Key.Escape or Key.Left)
        {
            if (key == Key.Left && MaterialTransparencySlider.IsKeyboardFocusWithin) return false;
            if (CurrentSection != MenuSection.None)
            {
                // A horizontal selector keeps its ordinary left/right navigation.
                if (key == Key.Left && Keyboard.FocusedElement is Button choice
                    && choice.Parent is System.Windows.Controls.Primitives.UniformGrid choices)
                {
                    var index = choices.Children.IndexOf(choice);
                    if (index > 0 && choices.Children[index - 1] is Button previous && previous.IsEnabled)
                    {
                        if (IsVisible) previous.Focus();
                        return true;
                    }
                }
                var target = appearanceOpen ? AppearanceButton : PreferencesButton;
                ReturnToPrimary();
                FocusWhenPageReady(target);
                return true;
            }
            if (key == Key.Escape) { if (IsVisible) RequestHide(); return true; }
            return false;
        }
        if (key is not (Key.Up or Key.Down or Key.Right)) return false;
        if (IsPageTransitionActive) return true;
        if (MaterialTransparencySlider.IsKeyboardFocusWithin) return false;
        var buttons = ActiveButtons().ToArray();
        var focused = buttons.FirstOrDefault(b => b.IsKeyboardFocusWithin);
        if (key == Key.Right)
        {
            if (CurrentSection == MenuSection.None)
            {
                if (focused != AppearanceButton && focused != PreferencesButton) return false;
                OpenSection(focused == AppearanceButton ? MenuSection.Appearance : MenuSection.Preferences);
                FocusWhenPageReady(focused == AppearanceButton ? ClassicStyleButton : DotRealtimeButton);
                return true;
            }
            if (focused?.Parent is System.Windows.Controls.Primitives.UniformGrid choices)
            {
                var index = choices.Children.IndexOf(focused);
                if (index + 1 < choices.Children.Count && choices.Children[index + 1] is Button next && next.IsEnabled)
                {
                    if (IsVisible) next.Focus();
                    return true;
                }
            }
            return false;
        }
        if (buttons.Length == 0) return false;
        var current = Array.IndexOf(buttons, focused);
        current = (current + (key == Key.Down ? 1 : -1) + buttons.Length) % buttons.Length;
        if (IsVisible)
        {
            buttons[current].Focus();
            buttons[current].BringIntoView();
        }
        return true;
    }

    private IEnumerable<Button> ActiveButtons()
    {
        if (CurrentSection != MenuSection.None && PageBackButton.IsEnabled) yield return PageBackButton;
        foreach (var button in FindVisualChildren<Button>(CurrentPageRoot).Where(IsAvailable)) yield return button;
    }

    private bool IsAvailable(Button button)
    {
        if (!button.IsEnabled || button.Visibility != Visibility.Visible) return false;
        if (!CurrentPageRoot.IsAncestorOf(button) && button != PageBackButton) return false;
        for (DependencyObject? node = button; node is not null && node != this; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement element && element.Visibility != Visibility.Visible) return false;
        return true;
    }

    private void OnMenuItemMouseEnter(object sender, MouseEventArgs e) => MoveHighlight(sender as Button, true);
    private void OnMenuItemFocus(object sender, KeyboardFocusChangedEventArgs e) => MoveHighlight(sender as Button, true);
    private void OnMenuMouseLeave(object sender, MouseEventArgs e)
    {
        if (highlightedButton?.IsKeyboardFocusWithin == true) return;
        Animate(MenuHighlight, OpacityProperty, MenuHighlight.Opacity, 0, 90);
        highlightedButton = null;
    }

    private void MoveHighlight(Button? button, bool animate)
    {
        if (button is null || MenuContent is null || !CurrentPageRoot.IsAncestorOf(button) || !IsAvailable(button) || button.ActualWidth <= 0) return;
        var point = button.TranslatePoint(new System.Windows.Point(), MenuContent);
        var duration = animate && highlightedButton is not null ? 130 : 0;
        highlightedButton = button;
        Animate(MenuHighlightTranslate, TranslateTransform.XProperty, MenuHighlightTranslate.X, point.X, duration);
        Animate(MenuHighlightTranslate, TranslateTransform.YProperty, MenuHighlightTranslate.Y, point.Y, duration);
        Animate(MenuHighlight, WidthProperty, MenuHighlight.Width, button.ActualWidth, duration);
        Animate(MenuHighlight, HeightProperty, MenuHighlight.Height, button.ActualHeight, duration);
        Animate(MenuHighlight, OpacityProperty, MenuHighlight.Opacity, 1, animate ? 90 : 0);
    }

    private void ToggleSubmenu(bool appearance)
    {
        var next = appearance ? MenuSection.Appearance : MenuSection.Preferences;
        if (CurrentSection == next) ReturnToPrimary();
        else OpenSection(next);
    }

    internal void OpenSection(MenuSection section)
    {
        if (section == MenuSection.None) { ReturnToPrimary(); return; }
        if (section is not (MenuSection.Appearance or MenuSection.Preferences)) throw new ArgumentOutOfRangeException(nameof(section));
        NavigatePage(section);
    }

    internal void ReturnToPrimary()
    {
        if (CurrentSection != MenuSection.None) FlushMaterialTransparencyChange();
        NavigatePage(MenuSection.None);
    }

    private void UpdateSectionStatus()
    {
        System.Windows.Automation.AutomationProperties.SetItemStatus(AppearanceButton, appearanceOpen ? "已展开" : "已折叠");
        System.Windows.Automation.AutomationProperties.SetItemStatus(PreferencesButton, preferencesOpen ? "已展开" : "已折叠");
        MenuHighlight.BeginAnimation(OpacityProperty, null);
        MenuHighlight.Opacity = 0;
        highlightedButton = null;
    }

    private void UpdatePageConstraints()
    {
        ApplyScrollHeightLimit();
    }

    // Set the base value first; finite clocks stop at completion. Retargeting reads the current
    // presentation value, so fast reversals never restart from a stale logical endpoint.
    private static void Animate(DependencyObject target, DependencyProperty property, double from, double to, int milliseconds)
    {
        var motion = milliseconds > 0 && SystemParameters.ClientAreaAnimation;
        target.SetValue(property, to);
        var animation = motion ? new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        } : null;
        if (target is UIElement element) element.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        else if (target is Animatable animatable) animatable.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void RepositionMenu()
    {
        if (!IsVisible || !IsLoaded || placingMenu || preparingPopup || IsPopupClosing) return;
        if (menuIslandBounds is not null)
        {
            RepositionIslandMenu(show: false);
            return;
        }
        placingMenu = true;
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var dpi = Math.Max(96u, NativeMethods.GetDpiForWindow(handle)) / 96d;
            var work = System.Windows.Forms.Screen.FromPoint(menuAnchor).WorkingArea;
            var size = new System.Drawing.Size((int)Math.Ceiling(ActualWidth * dpi), (int)Math.Ceiling(ActualHeight * dpi));
            var bounds = CalculateAnchoredBounds(work, menuAnchor, size, menuOpensAbove, menuAnchorGap, menuCenteredOnIsland);
            menuTrace?.Record(MenuTraceKind.PositionRequest, requested: new(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom, (uint)(dpi * 96)));
            NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                NativeMethods.SwpNoActivate);
            menuTrace?.Record(MenuTraceKind.PositionActual);
            PublishPlacement(new(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
        }
        finally { placingMenu = false; }
    }

    internal static System.Drawing.Rectangle CalculateAnchoredBounds(System.Drawing.Rectangle work,
        System.Drawing.Point anchor, System.Drawing.Size size, bool above, int gap = 8, bool centered = false)
    {
        var bounds = CalculateBounds(work, anchor, size, gap);
        var min = work.Top + 8;
        var max = Math.Max(min, work.Bottom - bounds.Height - 8);
        var left = centered ? Math.Clamp(anchor.X - bounds.Width / 2, work.Left + 8, work.Right - bounds.Width - 8) : bounds.X;
        return new(left, Math.Clamp(above ? anchor.Y - bounds.Height - gap : anchor.Y + gap, min, max),
            bounds.Width, bounds.Height);
    }
}
