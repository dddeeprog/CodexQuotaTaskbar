using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Host.Platform;
using FrostedBlur = BlurredBackground.WPF.BlurredBackground;
using Button = System.Windows.Controls.Button;
using MediaColor = System.Windows.Media.Color;
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
    internal const int SessionAdditionAnimationMilliseconds = 180;
    internal const int SessionRemovalAnimationMilliseconds = 160;
    internal const int SessionGlassCaptureTargetCount = 1;
    internal const bool ExpansionChangesOpacity = true;
    internal const double SessionGlassBlurRadius = 20;
    internal const double SessionGlassMerging = 0.92;
    internal const double SessionRemovalOffset = -6;
    internal const double CollapsedLayerRemovalOffset = -4;
    internal const double CollapsedOcclusionTop = ShadowTopInset + CardHeight;
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
    private int sessionTransitionGeneration;
    private DispatcherTimer? sessionTransitionTimer;
    private readonly Dictionary<int, TranslateTransform> glassMaskTransforms = [];

    internal SessionStackWindow(string monitorId)
    {
        MonitorId = monitorId;
        InitializeComponent();
        ExpandButton.Click += (_, _) =>
        {
            var previousExpanded = expanded;
            var previousScrollOffset = SessionsScroller.VerticalOffset;
            expanded = !expanded;
            Render(
                animateExpansion: true,
                previousSessionCount: snapshot.Sessions.Count,
                previousSnapshot: snapshot,
                previousExpanded: previousExpanded,
                previousScrollOffset: previousScrollOffset);
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
        Loaded += (_, _) => ApplySystemAppearance();
        Closed += (_, _) =>
        {
            sessionTransitionTimer?.Stop();
            SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        };
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
        var previousSnapshot = snapshot;
        var previousExpanded = expanded;
        var previousScrollOffset = SessionsScroller.VerticalOffset;
        var previousSessionCount = snapshot.Sessions.Count;
        var previousSessionIds = snapshot.Sessions.Select(session => session.Id).ToHashSet(StringComparer.Ordinal);
        var addedSessionIds = value.Sessions
            .Where(session => !previousSessionIds.Contains(session.Id))
            .Select(session => session.Id)
            .ToHashSet(StringComparer.Ordinal);
        snapshot = value;
        if (snapshot.Sessions.Count <= 1)
        {
            expanded = false;
        }
        Render(
            addedSessionIds: addedSessionIds,
            previousSessionCount: previousSessionCount,
            previousSnapshot: previousSnapshot,
            previousExpanded: previousExpanded,
            previousScrollOffset: previousScrollOffset);
    }

    internal void Place(ScreenRect bounds)
    {
        physicalBounds = bounds;
        WindowInteropPolicy.Position(this, bounds);
    }

    internal static int VisibleSessionCount(int totalCount, bool isExpanded) =>
        totalCount <= 0 ? 0 : isExpanded ? Math.Min(totalCount, MaximumVisibleSessions) : 1;

    internal static int CollapsedLayerCount(int totalCount) =>
        Math.Clamp(totalCount - 1, 0, MaximumVisibleSessions - 1);

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

    internal static bool ShouldAnimateSessionAddition(bool clientAreaAnimationEnabled, int addedSessionCount) =>
        clientAreaAnimationEnabled && addedSessionCount > 0;

    internal static bool ShouldAnimateSessionRemoval(
        bool clientAreaAnimationEnabled,
        int visibleRemovedSessionCount,
        int previousLayerCount,
        int currentLayerCount) =>
        clientAreaAnimationEnabled
        && (visibleRemovedSessionCount > 0 || currentLayerCount < previousLayerCount);

    internal static double SessionReflowOffset(int previousIndex, int currentIndex, bool isExpanded) =>
        previousIndex == currentIndex ? 0 : isExpanded ? (previousIndex - currentIndex) * (CardHeight + CardGap) : 6;

    internal static double ExpansionTransitionOffset(bool isExpanded) => isExpanded ? -10 : 10;

    internal static double GlassMaskCardTop(int index, bool isExpanded) =>
        isExpanded
            ? ShadowTopInset + index * (CardHeight + CardGap)
            : ShadowTopInset + Math.Min(index, MaximumVisibleSessions - 1) * 6;

    internal static double GlassMaskToggleTop(int totalCount, bool isExpanded) =>
        CalculateHeight(totalCount, isExpanded) - ToggleHeight;

    internal static LinearGradientBrush CreateSessionSurfaceBrush() => CreateNeutralGradient(
        MediaColor.FromArgb(136, 58, 58, 60),
        MediaColor.FromArgb(116, 48, 48, 50));

    internal static LinearGradientBrush CreateCollapsedLayerSurfaceBrush(int layer) => layer <= 1
        ? CreateNeutralGradient(
            MediaColor.FromArgb(184, 58, 58, 60),
            MediaColor.FromArgb(168, 44, 44, 46))
        : CreateNeutralGradient(
            MediaColor.FromArgb(168, 52, 52, 54),
            MediaColor.FromArgb(152, 40, 40, 42));

    internal static bool ShouldEnableGlass(bool elementIsLoaded, bool opaque) => elementIsLoaded && !opaque;

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

    private void Render(
        bool animateExpansion = false,
        IReadOnlySet<string>? addedSessionIds = null,
        int previousSessionCount = 0,
        CodexSessionsSnapshot? previousSnapshot = null,
        bool previousExpanded = false,
        double previousScrollOffset = 0)
    {
        var transitionGeneration = ++sessionTransitionGeneration;
        sessionTransitionTimer?.Stop();
        CardTransitionOverlay.Children.Clear();
        LayerTransitionOverlay.Children.Clear();
        CardTransitionOverlay.Clip = null;
        SessionsPanel.Children.Clear();
        ShadowSlots.Children.Clear();
        var visibleCount = VisibleSessionCount(snapshot.Sessions.Count, expanded);
        var renderedSessions = (expanded ? snapshot.Sessions : snapshot.Sessions.Take(1)).ToArray();
        var addedPills = new List<(UIElement Pill, int Index)>();
        for (var index = 0; index < renderedSessions.Length; index++)
        {
            var pill = CreateSessionPill(renderedSessions[index], index == renderedSessions.Length - 1);
            SessionsPanel.Children.Add(pill);
            if (addedSessionIds?.Contains(renderedSessions[index].Id) == true)
            {
                addedPills.Add((pill, index));
            }
        }
        for (var index = 0; index < renderedSessions.Length; index++)
        {
            ShadowSlots.Children.Add(CreateShadowSlot(index == renderedSessions.Length - 1));
        }
        SessionsScroller.Height = visibleCount * CardHeight + Math.Max(0, visibleCount - 1) * CardGap;
        CardsViewport.Height = ShadowTopInset + SessionsScroller.Height + ShadowBottomInset;
        var collapsedLayerCount = expanded ? 0 : CollapsedLayerCount(snapshot.Sessions.Count);
        CollapsedLayers.Visibility = collapsedLayerCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        CollapsedMiddleLayer.Visibility = collapsedLayerCount >= 1 ? Visibility.Visible : Visibility.Collapsed;
        CollapsedBackLayer.Visibility = collapsedLayerCount >= 2 ? Visibility.Visible : Visibility.Collapsed;
        var collapsedOcclusionClip = collapsedLayerCount > 0 ? CreateCollapsedOcclusionClip() : null;
        CollapsedLayers.Clip = collapsedOcclusionClip;
        LayerTransitionOverlay.Clip = collapsedOcclusionClip;
        if (!expanded)
        {
            BeginAnimation(AnimatedScrollOffsetProperty, null);
            SessionsScroller.ScrollToTop();
            scrollTarget = 0;
            SetValue(AnimatedScrollOffsetProperty, 0d);
        }
        UpdateShadowSlots(SessionsScroller.VerticalOffset);

        ExpandButton.Visibility = snapshot.Sessions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(ExpandButton, expanded ? "收起会话" : $"展开 {snapshot.Sessions.Count} 个会话");
        AutomationProperties.SetName(this, $"Codex 会话，共 {snapshot.Sessions.Count} 个，{(expanded ? "已展开" : "已收起")}");

        var nextHeight = CalculateHeight(snapshot.Sessions.Count, expanded);
        var previousHeight = DesiredHeight;
        var maskHeight = animateExpansion && SystemParameters.ClientAreaAnimation
            ? Math.Max(previousHeight, nextHeight)
            : nextHeight;
        UpdateGlassMask(maskHeight);
        ApplySystemAppearance();
        AnimateSessionAdditions(addedSessionIds, addedPills, previousSessionCount, previousSnapshot);
        var removalAnimated = AnimateSessionRemovals(
            previousSnapshot,
            previousExpanded,
            previousScrollOffset,
            addedSessionIds,
            transitionGeneration);
        var expansionAnimated = AnimateExpansionTransition(
            animateExpansion,
            out var expansionCompletionMilliseconds);

        var transitionAnimated = removalAnimated || expansionAnimated;
        var presentedHeight = transitionAnimated && nextHeight < previousHeight ? previousHeight : nextHeight;
        var heightChanged = Math.Abs(DesiredHeight - presentedHeight) > 0.1;
        DesiredHeight = presentedHeight;
        Height = Math.Max(1, presentedHeight);
        if (heightChanged)
        {
            PresentationChanged?.Invoke(this, EventArgs.Empty);
        }
        if (transitionAnimated)
        {
            var completionMilliseconds = Math.Max(
                removalAnimated ? Math.Max(SessionAdditionAnimationMilliseconds, SessionRemovalAnimationMilliseconds) : 0,
                expansionCompletionMilliseconds);
            CompleteSessionTransitionAfterDelay(transitionGeneration, nextHeight, completionMilliseconds);
        }
    }

    private void AnimateSessionAdditions(
        IReadOnlySet<string>? addedSessionIds,
        IReadOnlyList<(UIElement Pill, int Index)> addedPills,
        int previousSessionCount,
        CodexSessionsSnapshot? previousSnapshot)
    {
        if (!ShouldAnimateSessionAddition(SystemParameters.ClientAreaAnimation, addedSessionIds?.Count ?? 0))
        {
            return;
        }

        if (!expanded && SessionsPanel.Children.Count > 0)
        {
            var previousTopId = previousSnapshot?.Sessions.FirstOrDefault()?.Id;
            var currentTopId = snapshot.Sessions.FirstOrDefault()?.Id;
            var topWasRemoved = previousTopId is not null
                && currentTopId is not null
                && !snapshot.Sessions.Any(session => StringComparer.Ordinal.Equals(session.Id, previousTopId));
            var startOffset = topWasRemoved ? 6 : -6;
            AnimateAddedElement((UIElement)SessionsPanel.Children[0], startOffset);
            AnimateAddedShadow(0, startOffset);
        }
        else
        {
            foreach (var (pill, index) in addedPills)
            {
                AnimateAddedElement(pill, -6);
                AnimateAddedShadow(index, -6);
            }
        }

        if (expanded)
        {
            return;
        }

        var previousLayerCount = CollapsedLayerCount(previousSessionCount);
        var currentLayerCount = CollapsedLayerCount(snapshot.Sessions.Count);
        if (previousLayerCount < 1 && currentLayerCount >= 1)
        {
            AnimateAddedElement(CollapsedMiddleLayer, -4);
        }
        if (previousLayerCount < 2 && currentLayerCount >= 2)
        {
            AnimateAddedElement(CollapsedBackLayer, -4);
        }
    }

    private bool AnimateSessionRemovals(
        CodexSessionsSnapshot? previousSnapshot,
        bool previousExpanded,
        double previousScrollOffset,
        IReadOnlySet<string>? addedSessionIds,
        int transitionGeneration)
    {
        if (previousSnapshot is null)
        {
            return false;
        }

        var currentIds = snapshot.Sessions.Select(session => session.Id).ToHashSet(StringComparer.Ordinal);
        var previousSessions = previousSnapshot.Sessions;
        var previousViewportHeight = VisibleSessionCount(previousSessions.Count, previousExpanded) * CardHeight
            + Math.Max(0, VisibleSessionCount(previousSessions.Count, previousExpanded) - 1) * CardGap;
        var visibleRemovedSessions = previousSessions
            .Select((session, index) => (Session: session, Index: index))
            .Where(item => !currentIds.Contains(item.Session.Id))
            .Where(item => !previousExpanded
                ? item.Index == 0
                : IsShadowSlotVisible(item.Index, previousScrollOffset, previousViewportHeight))
            .ToArray();
        var previousIndices = previousSessions
            .Select((session, index) => (session.Id, Index: index))
            .ToDictionary(item => item.Id, item => item.Index, StringComparer.Ordinal);
        var visibleMovedSessionCount = snapshot.Sessions
            .Select((session, index) => (Session: session, Index: index))
            .Count(item => previousIndices.TryGetValue(item.Session.Id, out var previousIndex)
                && previousIndex != item.Index
                && (!expanded || IsShadowSlotVisible(
                    item.Index,
                    SessionsScroller.VerticalOffset,
                    SessionsScroller.Height)));
        var previousLayerCount = previousExpanded ? 0 : CollapsedLayerCount(previousSessions.Count);
        var currentLayerCount = expanded ? 0 : CollapsedLayerCount(snapshot.Sessions.Count);
        if (!ShouldAnimateSessionRemoval(
                SystemParameters.ClientAreaAnimation,
                visibleRemovedSessions.Length + visibleMovedSessionCount,
                previousLayerCount,
                currentLayerCount))
        {
            return false;
        }

        var opaque = CapsuleThemePolicy.Resolve(SystemParameters.HighContrast, SystemParameters.IsGlassEnabled)
            == CapsuleSurfaceMode.OpaqueSystem;
        foreach (var (session, index) in visibleRemovedSessions)
        {
            var ghost = CreateSessionPill(session, true);
            ghost.IsHitTestVisible = false;
            ghost.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            ghost.VerticalAlignment = System.Windows.VerticalAlignment.Top;
            ghost.Margin = new Thickness(0, ShadowTopInset + index * (CardHeight + CardGap) - previousScrollOffset, 0, 0);
            ghost.Effect = opaque ? null : CreateShadowEffect();
            ApplySessionButtonAppearance(ghost, opaque);
            CardTransitionOverlay.Children.Add(ghost);
            AnimateRemovedElement(ghost, SessionRemovalOffset);
        }

        for (var layer = currentLayerCount + 1; layer <= previousLayerCount; layer++)
        {
            var ghost = CreateCollapsedLayerGhost(layer, opaque);
            LayerTransitionOverlay.Children.Add(ghost);
            AnimateRemovedElement(ghost, CollapsedLayerRemovalOffset);
        }

        AnimateRetainedSessionReflow(previousSnapshot, addedSessionIds);
        return transitionGeneration == sessionTransitionGeneration;
    }

    private void AnimateRetainedSessionReflow(
        CodexSessionsSnapshot previousSnapshot,
        IReadOnlySet<string>? addedSessionIds)
    {
        var previousIndices = previousSnapshot.Sessions
            .Select((session, index) => (session.Id, Index: index))
            .ToDictionary(item => item.Id, item => item.Index, StringComparer.Ordinal);
        for (var currentIndex = 0; currentIndex < SessionsPanel.Children.Count; currentIndex++)
        {
            if (SessionsPanel.Children[currentIndex] is not Button pill || pill.Tag is not string sessionId
                || addedSessionIds?.Contains(sessionId) == true
                || !previousIndices.TryGetValue(sessionId, out var previousIndex)
                || expanded && !IsShadowSlotVisible(
                    currentIndex,
                    SessionsScroller.VerticalOffset,
                    SessionsScroller.Height))
            {
                continue;
            }

            var offset = SessionReflowOffset(previousIndex, currentIndex, expanded);
            if (Math.Abs(offset) <= 0.1)
            {
                continue;
            }

            AnimateMovedElement(pill, offset);
            AnimateMovedShadow(currentIndex, offset);
        }
    }

    private void CompleteSessionTransitionAfterDelay(int generation, double finalHeight, int delayMilliseconds)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(delayMilliseconds),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (generation != sessionTransitionGeneration)
            {
                return;
            }

            CardTransitionOverlay.Children.Clear();
            LayerTransitionOverlay.Children.Clear();
            var heightChanged = Math.Abs(DesiredHeight - finalHeight) > 0.1;
            DesiredHeight = finalHeight;
            Height = Math.Max(1, finalHeight);
            if (heightChanged)
            {
                PresentationChanged?.Invoke(this, EventArgs.Empty);
            }
            UpdateGlassMask(finalHeight);
        };
        sessionTransitionTimer = timer;
        timer.Start();
    }

    private static void AnimateAddedElement(UIElement element, double startOffset)
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(SessionAdditionAnimationMilliseconds);
        var translate = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = translate;
        element.Opacity = 1;
        translate.Y = 0;
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, duration) { EasingFunction = easing });
        translate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(startOffset, 0, duration) { EasingFunction = easing });
    }

    private void AnimateAddedShadow(int index, double startOffset)
    {
        if (index < ShadowSlots.Children.Count
            && IsShadowSlotVisible(index, SessionsScroller.VerticalOffset, SessionsScroller.Height))
        {
            AnimateAddedElement((UIElement)ShadowSlots.Children[index], startOffset);
        }
    }

    private static void AnimateRemovedElement(UIElement element, double endOffset)
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(SessionRemovalAnimationMilliseconds);
        var translate = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = translate;
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, duration) { EasingFunction = easing });
        translate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0, endOffset, duration) { EasingFunction = easing });
    }

    private static void AnimateMovedElement(UIElement element, double startOffset)
    {
        var translate = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = translate;
        translate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(startOffset, 0, TimeSpan.FromMilliseconds(SessionAdditionAnimationMilliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            });
    }

    private void AnimateMovedShadow(int index, double startOffset)
    {
        if (index < ShadowSlots.Children.Count
            && IsShadowSlotVisible(index, SessionsScroller.VerticalOffset, SessionsScroller.Height))
        {
            AnimateMovedElement((UIElement)ShadowSlots.Children[index], startOffset);
        }
    }

    private bool AnimateExpansionTransition(
        bool animate,
        out int completionMilliseconds)
    {
        completionMilliseconds = 0;
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
            return false;
        }

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(ExpansionAnimationMilliseconds);
        CardsViewport.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0.72, 1, duration) { EasingFunction = easing });
        var transitionOffset = ExpansionTransitionOffset(expanded);
        CardsTransitionTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(transitionOffset, 0, duration) { EasingFunction = easing });
        AnimateGlassMasks(transitionOffset, duration, easing);
        rotation.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(expanded ? 0 : 180, targetAngle, duration) { EasingFunction = easing });

        completionMilliseconds = Math.Max(completionMilliseconds, ExpansionAnimationMilliseconds);
        return true;
    }

    private static RectangleGeometry CreateCollapsedOcclusionClip() => new(new Rect(
        0,
        CollapsedOcclusionTop,
        StackWidth,
        Math.Max(1, CalculateHeight(MaximumVisibleSessions, true) - CollapsedOcclusionTop)));

    private void AnimateGlassMasks(double startOffset, Duration duration, IEasingFunction easing)
    {
        foreach (var translate in glassMaskTransforms.Values)
        {
            translate.BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimation(startOffset, 0, duration) { EasingFunction = easing },
                HandoffBehavior.SnapshotAndReplace);
        }
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
            FontFamily = OverlayTypography.Text,
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
            FontFamily = OverlayTypography.Text,
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
            window.UpdateGlassMask(window.DesiredHeight);
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
            Effect = CreateShadowEffect(),
        };
        return shadow;
    }

    private static System.Windows.Media.Effects.DropShadowEffect CreateShadowEffect() => new()
    {
        Color = MediaColor.FromRgb(0, 0, 0),
        BlurRadius = 18,
        ShadowDepth = 5,
        Opacity = 0.44,
    };

    private static Border CreateCollapsedLayerGhost(int layer, bool opaque) => new()
    {
        Width = layer == 1 ? 334 : 324,
        Height = CardHeight,
        Margin = new Thickness(0, layer == 1 ? 22 : 28, 0, 0),
        VerticalAlignment = VerticalAlignment.Top,
        CornerRadius = new CornerRadius(29),
        Background = opaque
            ? WpfSystemColors.WindowBrush
            : CreateCollapsedLayerSurfaceBrush(layer),
        BorderBrush = opaque
            ? WpfSystemColors.ActiveBorderBrush
            : new SolidColorBrush(layer == 1 ? MediaColor.FromArgb(59, 255, 255, 255) : MediaColor.FromArgb(46, 255, 255, 255)),
        BorderThickness = new Thickness(1),
    };

    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs) => ApplySystemAppearance();

    private void UpdateGlassMask(double maskHeight)
    {
        glassMaskTransforms.Clear();
        var drawing = new DrawingGroup();
        var viewportClip = new RectangleGeometry(new Rect(
            ShadowHorizontalInset,
            ShadowTopInset,
            StackWidth - ShadowHorizontalInset * 2,
            SessionsScroller.Height));

        if (expanded)
        {
            for (var index = 0; index < SessionsPanel.Children.Count; index++)
            {
                var top = GlassMaskCardTop(index, true) - SessionsScroller.VerticalOffset;
                AddGlassMaskRectangle(
                    drawing,
                    new Rect(ShadowHorizontalInset, top, 344, CardHeight),
                    29,
                    index,
                    viewportClip);
            }
        }
        else if (snapshot.Sessions.Count > 0)
        {
            AddGlassMaskRectangle(
                drawing,
                new Rect(ShadowHorizontalInset, GlassMaskCardTop(0, false), 344, CardHeight),
                29,
                0);
            var layerCount = CollapsedLayerCount(snapshot.Sessions.Count);
            if (layerCount >= 1)
            {
                AddGlassMaskRectangle(
                    drawing,
                    new Rect((StackWidth - 334) / 2, GlassMaskCardTop(1, false), 334, CardHeight),
                    29,
                    1);
            }
            if (layerCount >= 2)
            {
                AddGlassMaskRectangle(
                    drawing,
                    new Rect((StackWidth - 324) / 2, GlassMaskCardTop(2, false), 324, CardHeight),
                    29,
                    2);
            }
        }

        if (snapshot.Sessions.Count > 1)
        {
            AddGlassMaskRectangle(
                drawing,
                new Rect(
                    (StackWidth - ToggleHeight) / 2,
                    GlassMaskToggleTop(snapshot.Sessions.Count, expanded),
                    ToggleHeight,
                    ToggleHeight),
                ToggleHeight / 2);
        }

        var absoluteBounds = new Rect(0, 0, StackWidth, Math.Max(1, maskHeight));
        GlassCaptureSurface.OpacityMask = new DrawingBrush(drawing)
        {
            Viewbox = absoluteBounds,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = absoluteBounds,
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
            TileMode = TileMode.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
        };
    }

    private TranslateTransform AddGlassMaskRectangle(
        DrawingGroup drawing,
        Rect bounds,
        double radius,
        int? visualIndex = null,
        Geometry? clip = null)
    {
        var translate = new TranslateTransform();
        var geometry = new RectangleGeometry(bounds, radius, radius)
        {
            Transform = translate,
        };
        var maskDrawing = new GeometryDrawing(System.Windows.Media.Brushes.White, null, geometry);
        if (clip is null)
        {
            drawing.Children.Add(maskDrawing);
        }
        else
        {
            var clippedDrawing = new DrawingGroup { ClipGeometry = clip };
            clippedDrawing.Children.Add(maskDrawing);
            drawing.Children.Add(clippedDrawing);
        }

        if (visualIndex is int index)
        {
            glassMaskTransforms[index] = translate;
        }
        return translate;
    }

    private void ApplySystemAppearance()
    {
        var opaque = CapsuleThemePolicy.Resolve(SystemParameters.HighContrast, SystemParameters.IsGlassEnabled) == CapsuleSurfaceMode.OpaqueSystem;
        ShadowSlots.Visibility = opaque ? Visibility.Collapsed : Visibility.Visible;
        foreach (var button in SessionsPanel.Children.OfType<Button>())
        {
            ApplySessionButtonAppearance(button, opaque);
        }
        ConfigureGlassCapture(!opaque);
        CollapsedMiddleLayer.Background = opaque ? WpfSystemColors.WindowBrush : CreateCollapsedLayerSurfaceBrush(1);
        CollapsedBackLayer.Background = opaque ? WpfSystemColors.WindowBrush : CreateCollapsedLayerSurfaceBrush(2);
        CollapsedMiddleLayer.BorderBrush = opaque ? WpfSystemColors.ActiveBorderBrush : new SolidColorBrush(MediaColor.FromArgb(68, 255, 255, 255));
        CollapsedBackLayer.BorderBrush = opaque ? WpfSystemColors.ActiveBorderBrush : new SolidColorBrush(MediaColor.FromArgb(54, 255, 255, 255));
        ExpandButton.Background = opaque ? WpfSystemColors.ControlBrush : CreateSessionSurfaceBrush();
        ExpandButton.BorderBrush = opaque ? WpfSystemColors.ActiveBorderBrush : new SolidColorBrush(MediaColor.FromArgb(88, 255, 255, 255));
        ExpandChevron.Stroke = opaque ? WpfSystemColors.ControlTextBrush : System.Windows.Media.Brushes.White;
    }

    private void ApplySessionButtonAppearance(Button button, bool opaque)
    {
        button.Background = opaque ? WpfSystemColors.WindowBrush : CreateSessionSurfaceBrush();
        button.BorderBrush = opaque ? WpfSystemColors.ActiveBorderBrush : new SolidColorBrush(MediaColor.FromArgb(70, 255, 255, 255));
        foreach (var text in FindTextBlocks(button))
        {
            text.Foreground = opaque
                ? WpfSystemColors.WindowTextBrush
                : Equals(text.Tag, "secondary") ? new SolidColorBrush(MediaColor.FromRgb(174, 174, 178))
                : System.Windows.Media.Brushes.White;
        }
    }

    private void ConfigureGlassCapture(bool enabled)
    {
        FrostedBlur.SetBlurRadius(GlassCaptureSurface, SessionGlassBlurRadius);
        FrostedBlur.SetMerging(GlassCaptureSurface, SessionGlassMerging);
        FrostedBlur.SetDpi(GlassCaptureSurface, 48);
        if (GlassCaptureSurface.IsLoaded)
        {
            FrostedBlur.SetEnableBlur(GlassCaptureSurface, enabled);
        }
    }

    private static LinearGradientBrush CreateNeutralGradient(MediaColor top, MediaColor bottom)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new WpfPoint(0, 0),
            EndPoint = new WpfPoint(0, 1),
        };
        brush.GradientStops.Add(new GradientStop(top, 0));
        brush.GradientStops.Add(new GradientStop(bottom, 1));
        return brush;
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
