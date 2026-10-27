using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CodexQuotaTaskbar.Host.UI;

public partial class QuotaCapsuleWindow
{
    internal const int ExtensionExpandMilliseconds = 200;
    internal const int ExtensionCollapseMilliseconds = 160;
    private static readonly DependencyProperty TopExtensionHeightProperty = DependencyProperty.Register(
        "TopExtensionHeight", typeof(double), typeof(QuotaCapsuleWindow),
        new PropertyMetadata(0d, OnTopExtensionHeightChanged), IsValidExtensionHeight);
    private static readonly DependencyProperty ResetExtensionHeightProperty = DependencyProperty.Register(
        "ResetExtensionHeight", typeof(double), typeof(QuotaCapsuleWindow),
        new PropertyMetadata(0d, OnResetExtensionHeightChanged), IsValidExtensionHeight);
    private double topExtensionTargetHeight;
    private double resetExtensionTargetHeight;
    private int topExtensionGeneration, resetExtensionGeneration, extensionMotionGeneration;
    private bool topExtensionAnimating, resetExtensionAnimating, extensionMotionClosed;
    private int extensionMutationDepth;
    private long extensionMotionLayoutCount;
    private bool extensionLayoutApplying, extensionLayoutRequested, extensionLayoutForced;
    private bool extensionRenderAttached, extensionRenderCommit;
    private TimeSpan? extensionRenderTime;
    private double appliedTopExtensionHeight, appliedResetExtensionHeight;
    private readonly RectangleGeometry resetRevealClip = new();
    // A coordinated owner commits Top and size together through its native
    // placement callback. An independent Height setter would resize downward
    // first and expose a separate real frame before the matching Top change.
    internal bool UsesCoordinatedPlacement { get; set; }
    private void RefreshStandaloneWindowHeight()
    {
        if (CaptureOccluded) return;
        if (!UsesCoordinatedPlacement || !IsVisible) Height = DesiredHeight;
    }

    internal bool TopExtensionAnimating => topExtensionAnimating;
    internal bool ResetScheduleAnimating => resetExtensionAnimating;
    internal int ExtensionMotionGeneration => extensionMotionGeneration;
    internal long ExtensionMotionLayoutCount => extensionMotionLayoutCount;
    private bool CanAnimateExtensions => IsLoaded && IsVisible && !extensionMotionClosed
        && !bongoWindowClosing && !companionReduced && SystemParameters.ClientAreaAnimation;

    private static bool IsValidExtensionHeight(object value) => value is double number
        && double.IsFinite(number) && number is >= 0 and <= 401;

    private void InitializeExtensionMotion()
    {
        ResetScheduleReveal.Clip = resetRevealClip;
        ResetScheduleReveal.SizeChanged += (_, _) => UpdateResetRevealClip();
    }

    internal static DoubleAnimation CreateExtensionHeightAnimation(double from, double to)
    {
        if (!double.IsFinite(from) || !double.IsFinite(to) || from is < 0 or > 401 || to is < 0 or > 401)
            throw new ArgumentOutOfRangeException(nameof(to));
        var opening = to > from;
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(
            opening ? ExtensionExpandMilliseconds : ExtensionCollapseMilliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = opening ? EasingMode.EaseOut : EasingMode.EaseIn },
        };
        // Let this short-lived WPF clock follow the compositor's display
        // cadence. A fixed 60Hz cap visibly steps on higher-refresh monitors;
        // no extra timer, permanent render loop or duration change is needed.
        return animation;
    }

    private static void OnTopExtensionHeightChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((QuotaCapsuleWindow)sender).RequestExtensionLayout();

    private static void OnResetExtensionHeightChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((QuotaCapsuleWindow)sender).RequestExtensionLayout();

    private void UpdateExtensionRenderSubscription()
    {
        var wanted = CanAnimateExtensions && (topExtensionAnimating || resetExtensionAnimating);
        if (wanted == extensionRenderAttached) return;
        extensionRenderAttached = wanted;
        extensionRenderTime = null;
        if (wanted) CompositionTarget.Rendering += OnExtensionRendering;
        else CompositionTarget.Rendering -= OnExtensionRendering;
    }

    private void OnExtensionRendering(object? sender, EventArgs args)
    {
        if (args is RenderingEventArgs frame)
        {
            if (extensionRenderTime == frame.RenderingTime) return;
            extensionRenderTime = frame.RenderingTime;
        }
        if (!CanAnimateExtensions) { FinishExtensionTransitions(); UpdateExtensionRenderSubscription(); return; }
        extensionRenderCommit = true;
        try { RequestExtensionLayout(); }
        finally { extensionRenderCommit = false; }
    }

    private void ApplyResetRevealHeight(double height)
    {
        ResetScheduleReveal.Height = height;
        ResetScheduleReveal.Visibility = height > .001 || resetExtensionTargetHeight > 0
            ? Visibility.Visible : Visibility.Collapsed;
        if (height <= .001 && resetExtensionTargetHeight == 0) ResetScheduleRow.Visibility = Visibility.Collapsed;
        UpdateResetRevealClip();
    }

    private void UpdateResetRevealClip()
    {
        var height = Math.Max(0, ResetScheduleHeight);
        // The already accepted divider is translated 4 DIP above its row. Keep
        // that exact final position, but show nothing when the reveal is zero.
        resetRevealClip.Rect = height <= .001
            ? new Rect(0, 0, Math.Max(0, ResetScheduleReveal.ActualWidth), 0)
            : new Rect(0, -4, Math.Max(0, ResetScheduleReveal.ActualWidth), height + 4);
    }

    private bool RetargetTopExtension(double target, bool animate)
    {
        var current = (double)GetValue(TopExtensionHeightProperty);
        if (Math.Abs(topExtensionTargetHeight - target) < .001
            && (animate || !topExtensionAnimating) && (topExtensionAnimating || Math.Abs(current - target) < .001))
            return false;
        var generation = ++topExtensionGeneration;
        extensionMotionGeneration++;
        extensionMutationDepth++;
        try
        {
            topExtensionTargetHeight = target;
            topExtensionAnimating = animate && CanAnimateExtensions && Math.Abs(current - target) > .01;
            // Preserve the current displayed value before replacing a clock;
            // there is no one-frame flash through its old base/target value.
            SetValue(TopExtensionHeightProperty, current);
            BeginAnimation(TopExtensionHeightProperty, null);
            if (!topExtensionAnimating)
            {
                SetValue(TopExtensionHeightProperty, target);
                if (target == 0) DetachDotTopContent();
            }
            else
            {
                var animation = CreateExtensionHeightAnimation(current, target);
                animation.Completed += (_, _) =>
                {
                    if (generation != topExtensionGeneration || extensionMotionClosed) return;
                    extensionMutationDepth++;
                    try
                    {
                        topExtensionAnimating = false;
                        SetValue(TopExtensionHeightProperty, target);
                        BeginAnimation(TopExtensionHeightProperty, null);
                        if (target == 0) DetachDotTopContent();
                    }
                    finally { extensionMutationDepth--; }
                    RequestExtensionLayout(immediate: true);
                };
                BeginAnimation(TopExtensionHeightProperty, animation);
            }
        }
        finally { extensionMutationDepth--; }
        RequestExtensionLayout(immediate: true);
        return true;
    }

    private void RetargetResetExtension(double target, bool animate = true)
    {
        if (Math.Abs(resetExtensionTargetHeight - target) < .001 && (animate || !resetExtensionAnimating)) return;
        var current = (double)GetValue(ResetExtensionHeightProperty);
        var generation = ++resetExtensionGeneration;
        extensionMotionGeneration++;
        extensionMutationDepth++;
        try
        {
            resetExtensionTargetHeight = target;
            resetExtensionAnimating = animate && CanAnimateExtensions && Math.Abs(current - target) > .01;
            SetValue(ResetExtensionHeightProperty, current);
            BeginAnimation(ResetExtensionHeightProperty, null);
            if (target > 0) ResetScheduleRow.Visibility = Visibility.Visible;
            if (!resetExtensionAnimating) SetValue(ResetExtensionHeightProperty, target);
            else
            {
                var animation = CreateExtensionHeightAnimation(current, target);
                animation.Completed += (_, _) =>
                {
                    if (generation != resetExtensionGeneration || extensionMotionClosed) return;
                    extensionMutationDepth++;
                    try
                    {
                        resetExtensionAnimating = false;
                        SetValue(ResetExtensionHeightProperty, target);
                        BeginAnimation(ResetExtensionHeightProperty, null);
                    }
                    finally { extensionMutationDepth--; }
                    RequestExtensionLayout(immediate: true);
                };
                BeginAnimation(ResetExtensionHeightProperty, animation);
            }
        }
        finally { extensionMutationDepth--; }
        RequestExtensionLayout(immediate: true);
    }

    // While clocks run, pull both axes once immediately before WPF renders.
    // Their viewports and HWND geometry are still committed synchronously in
    // that render tick, never through a later dispatcher job. The observer
    // exists only during these short transitions, not while the card is idle.
    private void RequestExtensionLayout(bool immediate = false)
    {
        if (extensionMotionClosed || extensionMutationDepth > 0) return;
        UpdateExtensionRenderSubscription();
        if (!immediate && extensionRenderAttached && !extensionRenderCommit) return;
        extensionLayoutForced |= immediate;
        if (extensionLayoutApplying)
        {
            extensionLayoutRequested = true;
            return;
        }
        do
        {
            extensionLayoutRequested = false;
            extensionLayoutApplying = true;
            var force = extensionLayoutForced;
            extensionLayoutForced = false;
            try { ApplyExtensionLayout(force); }
            finally { extensionLayoutApplying = false; }
        }
        while (extensionLayoutRequested && !extensionMotionClosed);
        UpdateExtensionRenderSubscription();
    }

    private void ApplyExtensionLayout(bool force)
    {
        var top = (double)GetValue(TopExtensionHeightProperty);
        var reset = (double)GetValue(ResetExtensionHeightProperty);
        if (!force && Math.Abs(top - appliedTopExtensionHeight) < .00001
            && Math.Abs(reset - appliedResetExtensionHeight) < .00001) return;
        appliedTopExtensionHeight = top;
        appliedResetExtensionHeight = reset;
        if (dotTopHost is not null) dotTopHost.Height = top;
        if (dotTopSurface is not null) dotTopSurface.RowDefinitions[0].Height = new GridLength(top);
        ApplyResetRevealHeight(reset);
        RefreshCompanionResetNotice();
        extensionMotionLayoutCount++;
        RefreshBongoBodyLayout();
        RefreshStandaloneWindowHeight();
        // Spacing already reanchors the marker; avoid repeating its appearance
        // and tooltip work on every top/footer animation clock callback.
        UpdateCompanionSpacing();
        PresentationChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void FinishExtensionTransitions()
    {
        VerifyAccess();
        if (extensionMotionClosed) return;
        if (!topExtensionAnimating && !resetExtensionAnimating) return;
        extensionMutationDepth++;
        try
        {
            if (topExtensionAnimating)
            {
                topExtensionGeneration++; extensionMotionGeneration++; topExtensionAnimating = false;
                SetValue(TopExtensionHeightProperty, topExtensionTargetHeight);
                BeginAnimation(TopExtensionHeightProperty, null);
                if (topExtensionTargetHeight == 0) DetachDotTopContent();
            }
            if (resetExtensionAnimating)
            {
                resetExtensionGeneration++; extensionMotionGeneration++; resetExtensionAnimating = false;
                SetValue(ResetExtensionHeightProperty, resetExtensionTargetHeight);
                BeginAnimation(ResetExtensionHeightProperty, null);
            }
        }
        finally { extensionMutationDepth--; }
        RequestExtensionLayout(immediate: true);
    }

    private void CloseExtensionMotion()
    {
        FinishExtensionTransitions();
        extensionMotionClosed = true;
        UpdateExtensionRenderSubscription();
        extensionLayoutRequested = false;
        extensionLayoutForced = false;
    }
}
