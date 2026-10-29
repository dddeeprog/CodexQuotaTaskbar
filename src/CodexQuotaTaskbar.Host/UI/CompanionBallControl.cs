using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Host.Platform;
using CodexQuotaTaskbar.Host.Resets;
using Grid = System.Windows.Controls.Grid;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace CodexQuotaTaskbar.Host.UI;

internal sealed class CompanionBallControl : Grid, IDisposable
{
    private readonly CompanionEmotionDirector director = new(DateTimeOffset.UtcNow);
    private static int diagnosticControlCount;
    private readonly int diagnosticControlId = System.Threading.Interlocked.Increment(ref diagnosticControlCount);
    private readonly CompanionDiagnostics diagnostics = CompanionDiagnostics.Local;
    private CompanionFrame? diagnosticFrame;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly TextBlock number = new() { FontFamily = OverlayTypography.Number, FontSize = 9,
        FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center };
    private readonly System.Windows.Controls.Border countBadge;
    private bool resetHintHovered;
    private CompanionBallVisual? visual;
    private bool disposed;
    private bool reduced;
    private bool typingEnabled;
    private bool externalAppearance;
    private ResetVisualState resetState;
    private bool resetEpisodeActive;
    private bool observePointer = true;
    private string? externalFailure;
    private bool ready;
    private bool failed;
    private int count = -1;

    internal CompanionBallControl()
    {
        Width = Height = 56;
        Background = Brushes.Transparent;
        number.Typography.NumeralAlignment = FontNumeralAlignment.Tabular;
        countBadge = new() { Child = number, Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(72, 72, 77)),
            CornerRadius = new CornerRadius(11), Width = 22, Height = 22,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 2, 0), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        Children.Add(countBadge);
        // Observe the existing whole-ball surface; do not add a hit-test layer.
        MouseEnter += (_, _) => SetResetHintHovered(true);
        MouseLeave += (_, _) => SetResetHintHovered(false);
        timer.Tick += OnTick;
        director.SetVisible(false, DateTimeOffset.UtcNow);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnVisibility;
    }

    internal bool Ready => ready && !failed;
    internal CompanionBallVisual? Visual => visual;
    internal bool ResetNoticeActive => resetState != ResetVisualState.None;
    internal ResetVisualState ResetState => resetState;
    internal bool CanAnimateTyping => Ready && IsLoaded && IsVisible && !reduced && SystemParameters.ClientAreaAnimation;
    internal event EventHandler? AvailabilityChanged;
    internal event Action<System.Windows.Point>? GazeChanged;
    internal event Action<bool>? ResetHintHoverChanged;
    internal void SetResetHintHovered(bool hovered)
    {
        if (disposed || resetHintHovered == hovered) return;
        resetHintHovered = hovered;
        // Do not let the parent's emotion tooltip leak into this base area.
        ToolTipService.SetIsEnabled(this, !hovered);
        ResetHintHoverChanged?.Invoke(hovered);
    }
    internal void SetPointerObservationAllowed(bool allowed) => observePointer = allowed;
    internal void SetExternalFailure(string? reason) { externalFailure = reason; Render(); }
    internal void SetResetNoticeActive(bool active) => SetResetState(active ? ResetVisualState.Upcoming : ResetVisualState.None);

    internal void SetResetState(ResetVisualState state, bool? episodeActive = null)
    {
        var continuing = episodeActive ?? state != ResetVisualState.None;
        if (disposed || resetState == state && resetEpisodeActive == continuing) return;
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        resetState = state;
        resetEpisodeActive = continuing;
        visual?.SetResetState(state, continuing);
    }
    internal void SetExternalAppearance(bool active)
    {
        if (externalAppearance == active) return;
        externalAppearance = active;
        if (visual is not null) visual.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        if (!active) countBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Render();
    }
    internal void SetReducedMotion(bool value) { reduced = value; Render(); }
    internal void Apply(CodexSessionsSnapshot value) { director.Apply(value, DateTimeOffset.UtcNow); Render(); }
    internal void Tap() { director.Interact(DateTimeOffset.UtcNow, true); Render(); }
    internal void Hover() { director.Interact(DateTimeOffset.UtcNow, false); Render(); }
    internal void ToggleTour() { director.ToggleTour(DateTimeOffset.UtcNow); Render(); }
    internal void SetTypingInteraction(bool enabled)
    {
        typingEnabled = enabled;
        visual?.SetTypingInteraction(enabled && CanAnimateTyping);
    }
    internal void KeyboardActivity(CompanionInputEvent value)
    {
        if (value.Kind == CompanionInputKind.Reset || typingEnabled && CanAnimateTyping) visual?.Input(value);
    }

    internal void Gaze(System.Windows.Point point)
    {
        if (!observePointer || !Ready || reduced || !SystemParameters.ClientAreaAnimation) return;
        var direction = NormalizeGaze(point);
        if (!externalAppearance) visual?.Gaze(direction.X, direction.Y);
        GazeChanged?.Invoke(direction);
    }

    internal static System.Windows.Point NormalizeGaze(System.Windows.Point point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return new(0, 0);
        var x = (point.X - 28) / 72;
        var y = (point.Y - 28) / 72;
        // Normalize as a vector: far-away pointer angles remain distinct instead of both axes saturating.
        var scale = Math.Max(1, Math.Max(Math.Abs(x), Math.Abs(y)));
        x /= scale; y /= scale;
        var length = Math.Max(1 / scale, Math.Sqrt(x * x + y * y));
        return length > 0 ? new(x / length, y / length) : new(0, 0);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // Reuse the existing 5 Hz clock so eyes also follow the cursor outside the capsule.
        if (!disposed && observePointer && Ready && IsLoaded && IsVisible && !reduced && SystemParameters.ClientAreaAnimation
            && PresentationSource.FromVisual(this) is not null
            && WindowInteropPolicy.TryGetCursorPosition(out var x, out var y))
            Gaze(PointFromScreen(new System.Windows.Point(x, y)));
        Render();
    }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SetResetHintHovered(false);
        timer.Stop();
        director.SetVisible(false, DateTimeOffset.UtcNow);
        visual?.SetTypingInteraction(false);
    }
    private void OnVisibility(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) SetResetHintHovered(false);
        Render();
        if (IsVisible && IsLoaded && Ready) timer.Start(); else timer.Stop();
    }
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (disposed || failed) return;
        if (!ready)
        {
            try
            {
                visual = new CompanionBallVisual();
                visual.SetResetState(resetState, resetEpisodeActive);
                visual.Presented += OnPresented;
                Children.Insert(0, visual);
                ready = true;
                AvailabilityChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Fail(); return; }
        }
        Render();
        if (IsVisible) timer.Start();
    }

    private void Render()
    {
        director.SetVisible(!disposed && Ready && IsLoaded && IsVisible && !externalAppearance, DateTimeOffset.UtcNow);
        if (disposed || !Ready) return;
        if (externalAppearance)
        {
            visual?.SetTypingInteraction(false);
            countBadge.Visibility = Visibility.Collapsed;
            return;
        }
        var frame = director.Evaluate(DateTimeOffset.UtcNow);
        var minimizeMotion = reduced || !SystemParameters.ClientAreaAnimation;
        diagnosticFrame = frame;
        visual?.SetState(frame.Emotion, !minimizeMotion);
        visual?.SetTypingInteraction(typingEnabled && CanAnimateTyping);
        ObservePresentation("selected");
        AutomationProperties.SetName(this, frame.Label);
        ToolTip = externalFailure ?? frame.Label;
        if (count != frame.Count)
        {
            number.Text = frame.Count > 99 ? "99+" : frame.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            countBadge.Visibility = frame.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            var motion = new TranslateTransform();
            number.RenderTransform = motion;
            if (count >= 0 && !minimizeMotion)
            {
                motion.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(frame.Count > count ? 3 : -3, 0, TimeSpan.FromMilliseconds(180)));
                number.BeginAnimation(OpacityProperty, new DoubleAnimation(.3, 1, TimeSpan.FromMilliseconds(180)));
            }
            count = frame.Count;
        }
    }

    private void OnPresented() => ObservePresentation("presented");

    private void ObservePresentation(string kind)
    {
        if (diagnosticFrame is not { } frame || visual is null) return;
        diagnostics.Observe(diagnosticControlId, kind, frame, visual.Emotion, visual.EffectiveEmotion,
            IsVisible, reduced || !SystemParameters.ClientAreaAnimation, visual.GazeSample);
    }

    private void Fail()
    {
        if (disposed || failed) return;
        failed = true;
        ready = false;
        timer.Stop();
        visual?.Dispose();
        Visibility = Visibility.Collapsed;
        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (disposed) return;
        SetResetHintHovered(false);
        disposed = true;
        diagnostics.ReleaseControl(diagnosticControlId);
        ResetHintHoverChanged = null;
        resetState = ResetVisualState.None;
        timer.Stop();
        timer.Tick -= OnTick;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        IsVisibleChanged -= OnVisibility;
        if (visual is not null) visual.Presented -= OnPresented;
        visual?.Dispose();
    }
}
