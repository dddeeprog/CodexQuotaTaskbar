using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CodexQuotaTaskbar.Host.Platform;
using CodexQuotaTaskbar.Core.Overlay;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Grid = System.Windows.Controls.Grid;
using Panel = System.Windows.Controls.Panel;

namespace CodexQuotaTaskbar.Host.UI;

public partial class QuotaCapsuleWindow
{
    private Grid? dotTopSurface;
    private Border? dotTopHost;
    private FrameworkElement? dotTopContent;
    private DotInlineView? dotAdaptiveReply;
    private Button? dotMarker;
    private TextBlock? dotMarkerText;
    internal event EventHandler? DotMarkerInvoked;
    internal bool DotTopExtensionVisible => dotTopContent is not null && dotTopHost?.Visibility == Visibility.Visible;
    internal double DotTopExtensionHeight => DotTopExtensionVisible ? appliedTopExtensionHeight : 0;
    internal double DotTopExtensionTargetHeight => dotTopContent is not null ? topExtensionTargetHeight : 0;
    internal FrameworkElement? DotTopContent => dotTopContent;
    internal double DotReplyContentWidthDip
    {
        get
        {
            var expected = Math.Max(1, LayoutWidth - GlassBorder.Margin.Left - GlassBorder.Margin.Right
                - GlassBorder.BorderThickness.Left - GlassBorder.BorderThickness.Right - 24);
            // Pixel rounding can leave a fractional DIP at noninteger DPI. Use
            // the current slot once arranged, but not a stale width from another layout.
            return dotTopContent is DotInlineView { ActualWidth: > 0 } reply
                && Math.Abs(reply.ActualWidth - expected) < 2 ? reply.ActualWidth : expected;
        }
    }
    internal ScreenRect BasePanelBounds
    {
        get
        {
            var scale = physicalBounds.Width > 0 ? physicalBounds.Width / LayoutWidth : 1;
            return physicalBounds with { Top = physicalBounds.Top + (int)Math.Round(DotTopExtensionHeight * scale) };
        }
    }
    internal ScreenRect CorePanelBounds
    {
        get
        {
            var scale = physicalBounds.Width > 0 ? physicalBounds.Width / LayoutWidth : 1;
            return physicalBounds with
            {
                Top = physicalBounds.Top + (int)Math.Round(DotTopExtensionHeight * scale),
                Bottom = physicalBounds.Bottom - (int)Math.Round(ResetScheduleHeight * scale),
            };
        }
    }
    internal double CoreBodyDesiredHeight => BaseDesiredHeight - ResetScheduleHeight;
    internal double CoreBodyReservedHeight => BaseReservedHeight - ResetScheduleHeight;
    internal double ResetScheduleTargetHeightForLayout => resetExtensionTargetHeight;
    internal bool DotInlineVisible => DotTopExtensionVisible && dotTopContent is DotInlineView;
    internal double DotInlineAreaHeight => DotInlineVisible ? DotTopExtensionHeight : 0;
    internal double ResetScheduleHeightForLayout => ResetScheduleHeight;
    internal bool DotMarkerVisible => dotMarker?.Visibility == Visibility.Visible;
    internal bool DotMarkerIsCatHead => BongoHasFrame;
    internal Button? DotMarkerButton => dotMarker;
    internal Rect DotMarkerBoundsDip => dotMarker is null ? Rect.Empty : new Rect(
        dotMarker.TranslatePoint(default, Shell), new System.Windows.Size(dotMarker.Width, dotMarker.Height));
    internal double EffectivePanelBodyHeight => (double)GetValue(PanelBodyHeightProperty);

    internal void SetDotReplyAffordance(bool allowed)
    {
        // Kept for owner protocol compatibility; no appearance exposes reply.
        if (dotMarker is not null) SetDotMarker(dotMarkerUnread);
    }
    private int dotMarkerUnread = -1;
    private bool dotMarkerCanAcknowledge;
    internal void SetDotMarker(int unreadCount, bool hasDraftOrPending = false, bool canAcknowledge = false)
    {
        VerifyAccess();
        if (dotMarker is null)
        {
            dotMarkerText = new TextBlock { FontSize = 9, FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White, HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center };
            dotMarker = new Button { Content = dotMarkerText, Width = 24, Height = 24,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = System.Windows.VerticalAlignment.Top,
                Padding = new Thickness(0), BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
                Style = (Style)FindResource("PanelButton"), Visibility = Visibility.Collapsed };
            dotMarker.Click += (_, args) => { args.Handled = true; DotMarkerInvoked?.Invoke(this, EventArgs.Empty); };
            Panel.SetZIndex(dotMarker, 8);
            Shell.Children.Add(dotMarker);
        }
        unreadCount = Math.Max(0, unreadCount);
        dotMarkerUnread = unreadCount;
        dotMarkerCanAcknowledge = canAcknowledge;
        dotMarker.Visibility = unreadCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshDotMarkerPlacement();
    }

    private void RefreshDotMarkerPlacement()
    {
        if (dotMarker is null) return;
        ApplyDotMarkerAppearance();
        if (BongoHasFrame)
        {
            var marker = CatHeadMarkerBounds(BongoModelBoundsDip, LayoutWidth);
            dotMarker.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            dotMarker.Margin = new Thickness(marker.Left, marker.Top, 0, 0);
            return;
        }
        var ballHeight = StatusBallVisibleForLayout ? companion!.Height : (double?)null;
        // On the unified card the left outside edge has a permanent 6-DIP
        // clearance from quota text and 4 DIP from task content. It stays clear
        // of the right-hand ball even while the task body is contracting.
        dotMarker.HorizontalAlignment = IsUnifiedPanel && ballHeight is not null
            ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right;
        var top = DotMarkerTopOffset(DotTopExtensionHeight,
            GlassBorder.Margin.Top, BongoStageHeight, ballHeight, CoreBodyDesiredHeight, IsUnifiedPanel);
        if (!IsUnifiedPanel && ballHeight is not null)
        {
            // A coexisting status ball is scaled to 52 DIP and sits below
            // the model. Its logical Height is still 56: use its transformed
            // extent and actual dock offset, not the old white-only 64.
            var size = companion!.LayoutTransform.TransformBounds(new Rect(0, 0, companion.Width, companion.Height));
            var bottom = StatusBallTopOffsetForLayout + size.Height;
            top = Math.Max(bottom, Math.Min(bottom + 8, DotTopExtensionHeight + CoreBodyDesiredHeight - 24));
        }
        dotMarker.Margin = new Thickness(0, top, 0, 0);
    }

    private void ApplyDotMarkerAppearance()
    {
        if (dotMarker is null || dotMarkerText is null) return;
        var cat = BongoHasFrame;
        dotMarkerText.Text = cat ? "!" : dotMarkerUnread > 99 ? "99+" : dotMarkerUnread > 0 ? dotMarkerUnread.ToString() : "·";
        dotMarkerText.FontSize = cat ? 20 : 9;
        dotMarkerText.FontWeight = cat ? FontWeights.Bold : FontWeights.SemiBold;
        var opaque = CapsuleThemePolicy.Resolve(SystemParameters.HighContrast, SystemParameters.IsGlassEnabled) == CapsuleSurfaceMode.OpaqueSystem;
        dotMarkerText.Foreground = cat ? opaque ? System.Windows.SystemColors.WindowTextBrush : new SolidColorBrush(Color.FromRgb(244, 205, 118)) : Brushes.White;
        dotMarker.Background = cat ? Brushes.Transparent : new SolidColorBrush(dotMarkerUnread > 0 ? Color.FromRgb(40, 111, 204) : Color.FromRgb(80, 82, 89));
        dotMarker.IsEnabled = !cat || dotMarkerCanAcknowledge;
        dotMarker.ToolTip = cat
            ? $"有 {Math.Max(0, dotMarkerUnread)} 条新消息 · 点击清除提醒"
            : $"Dot · {Math.Max(0, dotMarkerUnread)} 条未读，点击重显提醒；计时收回不会标为已读";
        ToolTipService.SetShowOnDisabled(dotMarker, true);
        AutomationProperties.SetName(dotMarker, (string)dotMarker.ToolTip);
    }

    // Uses the real model canvas, not the status ball or an old 56-DIP anchor.
    // The existing outer margin and model's 8-DIP inset provide the small headroom;
    // this overlay never changes the card, stage or model height.
    internal static Rect CatHeadMarkerBounds(Rect modelBounds, double layoutWidth)
    {
        const double hitSize = 24;
        if (modelBounds.IsEmpty || modelBounds.Width <= 0 || modelBounds.Height <= 0 ||
            !double.IsFinite(modelBounds.Left) || !double.IsFinite(modelBounds.Top) ||
            !double.IsFinite(modelBounds.Width) || !double.IsFinite(modelBounds.Height) ||
            !double.IsFinite(layoutWidth) || layoutWidth < hitSize) return Rect.Empty;
        // Approved A position: above the between-ear headline of the standard
        // model, rather than above the complete keyboard/mouse canvas centre.
        // Match the Uniform image mapping used by the reviewed 480x278 frame.
        var uniform = Math.Min(modelBounds.Width / 480, modelBounds.Height / 278);
        var left = modelBounds.Left + (modelBounds.Width - 480 * uniform) / 2;
        var top = modelBounds.Top + (modelBounds.Height - 278 * uniform) / 2;
        return new Rect(Math.Clamp(left + .61 * 480 * uniform - hitSize / 2, 0, layoutWidth - hitSize),
            Math.Max(0, top + .16 * 278 * uniform - hitSize - 3), hitSize, hitSize);
    }

    internal static double DotMarkerTopOffset(double topExtension, double glassTop, double bongoStage,
        double? visibleBallHeight, double coreHeight, bool unified)
    {
        // Keep the original vertical position for the model, the no-ball view,
        // and the unified white ball's new left-edge marker.
        if (unified || visibleBallHeight is not { } ballHeight)
            return glassTop + topExtension + bongoStage + 11;
        // Classic's left-side logo occupies the other edge, so its 112-DIP
        // card retains a right marker below the complete 56-DIP ball box.
        var belowBall = Math.Max(glassTop + 11, ballHeight + 8);
        var availableTop = Math.Max(ballHeight, coreHeight - 24);
        return topExtension + Math.Min(belowBall, availableTop);
    }

    // Only the top row is exchanged. Existing quota/task controls always stay in the same body.
    internal void ShowDotTopExtension(FrameworkElement content, double heightDip, bool acceptsReply = false)
    {
        VerifyAccess(); ArgumentNullException.ThrowIfNull(content);
        if (!double.IsFinite(heightDip) || heightDip < 1 || heightDip > 400) throw new ArgumentOutOfRangeException(nameof(heightDip));
        EnsureDotTopSurface();
        if (content.Parent is not null && !ReferenceEquals(content.Parent, dotTopHost))
            throw new InvalidOperationException("The Dot extension already belongs to another card.");
        if (!ReferenceEquals(dotAdaptiveReply, content))
        {
            if (dotAdaptiveReply is not null) dotAdaptiveReply.PreferredHeightChanged -= OnDotReplyPreferredHeightChanged;
            dotAdaptiveReply = content as DotInlineView;
            if (dotAdaptiveReply is not null) dotAdaptiveReply.PreferredHeightChanged += OnDotReplyPreferredHeightChanged;
        }
        // Reconciliation and fresh text for an existing notice must not restart its clock.
        if (ReferenceEquals(dotTopContent, content) && dotTopHost!.Visibility == Visibility.Visible
            && Math.Abs(topExtensionTargetHeight - (heightDip + 1)) < .01) return;
        if (dotTopContent is DotInlineView previous && !ReferenceEquals(previous, content)) previous.Hidden();
        dotTopContent = content;
        dotTopHost!.Margin = content is DotInlineView ? new Thickness(12, 0, 12, 0) : new Thickness(0);
        content.VerticalAlignment = VerticalAlignment.Bottom;
        if (!ReferenceEquals(dotTopHost.Child, content)) dotTopHost.Child = content;
        dotTopHost.Height = appliedTopExtensionHeight;
        dotTopHost.Visibility = Visibility.Visible;
        RefreshResetFooter();
        if (!acceptsReply) WindowInteropPolicy.SetReplyActivation(this, false);
        // The short notice animates. A composer and its content-sized input updates
        // retain immediate sizing, focus, selection and IME behavior.
        if (!RetargetTopExtension(heightDip + 1, content is DotTopNoticeView))
            RequestExtensionLayout(immediate: true);
    }

    // Lifecycle/mode/owner transfers keep immediate detach by default. Only a
    // normal notice dismissal explicitly requests its short exit transition.
    internal FrameworkElement? HideDotTopExtension(bool animate = false)
    {
        VerifyAccess();
        var previous = dotTopContent;
        if (previous is null) return null;
        RetargetTopExtension(0, animate && previous is DotTopNoticeView);
        return previous;
    }

    private void DetachDotTopContent()
    {
        if (dotAdaptiveReply is not null) dotAdaptiveReply.PreferredHeightChanged -= OnDotReplyPreferredHeightChanged;
        dotAdaptiveReply = null;
        if (dotTopContent is DotInlineView reply) reply.Hidden();
        if (dotTopHost is not null) { dotTopHost.Child = null; dotTopHost.Visibility = Visibility.Collapsed; dotTopHost.Height = 0; }
        if (dotTopSurface is not null) dotTopSurface.RowDefinitions[0].Height = new GridLength(0);
        dotTopContent = null;
        WindowInteropPolicy.SetReplyActivation(this, false);
        ReconcileResetStatusPointer();
    }

    internal void ShowInlineDot(DotInlineView view)
    {
        view.RecalculatePreferredHeight(DotReplyContentWidthDip);
        ShowDotTopExtension(view, view.PreferredHeightDip, acceptsReply: true);
    }
    internal DotInlineView? HideInlineDot() => HideDotTopExtension() as DotInlineView;
    internal void EnableExplicitReplyActivation() => WindowInteropPolicy.SetReplyActivation(this, true);

    private void OnDotReplyPreferredHeightChanged(object? sender, EventArgs args)
    {
        // An attached reply changes only its existing top row. It is never
        // reopened, reselected, activated or reconstructed for a size change.
        if (sender is DotInlineView reply && ReferenceEquals(reply, dotTopContent) && DotInlineVisible)
            ShowDotTopExtension(reply, reply.PreferredHeightDip, acceptsReply: true);
    }

    private void EnsureDotTopSurface()
    {
        if (dotTopSurface is not null) return;
        var body = (FrameworkElement)GlassBorder.Child;
        GlassBorder.Child = null;
        dotTopSurface = new Grid { ClipToBounds = true };
        dotTopSurface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
        dotTopSurface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        dotTopHost = new Border { Margin = new Thickness(12, 0, 12, 0),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            BorderThickness = new Thickness(0, 0, 0, 1), ClipToBounds = true, Visibility = Visibility.Collapsed };
        dotTopSurface.Children.Add(dotTopHost);
        Grid.SetRow(body, 1); dotTopSurface.Children.Add(body);
        GlassBorder.Child = dotTopSurface;
    }

    private FrameworkElement DetachQuotaBodyVisual()
    {
        if (dotTopSurface is null)
        {
            var body = (FrameworkElement)GlassBorder.Child;
            GlassBorder.Child = null; return body;
        }
        var current = dotTopSurface.Children.OfType<FrameworkElement>().Single(child => !ReferenceEquals(child, dotTopHost));
        dotTopSurface.Children.Remove(current); Grid.SetRow(current, 0); return current;
    }
    private void SetQuotaBodyVisual(FrameworkElement body)
    {
        if (dotTopSurface is null) GlassBorder.Child = body;
        else { Grid.SetRow(body, 1); dotTopSurface.Children.Add(body); }
    }
    private void RefreshDotExtensionLayout() => RequestExtensionLayout(immediate: true);
}
