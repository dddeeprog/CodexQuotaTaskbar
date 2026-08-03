using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Host.Platform;
using FrostedBlur = BlurredBackground.WPF.BlurredBackground;

namespace CodexQuotaTaskbar.Host.UI;

public partial class QuotaCapsuleWindow : Window
{
    internal const double WindowWidth = 306;
    internal const double WindowHeight = 92;
    internal const double BadgeShadowSafeInset = 12;
    internal const int BadgeAnimationMilliseconds = 180;
    internal const string GlassCaptureTargetName = "BackdropLayer";
    internal const double GlassBlurRadius = OverlayGlassMaterial.BlurRadius;
    internal const double GlassMerging = OverlayGlassMaterial.Merging;
    internal static EasingMode ExitEasingMode => EasingMode.EaseOut;
    private static readonly System.Windows.Media.Brush Cool = Freeze("#F2F2F7");
    private static readonly System.Windows.Media.Brush Amber = Freeze("#FFB84D");
    private static readonly System.Windows.Media.Brush Critical = Freeze("#FF5C6C");
    private static readonly System.Windows.Media.Brush Neutral = Freeze("#737378");
    private readonly System.Windows.Media.Brush normalLabelForeground;
    private readonly System.Windows.Media.Brush normalValueForeground;
    private readonly System.Windows.Media.Brush normalGlassBackground;
    private readonly System.Windows.Media.Brush normalBackdropBackground;
    private readonly System.Windows.Media.Brush normalBadgeBackground;
    private readonly System.Windows.Media.Brush normalBadgeBorderBrush;
    private ScreenRect physicalBounds;
    private bool blurReady;
    private ScreenRect dragOriginBounds;
    private int dragOriginX;
    private int dragOriginY;
    private bool pointerDown;
    private bool userDragging;
    private QuotaSnapshot quotaSnapshot = QuotaSnapshot.Unavailable("正在连接 Codex…");
    private CodexSessionsSnapshot sessionsSnapshot = CodexSessionsSnapshot.Empty;
    private int waitingCount;
    private int runningCount;
    private int completedCount;
    private int badgeAnimationGeneration;

    internal QuotaCapsuleWindow(string monitorId)
    {
        MonitorId = monitorId;
        InitializeComponent();
        normalLabelForeground = FirstLabel.Foreground;
        normalValueForeground = FirstValue.Foreground;
        normalGlassBackground = GlassBorder.Background;
        normalBackdropBackground = BackdropLayer.Background;
        normalBadgeBackground = SessionBadge.Background;
        normalBadgeBorderBrush = SessionBadge.BorderBrush;
        SourceInitialized += (_, _) => WindowInteropPolicy.AttachNoActivate(this);
        Loaded += (_, _) =>
        {
            blurReady = true;
            ApplySystemAppearance();
        };
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnMouseLeftButtonDown), true);
        AddHandler(Mouse.PreviewMouseMoveEvent, new System.Windows.Input.MouseEventHandler(OnMouseMove), true);
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnMouseLeftButtonUp), true);
        LostMouseCapture += (_, _) => ResetDragState();
        MouseRightButtonUp += (_, _) => ContextInvoked?.Invoke(this, EventArgs.Empty);
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        Closed += (_, _) => SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        ApplySystemAppearance();
    }

    internal string MonitorId { get; }
    internal ScreenRect PhysicalBounds => physicalBounds;
    internal bool IsUserDragging => userDragging;
    internal event EventHandler? PrimaryInvoked;
    internal event EventHandler? ContextInvoked;
    internal event Action<ScreenRect, ScreenRect>? UserMoved;

    internal void Apply(QuotaSnapshot snapshot)
    {
        quotaSnapshot = snapshot;
        Render();
    }

    internal void ApplySessions(CodexSessionsSnapshot snapshot)
    {
        sessionsSnapshot = snapshot;
        Render();
    }

    private void Render()
    {
        var snapshot = quotaSnapshot;
        var projection = QuotaCapsuleProjection.Create(snapshot);
        ApplyRow(projection.Rows[0], FirstLabel, FirstBar, FirstValue);
        var hasSecondRow = projection.Rows.Count > 1;
        System.Windows.Controls.Grid.SetRowSpan(FirstRow, hasSecondRow ? 1 : 2);
        SecondRow.Visibility = hasSecondRow ? Visibility.Visible : Visibility.Collapsed;
        if (hasSecondRow)
        {
            ApplyRow(projection.Rows[1], SecondLabel, SecondBar, SecondValue);
        }

        Opacity = snapshot.Availability == QuotaAvailability.Stale ? 0.78 : 1;
        ToolTip = null;
        UpdateSessionBadge();
        var activeSessions = sessionsSnapshot.ActiveCount;
        var rowSummary = string.Join("，", projection.Rows.Select(row => $"{row.Label} {row.Text}"));
        AutomationProperties.SetName(this,
            $"Codex 额度，{rowSummary}，待授权 {sessionsSnapshot.WaitingCount}，进行中 {sessionsSnapshot.RunningCount}，已完成 {sessionsSnapshot.CompletedCount}，{projection.StatusText}");
        ApplySystemAppearance();
    }

    internal static double NumberTransitionOffset(int previous, int current) =>
        current > previous ? 3 : current < previous ? -3 : 0;

    private void UpdateSessionBadge()
    {
        var nextWaiting = sessionsSnapshot.WaitingCount;
        var nextRunning = sessionsSnapshot.RunningCount;
        var nextCompleted = sessionsSnapshot.CompletedCount;
        var previousTotal = waitingCount + runningCount + completedCount;
        var nextTotal = nextWaiting + nextRunning + nextCompleted;
        if (nextWaiting == waitingCount && nextRunning == runningCount && nextCompleted == completedCount)
        {
            return;
        }
        var generation = ++badgeAnimationGeneration;

        UpdateStatusLight(WaitingLight, WaitingLightScale, WaitingLightText, WaitingTextScale, WaitingTextTranslate,
            waitingCount, nextWaiting, "待授权", generation, () => sessionsSnapshot.WaitingCount);
        UpdateStatusLight(RunningLight, RunningLightScale, RunningLightText, RunningTextScale, RunningTextTranslate,
            runningCount, nextRunning, "进行中", generation, () => sessionsSnapshot.RunningCount);
        UpdateStatusLight(CompletedLight, CompletedLightScale, CompletedLightText, CompletedTextScale, CompletedTextTranslate,
            completedCount, nextCompleted, "已完成", generation, () => sessionsSnapshot.CompletedCount);

        waitingCount = nextWaiting;
        runningCount = nextRunning;
        completedCount = nextCompleted;
        ToolTip = null;
        AutomationProperties.SetName(SessionBadge, $"任务状态，待授权 {nextWaiting}，进行中 {nextRunning}，已完成 {nextCompleted}");

        SessionBadge.BeginAnimation(OpacityProperty, null);
        SessionBadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        SessionBadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        SessionBadge.Opacity = 1;
        SessionBadgeScale.ScaleX = 1;
        SessionBadgeScale.ScaleY = 1;
        if (nextTotal > 0)
        {
            SessionBadge.Visibility = Visibility.Visible;
            if (previousTotal == 0 && SystemParameters.ClientAreaAnimation)
            {
                var duration = TimeSpan.FromMilliseconds(BadgeAnimationMilliseconds);
                var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
                SessionBadge.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = easing });
                SessionBadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, duration) { EasingFunction = easing });
                SessionBadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, duration) { EasingFunction = easing });
            }
            return;
        }

        if (previousTotal == 0 || !SystemParameters.ClientAreaAnimation)
        {
            SessionBadge.Visibility = Visibility.Collapsed;
            return;
        }

        var exitDuration = TimeSpan.FromMilliseconds(160);
        var exitEasing = new CubicEase { EasingMode = ExitEasingMode };
        var fade = new DoubleAnimation(1, 0, exitDuration) { EasingFunction = exitEasing };
        fade.Completed += (_, _) =>
        {
            if (generation == badgeAnimationGeneration
                && sessionsSnapshot.WaitingCount + sessionsSnapshot.RunningCount + sessionsSnapshot.CompletedCount == 0)
            {
                SessionBadge.Visibility = Visibility.Collapsed;
                SessionBadge.Opacity = 1;
            }
        };
        SessionBadge.BeginAnimation(OpacityProperty, fade);
        SessionBadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.94, exitDuration) { EasingFunction = exitEasing });
        SessionBadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.94, exitDuration) { EasingFunction = exitEasing });
    }

    private void UpdateStatusLight(
        Grid light,
        ScaleTransform lightScale,
        TextBlock text,
        ScaleTransform textScale,
        TranslateTransform textTranslate,
        int previous,
        int current,
        string label,
        int generation,
        Func<int> currentCount)
    {
        light.BeginAnimation(OpacityProperty, null);
        lightScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        lightScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        text.BeginAnimation(OpacityProperty, null);
        textScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        textScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        textTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        light.Opacity = 1;
        lightScale.ScaleX = 1;
        lightScale.ScaleY = 1;
        text.Opacity = 1;
        textScale.ScaleX = 1;
        textScale.ScaleY = 1;
        textTranslate.Y = 0;

        if (current <= 0)
        {
            if (previous <= 0 || !SystemParameters.ClientAreaAnimation)
            {
                light.Visibility = Visibility.Collapsed;
                text.Text = string.Empty;
                return;
            }

            var exitDuration = TimeSpan.FromMilliseconds(160);
            var exitEasing = new CubicEase { EasingMode = ExitEasingMode };
            var fade = new DoubleAnimation(1, 0, exitDuration) { EasingFunction = exitEasing };
            fade.Completed += (_, _) =>
            {
                if (generation == badgeAnimationGeneration && currentCount() == 0)
                {
                    light.Visibility = Visibility.Collapsed;
                    text.Text = string.Empty;
                }
            };
            light.BeginAnimation(OpacityProperty, fade);
            lightScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.88, exitDuration) { EasingFunction = exitEasing });
            lightScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.88, exitDuration) { EasingFunction = exitEasing });
            return;
        }

        light.Visibility = Visibility.Visible;
        text.Text = current.ToString(CultureInfo.InvariantCulture);
        AutomationProperties.SetName(light, $"{label} {current}");
        if (previous == current || !SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        var duration = TimeSpan.FromMilliseconds(BadgeAnimationMilliseconds);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var numberOffset = NumberTransitionOffset(previous, current);
        text.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = easing });
        textScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, 1, duration) { EasingFunction = easing });
        textScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, 1, duration) { EasingFunction = easing });
        textTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(numberOffset, 0, duration) { EasingFunction = easing });
        if (previous <= 0)
        {
            light.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = easing });
            lightScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, 1, duration) { EasingFunction = easing });
            lightScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, 1, duration) { EasingFunction = easing });
        }
    }

    internal void Place(ScreenRect bounds)
    {
        physicalBounds = bounds;
        WindowInteropPolicy.Position(this, bounds);
    }

    internal static bool ExceedsDragThreshold(int deltaX, int deltaY) => Math.Abs(deltaX) >= 4 || Math.Abs(deltaY) >= 4;

    internal static ScreenRect TranslateBounds(ScreenRect bounds, int deltaX, int deltaY) => new(
        bounds.Left + deltaX,
        bounds.Top + deltaY,
        bounds.Right + deltaX,
        bounds.Bottom + deltaY);

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton != MouseButton.Left || !WindowInteropPolicy.TryGetCursorPosition(out dragOriginX, out dragOriginY))
        {
            return;
        }

        dragOriginBounds = physicalBounds;
        pointerDown = true;
        userDragging = false;
        Mouse.Capture(this);
        eventArgs.Handled = true;
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs eventArgs)
    {
        if (!pointerDown || eventArgs.LeftButton != MouseButtonState.Pressed
            || !WindowInteropPolicy.TryGetCursorPosition(out var cursorX, out var cursorY))
        {
            return;
        }

        var deltaX = cursorX - dragOriginX;
        var deltaY = cursorY - dragOriginY;
        if (!userDragging && !ExceedsDragThreshold(deltaX, deltaY))
        {
            return;
        }

        userDragging = true;
        Cursor = System.Windows.Input.Cursors.SizeAll;
        var moved = TranslateBounds(dragOriginBounds, deltaX, deltaY);
        var previous = physicalBounds;
        Place(moved);
        UserMoved?.Invoke(previous, moved);
        eventArgs.Handled = true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        if (!pointerDown || eventArgs.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var invokePrimary = !userDragging;
        pointerDown = false;
        userDragging = false;
        Cursor = System.Windows.Input.Cursors.Arrow;
        ReleaseMouseCapture();
        eventArgs.Handled = true;
        if (invokePrimary)
        {
            PrimaryInvoked?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ResetDragState()
    {
        pointerDown = false;
        userDragging = false;
        Cursor = System.Windows.Input.Cursors.Arrow;
    }

    private static void ApplyRow(QuotaCapsuleRow row, System.Windows.Controls.TextBlock label, System.Windows.Controls.ProgressBar bar, System.Windows.Controls.TextBlock value)
    {
        label.Text = row.Label;
        value.Text = row.Text;
        bar.Value = row.RemainingPercent ?? 0;
        bar.Foreground = row.ColorToken switch
        {
            "Amber" => Amber,
            "Critical" => Critical,
            "Cool" => Cool,
            _ => Neutral,
        };
    }

    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => ApplySystemAppearance();

    private void ApplySystemAppearance()
    {
        if (CapsuleThemePolicy.Resolve(SystemParameters.HighContrast, SystemParameters.IsGlassEnabled) == CapsuleSurfaceMode.OpaqueSystem)
        {
            SetBlurEnabled(false);
            GlassBorder.Background = System.Windows.SystemColors.WindowBrush;
            GlassBorder.BorderBrush = System.Windows.SystemColors.ActiveBorderBrush;
            BackdropLayer.Background = System.Windows.SystemColors.WindowBrush;
            FirstLabel.Foreground = SecondLabel.Foreground = System.Windows.SystemColors.WindowTextBrush;
            FirstValue.Foreground = SecondValue.Foreground = System.Windows.SystemColors.WindowTextBrush;
            SessionBadge.Background = System.Windows.SystemColors.ControlBrush;
            SessionBadge.BorderBrush = System.Windows.SystemColors.ActiveBorderBrush;
            return;
        }

        GlassBorder.Background = normalGlassBackground;
        GlassBorder.BorderBrush = OverlayGlassMaterial.Border;
        BackdropLayer.Background = normalBackdropBackground;
        SetBlurEnabled(true);
        FirstLabel.Foreground = SecondLabel.Foreground = normalLabelForeground;
        FirstValue.Foreground = SecondValue.Foreground = normalValueForeground;
        SessionBadge.Background = normalBadgeBackground;
        SessionBadge.BorderBrush = normalBadgeBorderBrush;
    }

    private void SetBlurEnabled(bool enabled)
    {
        if (blurReady && FrostedBlur.GetEnableBlur(BackdropLayer) != enabled)
        {
            FrostedBlur.SetBlurRadius(BackdropLayer, GlassBlurRadius);
            FrostedBlur.SetMerging(BackdropLayer, GlassMerging);
            FrostedBlur.SetDpi(BackdropLayer, 48);
            FrostedBlur.SetEnableBlur(BackdropLayer, enabled);
        }
    }

    private static System.Windows.Media.Brush Freeze(string color)
    {
        var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
