using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CodexQuotaTaskbar.Core.Sessions;
using Button = System.Windows.Controls.Button;

namespace CodexQuotaTaskbar.Host.UI;

public partial class QuotaCapsuleWindow
{
    internal const double PanelSessionRowHeight = 34;
    private const double PanelSessionInsets = 18;
    private bool panelExpanded = true;
    private double panelScrollTarget;
    private int panelGeneration;
    private int scrollAnimationGeneration;
    private bool panelScrollAnimating;
    private static readonly DependencyProperty PanelBodyHeightProperty = DependencyProperty.Register(
        "PanelBodyHeight", typeof(double), typeof(QuotaCapsuleWindow), new PropertyMetadata(0d, OnPanelBodyHeightChanged));
    private static readonly DependencyProperty PanelScrollOffsetProperty = DependencyProperty.Register(
        "PanelScrollOffset", typeof(double), typeof(QuotaCapsuleWindow), new PropertyMetadata(0d,
            (owner, args) => ((QuotaCapsuleWindow)owner).PanelSessionsScroller.ScrollToVerticalOffset((double)args.NewValue)));

    internal double BaseDesiredHeight => LayoutBaseHeight + (IsUnifiedPanel ? (double)GetValue(PanelBodyHeightProperty) + ResetScheduleHeight : 0);
    internal double BaseReservedHeight => LayoutBaseHeight + (IsUnifiedPanel ? PanelSessionHeight(sessionsSnapshot.Sessions.Count, true) + ResetScheduleHeight : 0);
    internal double DesiredHeight => BaseDesiredHeight + DotTopExtensionHeight;
    internal double ReservedHeight => BaseReservedHeight + DotTopExtensionHeight;
    internal bool PanelExpanded => panelExpanded;
    internal event EventHandler? PresentationChanged;
    internal event Action<string>? OpenSessionRequested;

    internal static double PanelSessionHeight(int count, bool expanded) => count <= 0 ? 0
        : PanelSessionInsets + PanelSessionRowHeight * (expanded ? Math.Min(count, 3) : 1);

    private void InitializePanelSessions()
    {
        SessionToggle.Click += (_, _) => TogglePanelSessions();
        PanelSessionsScroller.PreviewMouseWheel += (_, args) =>
        {
            if (!panelExpanded || PanelSessionsScroller.ScrollableHeight <= 0) return;
            panelScrollTarget = SessionStackWindow.CalculateScrollOffset(panelScrollTarget, args.Delta, PanelSessionsScroller.ScrollableHeight);
            var current = PanelSessionsScroller.VerticalOffset;
            AnimatePanelValue(PanelScrollOffsetProperty, current, panelScrollTarget, 180, IsLoaded && SystemParameters.ClientAreaAnimation);
            args.Handled = true;
        };
        PanelSessionsScroller.ScrollChanged += (_, _) =>
        {
            if (!panelScrollAnimating) panelScrollTarget = PanelSessionsScroller.VerticalOffset;
        };
    }

    internal void TogglePanelSessions()
    {
        panelExpanded = !panelExpanded;
        if (!panelExpanded)
        {
            scrollAnimationGeneration++;
            panelScrollAnimating = false;
            BeginAnimation(PanelScrollOffsetProperty, null);
            SetValue(PanelScrollOffsetProperty, 0d);
            PanelSessionsScroller.ScrollToTop();
            panelScrollTarget = 0;
        }
        UpdatePanelHeight(true);
    }

    private static void OnPanelBodyHeightChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var window = (QuotaCapsuleWindow)owner;
        var height = Math.Max(0, (double)args.NewValue);
        window.PanelSessionsRegion.Height = height;
        window.RefreshBongoBodyLayout();
        window.RefreshStandaloneWindowHeight();
        window.PresentationChanged?.Invoke(window, EventArgs.Empty);
    }

    private void UpdatePanelHeight(bool animate)
    {
        var count = sessionsSnapshot.Sessions.Count;
        SessionToggle.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(SessionToggle, panelExpanded ? $"收起 {count} 个会话" : $"展开 {count} 个会话");
        SessionChevron.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        var target = PanelSessionHeight(count, panelExpanded);
        var current = (double)GetValue(PanelBodyHeightProperty);
        var angle = SessionChevronRotation.Angle;
        var useAnimation = animate && IsLoaded && SystemParameters.ClientAreaAnimation && Math.Abs(current - target) > 0.1;
        AnimatePanelValue(PanelBodyHeightProperty, current, target, 220, useAnimation);
        SessionChevronRotation.Angle = angle;
        SessionChevronRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        var targetAngle = panelExpanded ? 180 : 0;
        if (useAnimation)
        {
            var duration = TimeSpan.FromMilliseconds(220);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            SessionChevronRotation.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(angle, targetAngle, duration) { EasingFunction = ease });
        }
        else SessionChevronRotation.Angle = targetAngle;
        // Reservation may change even when the collapsed height does not.
        PresentationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AnimatePanelValue(DependencyProperty property, double current, double target, int milliseconds, bool animate)
    {
        var scrolling = property == PanelScrollOffsetProperty;
        var generation = scrolling ? ++scrollAnimationGeneration : 0;
        if (scrolling) panelScrollAnimating = animate;
        // Set the base under the current clock before removing it; never flash through the target first.
        SetValue(property, current);
        BeginAnimation(property, null);
        if (!animate) { SetValue(property, target); return; }
        var animation = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(milliseconds))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (scrolling) animation.Completed += (_, _) =>
        {
            if (generation != scrollAnimationGeneration) return;
            SetValue(property, target);
            BeginAnimation(property, null);
            panelScrollAnimating = false;
            panelScrollTarget = PanelSessionsScroller.VerticalOffset;
        };
        BeginAnimation(property, animation);
    }

    private void RenderPanelSessions(CodexSessionsSnapshot previous)
    {
        var next = sessionsSnapshot.Sessions;
        if (previous.Sessions.SequenceEqual(next)) return;
        var generation = ++panelGeneration;
        var previousButtons = PanelSessionsList.Children.OfType<Button>().ToArray();
        var old = previousButtons.GroupBy(button => (string)button.Tag).ToDictionary(group => group.Key, group => group.First());
        PanelSessionsList.Children.Clear();
        var animate = IsLoaded && SystemParameters.ClientAreaAnimation;
        foreach (var session in next)
        {
            var retained = old.TryGetValue(session.Id, out var button);
            button ??= CreatePanelSessionButton(session.Id);
            button.BeginAnimation(HeightProperty, null);
            button.BeginAnimation(OpacityProperty, null);
            button.Height = PanelSessionRowHeight;
            button.Opacity = 1;
            button.IsHitTestVisible = true;
            var labels = ((Grid)button.Content).Children.OfType<TextBlock>().ToArray();
            labels[0].Text = session.Title;
            labels[1].Text = CompactSessionState(session.State);
            labels[1].Tag = session.State;
            AutomationProperties.SetName(button, $"{session.Title}，{labels[1].Text}");
            PanelSessionsList.Children.Add(button);
            if (!retained && animate)
                button.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop });
        }
        if (animate)
        {
            var ids = next.Select(session => session.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var removed in previousButtons.Where(button => !ids.Contains((string)button.Tag)))
            {
                var index = Array.IndexOf(previousButtons, removed);
                PanelSessionsList.Children.Insert(Math.Min(index, PanelSessionsList.Children.Count), removed);
                removed.IsHitTestVisible = false;
                var duration = TimeSpan.FromMilliseconds(160);
                removed.BeginAnimation(OpacityProperty, new DoubleAnimation(removed.Opacity, 0, duration));
                var collapse = new DoubleAnimation(removed.ActualHeight, 0, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                collapse.Completed += (_, _) => { if (panelGeneration == generation) PanelSessionsList.Children.Remove(removed); };
                removed.BeginAnimation(HeightProperty, collapse);
            }
        }
        ApplyPanelSessionAppearance(SystemParameters.HighContrast || !SystemParameters.IsGlassEnabled);
        UpdatePanelHeight(animate);
    }

    private Button CreatePanelSessionButton(string id)
    {
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { FontFamily = OverlayTypography.Text, FontSize = 12, FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
        var state = new TextBlock { FontFamily = OverlayTypography.Text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(state, 1);
        content.Children.Add(title);
        content.Children.Add(state);
        var button = new Button { Content = content, Tag = id, Height = PanelSessionRowHeight,
            Padding = new Thickness(4, 0, 4, 0), Style = (Style)FindResource("PanelButton") };
        button.Click += (_, _) => OpenSessionRequested?.Invoke(id);
        return button;
    }

    private static string CompactSessionState(CodexSessionState state) => state switch
    {
        CodexSessionState.Running => "进行中",
        CodexSessionState.Waiting => "待输入",
        CodexSessionState.Failed => "有异常",
        CodexSessionState.Review => "已完成",
        _ => "空闲"
    };

    private void ApplyPanelSessionAppearance(bool highContrast)
    {
        foreach (var button in PanelSessionsList.Children.OfType<Button>())
        {
            var labels = ((Grid)button.Content).Children.OfType<TextBlock>().ToArray();
            labels[0].Foreground = highContrast ? System.Windows.SystemColors.WindowTextBrush : Cool;
            labels[1].Foreground = highContrast ? System.Windows.SystemColors.WindowTextBrush
                : labels[1].Tag is CodexSessionState.Failed or CodexSessionState.Waiting ? Amber : normalLabelForeground;
        }
    }
}
