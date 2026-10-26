using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CodexQuotaTaskbar.Core.Overlay;

namespace CodexQuotaTaskbar.Host.UI;

public partial class TrayMenuWindow
{
    internal const int PopupEntranceAnimationMilliseconds = 220;
    internal const int PopupExitAnimationMilliseconds = 130;
    internal const double PopupMotionOffsetDip = 10;
    internal static EasingMode PopupExitEasingMode => EasingMode.EaseOut;
    private readonly ScaleTransform menuPopupScale = new(1, 1);
    private readonly TranslateTransform menuPopupTranslate = new();
    private DispatcherOperation? popupEntranceOperation;
    private long? popupPendingEntranceGeneration;
    private bool popupMotionClosed;
    private bool preparingPopup;
    private (double X, double Y) popupEntranceOffset = (0, PopupMotionOffsetDip);
    internal bool IsPopupClosing { get; private set; }
    internal bool IsPopupTransitionActive { get; private set; }
    internal long PopupTransitionGeneration { get; private set; }

    internal static bool PopupMotionEnabled(bool visible, bool loaded, bool systemMotion, bool reduceMotion) =>
        visible && loaded && systemMotion && !reduceMotion;
    private bool CanRequestPopupMotion => !popupMotionClosed && IsVisible &&
        SystemParameters.ClientAreaAnimation && !settings.ReduceCompanionMotion;
    private bool CanAnimatePopup => CanRequestPopupMotion && IsLoaded;

    private void InitializePopupMotion()
    {
        MenuShell.RenderTransformOrigin = new System.Windows.Point(.5, .5);
        var transforms = new TransformGroup();
        transforms.Children.Add(menuPopupScale);
        transforms.Children.Add(menuPopupTranslate);
        MenuShell.RenderTransform = transforms;
        RegisterName("MenuPopupScale", menuPopupScale);
        RegisterName("MenuPopupTranslate", menuPopupTranslate);
    }

    private long PreparePopupOpen()
    {
        var continuing = IsPopupTransitionActive || IsPopupClosing;
        var opacity = MenuShell.Opacity;
        var scaleX = menuPopupScale.ScaleX;
        var scaleY = menuPopupScale.ScaleY;
        var x = menuPopupTranslate.X;
        var y = menuPopupTranslate.Y;
        StopPopupClocks();
        ++PopupTransitionGeneration;
        IsPopupClosing = false;
        if (continuing)
        {
            MenuShell.Opacity = opacity;
            menuPopupScale.ScaleX = scaleX; menuPopupScale.ScaleY = scaleY;
            menuPopupTranslate.X = x; menuPopupTranslate.Y = y;
            IsPopupTransitionActive = true;
        }
        else NormalizePopupSurface();
        return PopupTransitionGeneration;
    }

    private bool IsCurrentPopupOpenRequest(long generation) => !popupMotionClosed &&
        generation == PopupTransitionGeneration && !IsPopupClosing;

    private bool IsCurrentPopupOpen(long generation) => IsCurrentPopupOpenRequest(generation) && IsVisible;

    internal void HidePopupImmediately()
    {
        if (IsVisible) Hide();
        else CancelPopupMotion();
    }

    internal void PlayPopupEntrance((double X, double Y) offset)
    {
        if (popupMotionClosed) return;
        var continuing = IsPopupTransitionActive || IsPopupClosing;
        var opacity = Math.Clamp(MenuShell.Opacity, 0, 1);
        var scaleX = menuPopupScale.ScaleX;
        var scaleY = menuPopupScale.ScaleY;
        var x = menuPopupTranslate.X;
        var y = menuPopupTranslate.Y;
        StopPopupClocks();
        var generation = ++PopupTransitionGeneration;
        IsPopupClosing = false;
        popupEntranceOffset = (Math.Clamp(offset.X, -PopupMotionOffsetDip, PopupMotionOffsetDip),
            Math.Clamp(offset.Y, -PopupMotionOffsetDip, PopupMotionOffsetDip));
        MenuShell.IsEnabled = MenuShell.IsHitTestVisible = true;
        if (!CanRequestPopupMotion) { NormalizePopupSurface(); return; }
        MenuShell.Opacity = continuing ? opacity : 0;
        menuPopupScale.ScaleX = continuing ? scaleX : .97;
        menuPopupScale.ScaleY = continuing ? scaleY : .97;
        menuPopupTranslate.X = continuing ? x : popupEntranceOffset.X;
        menuPopupTranslate.Y = continuing ? y : popupEntranceOffset.Y;
        IsPopupTransitionActive = true;
        popupPendingEntranceGeneration = generation;
        StartPendingPopupEntrance();
    }

    private void StartPendingPopupEntrance()
    {
        if (!IsLoaded || popupPendingEntranceGeneration is not { } generation) return;
        popupPendingEntranceGeneration = null;
        if (popupMotionClosed || generation != PopupTransitionGeneration || IsPopupClosing) return;
        popupEntranceOperation = Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            popupEntranceOperation = null;
            if (popupMotionClosed || generation != PopupTransitionGeneration || IsPopupClosing) return;
            if (!CanAnimatePopup) { NormalizePopupSurface(); return; }
            AnimatePopup(MenuShell, OpacityProperty, MenuShell.Opacity, 1, PopupEntranceAnimationMilliseconds, () =>
            {
                if (popupMotionClosed || generation != PopupTransitionGeneration || IsPopupClosing) return;
                StopPopupClocks();
                NormalizePopupSurface();
            });
            AnimatePopup(menuPopupScale, ScaleTransform.ScaleXProperty, menuPopupScale.ScaleX, 1, PopupEntranceAnimationMilliseconds);
            AnimatePopup(menuPopupScale, ScaleTransform.ScaleYProperty, menuPopupScale.ScaleY, 1, PopupEntranceAnimationMilliseconds);
            AnimatePopup(menuPopupTranslate, TranslateTransform.XProperty, menuPopupTranslate.X, 0, PopupEntranceAnimationMilliseconds);
            AnimatePopup(menuPopupTranslate, TranslateTransform.YProperty, menuPopupTranslate.Y, 0, PopupEntranceAnimationMilliseconds);
        }));
    }

    internal void RequestHide()
    {
        if (popupMotionClosed || IsPopupClosing) return;
        if (!IsVisible) { CancelPopupMotion(); return; }
        var opacity = Math.Clamp(MenuShell.Opacity, 0, 1);
        var scaleX = menuPopupScale.ScaleX;
        var scaleY = menuPopupScale.ScaleY;
        var x = menuPopupTranslate.X;
        var y = menuPopupTranslate.Y;
        StopPopupClocks();
        var generation = ++PopupTransitionGeneration;
        IsPopupClosing = true;
        if (!CanAnimatePopup) { Hide(); return; }
        IsPopupTransitionActive = true;
        // The old immediate Hide could not execute another item. Keep its input
        // boundary during the finite outro, without changing HWND focus/activation.
        MenuShell.IsEnabled = MenuShell.IsHitTestVisible = false;
        AnimatePopup(MenuShell, OpacityProperty, opacity, 0, PopupExitAnimationMilliseconds, () =>
        {
            if (!popupMotionClosed && generation == PopupTransitionGeneration && IsPopupClosing) Hide();
        });
        AnimatePopup(menuPopupScale, ScaleTransform.ScaleXProperty, scaleX, .98, PopupExitAnimationMilliseconds);
        AnimatePopup(menuPopupScale, ScaleTransform.ScaleYProperty, scaleY, .98, PopupExitAnimationMilliseconds);
        AnimatePopup(menuPopupTranslate, TranslateTransform.XProperty, x, popupEntranceOffset.X * .7, PopupExitAnimationMilliseconds);
        AnimatePopup(menuPopupTranslate, TranslateTransform.YProperty, y, popupEntranceOffset.Y * .7, PopupExitAnimationMilliseconds);
    }

    private void RefreshPopupMotionPolicy()
    {
        if (popupMotionClosed || CanRequestPopupMotion) return;
        if (IsPopupClosing && IsVisible) { Hide(); return; }
        CancelPopupMotion();
    }

    private void CancelPopupMotion()
    {
        ++PopupTransitionGeneration;
        StopPopupClocks();
        NormalizePopupSurface();
    }

    private void StopPopupClocks()
    {
        if (popupEntranceOperation?.Status == DispatcherOperationStatus.Pending) popupEntranceOperation.Abort();
        popupEntranceOperation = null;
        popupPendingEntranceGeneration = null;
        MenuShell.BeginAnimation(OpacityProperty, null);
        menuPopupScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        menuPopupScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        menuPopupTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        menuPopupTranslate.BeginAnimation(TranslateTransform.YProperty, null);
    }

    private void NormalizePopupSurface()
    {
        IsPopupClosing = IsPopupTransitionActive = false;
        MenuShell.Opacity = 1;
        menuPopupScale.ScaleX = menuPopupScale.ScaleY = 1;
        menuPopupTranslate.X = menuPopupTranslate.Y = 0;
        MenuShell.IsEnabled = MenuShell.IsHitTestVisible = true;
    }

    private void DisposePopupMotion()
    {
        popupMotionClosed = true;
        CancelPopupMotion();
    }

    private void ResetPrimaryPageForPopup()
    {
        if (CurrentSection != MenuSection.None) FlushMaterialTransparencyChange();
        appearanceOpen = preferencesOpen = false;
        requestedMenuPage = MenuSection.None;
        UpdateSectionStatus();
        CancelPageTransition();
    }

    private void SetPopupEntranceAnchor(ScreenRect bounds)
    {
        var inset = (int)Math.Ceiling(MenuShell.Margin.Left * menuIslandDpi / 96d);
        var visibleMenu = new ScreenRect(bounds.Left + inset, bounds.Top + inset,
            bounds.Right - inset, bounds.Bottom - inset);
        popupEntranceOffset = menuIslandBounds is { } island
            ? CalculatePopupEntranceOffset(island, visibleMenu)
            : (0, menuOpensAbove ? PopupMotionOffsetDip : -PopupMotionOffsetDip);
    }

    internal static (double X, double Y) CalculatePopupEntranceOffset(ScreenRect anchor, ScreenRect menu) =>
        menu.Bottom <= anchor.Top ? (0, PopupMotionOffsetDip)
        : menu.Top >= anchor.Bottom ? (0, -PopupMotionOffsetDip)
        : menu.Right <= anchor.Left ? (PopupMotionOffsetDip, 0) : (-PopupMotionOffsetDip, 0);

    private static void AnimatePopup(DependencyObject target, DependencyProperty property, double from, double to,
        int milliseconds, Action? completed = null)
    {
        target.SetValue(property, to);
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        if (completed is not null) animation.Completed += (_, _) => completed();
        if (target is UIElement element) element.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        else if (target is Animatable animatable) animatable.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
