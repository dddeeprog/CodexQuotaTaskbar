using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Host.Platform;
using Button = System.Windows.Controls.Button;
using MediaColor = System.Windows.Media.Color;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfPoint = System.Windows.Point;
using WpfSystemColors = System.Windows.SystemColors;

namespace CodexQuotaTaskbar.Host.UI;

public partial class SessionStackWindow : Window
{
    internal const double StackWidth = 392;
    internal const double ShadowHorizontalInset = 24;
    internal const double ShadowTopInset = 16;
    internal const double ShadowBottomInset = 24;
    internal const int ScrollAnimationMilliseconds = 180;
    internal const int ExpansionAnimationMilliseconds = 220;
    private const int MaximumVisibleSessions = 3;
    private const double CardHeight = 58;
    private const double CardGap = 5;
    private const double ToggleHeight = 24;
    private const double ToggleGap = 8;
    private static readonly SolidColorBrush ShadowFill = new(MediaColor.FromArgb(168, 0, 0, 0));
    private static readonly DependencyProperty AnimatedScrollOffsetProperty = DependencyProperty.Register(
        nameof(AnimatedScrollOffset),
        typeof(double),
        typeof(SessionStackWindow),
        new PropertyMetadata(0d, OnAnimatedScrollOffsetChanged));
    private CodexSessionsSnapshot snapshot = CodexSessionsSnapshot.Empty;
    private ScreenRect physicalBounds;
    private bool expanded;
    private double scrollTarget;

    internal SessionStackWindow(string monitorId)
    {
        MonitorId = monitorId;
        InitializeComponent();
        ExpandButton.Click += (_, _) =>
        {
            expanded = !expanded;
            Render(animateExpansion: true);
        };
        PreviewMouseWheel += (_, eventArgs) =>
        {
            if (!expanded || snapshot.Sessions.Count <= MaximumVisibleSessions)
            {
                return;
            }
            scrollTarget = CalculateScrollOffset(
                scrollTarget,
                eventArgs.Delta,
                SessionsScroller.ScrollableHeight);
            AnimateScrollTo(scrollTarget);
            eventArgs.Handled = true;
        };
        MouseRightButtonUp += (_, _) => ContextInvoked?.Invoke(this, EventArgs.Empty);
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        Closed += (_, _) => SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
    }

    internal string MonitorId { get; }
    internal ScreenRect PhysicalBounds => physicalBounds;
    internal double DesiredHeight { get; private set; } = 98;
    internal bool IsExpanded => expanded;
    internal event Action<string>? OpenSessionRequested;
    internal event EventHandler? ContextInvoked;
    internal event EventHandler? PresentationChanged;

    internal void Apply(CodexSessionsSnapshot value)
    {
        snapshot = value;
        if (snapshot.Sessions.Count <= 1)
        {
            expanded = false;
        }
        Render();
    }

    internal void Place(ScreenRect bounds)
    {
        physicalBounds = bounds;
        WindowInteropPolicy.Position(this, bounds);
    }

    internal static int VisibleSessionCount(int totalCount, bool isExpanded) =>
        totalCount <= 0 ? 0 : isExpanded ? Math.Min(totalCount, MaximumVisibleSessions) : 1;

    internal static double CalculateHeight(int totalCount, bool isExpanded)
    {
        var visibleCount = VisibleSessionCount(totalCount, isExpanded);
        if (visibleCount == 0)
        {
            return 0;
        }

        var cards = visibleCount * CardHeight + Math.Max(0, visibleCount - 1) * CardGap;
        var toggle = totalCount > 1 ? ToggleGap + ToggleHeight : 0;
        return ShadowTopInset + cards + ShadowBottomInset + toggle;
    }

    internal static double CalculateScrollOffset(double currentOffset, int wheelDelta, double scrollableHeight) =>
        Math.Clamp(currentOffset - wheelDelta / 4d, 0, Math.Max(0, scrollableHeight));

    internal static bool ShouldAnimateScroll(bool clientAreaAnimationEnabled, double currentOffset, double targetOffset) =>
        clientAreaAnimationEnabled && Math.Abs(currentOffset - targetOffset) > 0.1;

    internal static double ExpansionTransitionOffset(bool isExpanded) => isExpanded ? -10 : 10;

    internal static bool IsShadowSlotVisible(int index, double scrollOffset, double viewportHeight)
    {
        var top = index * (CardHeight + CardGap);
        var bottom = top + CardHeight;
        return top >= scrollOffset && bottom <= scrollOffset + viewportHeight;
    }

    internal static string SessionStateLabel(CodexSessionState state) => state switch
    {
        CodexSessionState.Waiting => "等待你的输入",
        CodexSessionState.Failed => "任务遇到问题",
        CodexSessionState.Review => "任务已完成，可查看",
        CodexSessionState.Running => "正在处理",
        _ => "空闲",
    };

    private void Render(bool animateExpansion = false)
    {
        SessionsPanel.Children.Clear();
        ShadowSlots.Children.Clear();
        var visibleCount = VisibleSessionCount(snapshot.Sessions.Count, expanded);
        var renderedSessions = (expanded ? snapshot.Sessions : snapshot.Sessions.Take(1)).ToArray();
        for (var index = 0; index < renderedSessions.Length; index++)
        {
            SessionsPanel.Children.Add(CreateSessionPill(renderedSessions[index], index == renderedSessions.Length - 1));
        }
        for (var index = 0; index < renderedSessions.Length; index++)
        {
            ShadowSlots.Children.Add(CreateShadowSlot(index == renderedSessions.Length - 1));
        }
        SessionsScroller.Height = visibleCount * CardHeight + Math.Max(0, visibleCount - 1) * CardGap;
        CardsViewport.Height = ShadowTopInset + SessionsScroller.Height + ShadowBottomInset;
        CollapsedLayers.Visibility = !expanded && snapshot.Sessions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (!expanded)
        {
            BeginAnimation(AnimatedScrollOffsetProperty, null);
            SessionsScroller.ScrollToTop();
            scrollTarget = 0;
            SetValue(AnimatedScrollOffsetProperty, 0d);
        }
        UpdateShadowSlots(SessionsScroller.VerticalOffset);

        ExpandButton.Visibility = snapshot.Sessions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        AnimateExpansionTransition(animateExpansion);
        AutomationProperties.SetName(ExpandButton, expanded ? "收起会话" : $"展开 {snapshot.Sessions.Count} 个会话");
        AutomationProperties.SetName(this, $"Codex 会话，共 {snapshot.Sessions.Count} 个，{(expanded ? "已展开" : "已收起")}");

        var nextHeight = CalculateHeight(snapshot.Sessions.Count, expanded);
        var heightChanged = Math.Abs(DesiredHeight - nextHeight) > 0.1;
        DesiredHeight = nextHeight;
        Height = Math.Max(1, nextHeight);
        ApplySystemAppearance();
        if (heightChanged)
        {
            PresentationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void AnimateExpansionTransition(bool animate)
    {
        var targetAngle = expanded ? 180d : 0d;
        var rotation = ExpandChevron.RenderTransform as RotateTransform ?? new RotateTransform();
        ExpandChevron.RenderTransformOrigin = new WpfPoint(0.5, 0.5);
        ExpandChevron.RenderTransform = rotation;
        rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        CardsViewport.BeginAnimation(OpacityProperty, null);
        CardsTransitionTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        rotation.Angle = targetAngle;
        CardsViewport.Opacity = 1;
        CardsTransitionTranslate.Y = 0;

        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(ExpansionAnimationMilliseconds);
        CardsViewport.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0.72, 1, duration) { EasingFunction = easing });
        CardsTransitionTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(ExpansionTransitionOffset(expanded), 0, duration) { EasingFunction = easing });
        rotation.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(expanded ? 0 : 180, targetAngle, duration) { EasingFunction = easing });
    }

    private Button CreateSessionPill(CodexSessionSnapshot session, bool isLast)
    {
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition());
        content.RowDefinitions.Add(new RowDefinition());
        var title = new TextBlock
        {
            Text = session.Title,
            Foreground = System.Windows.Media.Brushes.White,
            FontFamily = new WpfFontFamily("Segoe UI Variable Text"),
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(title);
        var state = new TextBlock
        {
            Text = SessionStateLabel(session.State),
            Tag = "secondary",
            Foreground = SessionStateBrush(session.State),
            FontFamily = new WpfFontFamily("Segoe UI Variable Text"),
            FontSize = 11.5,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(state, 1);
        content.Children.Add(state);

        var button = new Button
        {
            Content = content,
            Style = (Style)FindResource("SessionPill"),
            Margin = new Thickness(0, 0, 0, CardGap),
            Tag = session.Id,
        };
        if (isLast)
        {
            button.Margin = new Thickness(0);
        }
        AutomationProperties.SetName(button, $"{session.Title}，{SessionStateLabel(session.State)}，{session.UpdatedAt.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture)}");
        button.Click += (_, _) => OpenSessionRequested?.Invoke(session.Id);
        return button;
    }

    private double AnimatedScrollOffset
    {
        get => (double)GetValue(AnimatedScrollOffsetProperty);
        set => SetValue(AnimatedScrollOffsetProperty, value);
    }

    private void AnimateScrollTo(double targetOffset)
    {
        var currentOffset = SessionsScroller.VerticalOffset;
        BeginAnimation(AnimatedScrollOffsetProperty, null);
        AnimatedScrollOffset = currentOffset;
        if (!ShouldAnimateScroll(SystemParameters.ClientAreaAnimation, currentOffset, targetOffset))
        {
            AnimatedScrollOffset = targetOffset;
            return;
        }

        var animation = new DoubleAnimation(
            currentOffset,
            targetOffset,
            TimeSpan.FromMilliseconds(ScrollAnimationMilliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        BeginAnimation(AnimatedScrollOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void OnAnimatedScrollOffsetChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is SessionStackWindow window && eventArgs.NewValue is double offset)
        {
            window.SessionsScroller.ScrollToVerticalOffset(offset);
            window.UpdateShadowSlots(offset);
        }
    }

    private void UpdateShadowSlots(double offset)
    {
        ShadowTranslate.Y = -offset;
        for (var index = 0; index < ShadowSlots.Children.Count; index++)
        {
            ShadowSlots.Children[index].Opacity = IsShadowSlotVisible(index, offset, SessionsScroller.Height) ? 1 : 0;
        }
    }

    private static Border CreateShadowSlot(bool isLast)
    {
        var shadow = new Border
        {
            Width = 344,
            Height = CardHeight,
            CornerRadius = new CornerRadius(29),
            Background = ShadowFill,
            Margin = isLast ? new Thickness(0) : new Thickness(0, 0, 0, CardGap),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = MediaColor.FromRgb(0, 0, 0),
                BlurRadius = 18,
                ShadowDepth = 5,
                Opacity = 0.44,
            },
        };
        return shadow;
    }

    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs) => ApplySystemAppearance();

    private void ApplySystemAppearance()
    {
        var opaque = CapsuleThemePolicy.Resolve(SystemParameters.HighContrast, SystemParameters.IsGlassEnabled) == CapsuleSurfaceMode.OpaqueSystem;
        ShadowSlots.Visibility = opaque ? Visibility.Collapsed : Visibility.Visible;
        foreach (var button in SessionsPanel.Children.OfType<Button>())
        {
            button.Background = opaque ? WpfSystemColors.WindowBrush : new SolidColorBrush(MediaColor.FromArgb(230, 32, 32, 34));
            button.BorderBrush = opaque ? WpfSystemColors.ActiveBorderBrush : new SolidColorBrush(MediaColor.FromArgb(72, 255, 255, 255));
            foreach (var text in FindTextBlocks(button))
            {
                text.Foreground = opaque
                    ? WpfSystemColors.WindowTextBrush
                    : Equals(text.Tag, "secondary") ? new SolidColorBrush(MediaColor.FromRgb(174, 174, 178))
                    : System.Windows.Media.Brushes.White;
            }
        }
        ExpandButton.Background = opaque ? WpfSystemColors.ControlBrush : new SolidColorBrush(MediaColor.FromArgb(217, 32, 32, 34));
        ExpandButton.BorderBrush = opaque ? WpfSystemColors.ActiveBorderBrush : new SolidColorBrush(MediaColor.FromArgb(88, 255, 255, 255));
        ExpandChevron.Stroke = opaque ? WpfSystemColors.ControlTextBrush : System.Windows.Media.Brushes.White;
    }

    private static IEnumerable<TextBlock> FindTextBlocks(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is TextBlock text)
            {
                yield return text;
            }
            foreach (var descendant in FindTextBlocks(child))
            {
                yield return descendant;
            }
        }
    }

    private static System.Windows.Media.Brush SessionStateBrush(CodexSessionState state) => new SolidColorBrush(state switch
    {
        CodexSessionState.Waiting => MediaColor.FromRgb(255, 184, 77),
        CodexSessionState.Failed => MediaColor.FromRgb(255, 92, 108),
        CodexSessionState.Review => MediaColor.FromRgb(174, 174, 178),
        _ => MediaColor.FromRgb(174, 174, 178),
    });
}
