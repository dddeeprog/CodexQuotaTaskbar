using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CodexQuotaTaskbar.Host.Platform;
using Button = System.Windows.Controls.Button;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace CodexQuotaTaskbar.Host.UI;

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

    private void InitializeMenuInteraction()
    {
        SizeChanged += (_, _) => RepositionMenu();
        MenuItems.SizeChanged += (_, _) => MoveHighlight(highlightedButton, false);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Up or Key.Down or Key.Right)) return;
            if (MaterialTransparencySlider.IsKeyboardFocusWithin) return;
            var buttons = FindVisualChildren<Button>(MenuItems).Where(IsAvailable).ToArray();
            var focused = buttons.FirstOrDefault(b => b.IsKeyboardFocusWithin);
            if (e.Key == Key.Right)
            {
                if (focused != AppearanceButton && focused != PreferencesButton) return;
                var appearance = focused == AppearanceButton;
                if (!(appearance ? appearanceOpen : preferencesOpen)) ToggleSubmenu(appearance);
                else (appearance ? ClassicStyleButton : TrayIconButton).Focus();
            }
            else if (buttons.Length > 0)
            {
                var index = Array.IndexOf(buttons, focused);
                index = (index + (e.Key == Key.Down ? 1 : -1) + buttons.Length) % buttons.Length;
                buttons[index].Focus();
                buttons[index].BringIntoView();
            }
            e.Handled = true;
        };
    }

    private bool IsAvailable(Button button) => button.IsVisible && button.IsEnabled
        && (appearanceOpen || !AppearanceRoot.IsAncestorOf(button))
        && (preferencesOpen || !PreferencesRoot.IsAncestorOf(button));

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
        if (button is null || MenuContent is null || !IsAvailable(button) || button.ActualWidth <= 0) return;
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
        var open = !(appearance ? appearanceOpen : preferencesOpen);
        SetSection(!appearance, false, true);
        SetSection(appearance, open, true);
    }

    private void SetSection(bool appearance, bool open, bool animate)
    {
        if (appearance) appearanceOpen = open;
        else preferencesOpen = open;
        var reveal = appearance ? AppearanceReveal : PreferencesReveal;
        var content = appearance ? AppearanceRoot : PreferencesRoot;
        var arrow = appearance ? AppearanceChevronRotation : PreferencesChevronRotation;
        var button = appearance ? AppearanceButton : PreferencesButton;
        content.Measure(new System.Windows.Size(Math.Max(1, MenuItems.ActualWidth > 0 ? MenuItems.ActualWidth : 239), double.PositiveInfinity));
        var target = open ? content.DesiredSize.Height : 0;
        var duration = animate && IsVisible ? 180 : 0;
        reveal.IsHitTestVisible = open;
        // Remove closed children from tab navigation immediately, even during the exit.
        KeyboardNavigation.SetTabNavigation(reveal, open ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.None);
        System.Windows.Automation.AutomationProperties.SetItemStatus(button, open ? "已展开" : "已折叠");
        Animate(reveal, HeightProperty, reveal.Height, target, duration);
        Animate(content, OpacityProperty, content.Opacity, open ? 1 : 0, duration);
        Animate(arrow, RotateTransform.AngleProperty, arrow.Angle, open ? 90 : 0, duration);
        if (!open && highlightedButton is { } selected && content.IsAncestorOf(selected))
            MoveHighlight(button, false);
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
        if (!IsVisible || !IsLoaded || placingMenu) return;
        placingMenu = true;
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var dpi = Math.Max(96u, NativeMethods.GetDpiForWindow(handle)) / 96d;
            var work = System.Windows.Forms.Screen.FromPoint(menuAnchor).WorkingArea;
            var size = new System.Drawing.Size((int)Math.Ceiling(ActualWidth * dpi), (int)Math.Ceiling(ActualHeight * dpi));
            var bounds = CalculateAnchoredBounds(work, menuAnchor, size, menuOpensAbove, menuAnchorGap, menuCenteredOnIsland);
            NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                NativeMethods.SwpShowWindow | NativeMethods.SwpNoActivate);
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
