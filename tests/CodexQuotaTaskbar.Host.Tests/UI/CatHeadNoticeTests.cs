using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.Bongo;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.Overlay;
using CodexQuotaTaskbar.Host.Platform;
using CodexQuotaTaskbar.Host.UI;
using Button = System.Windows.Controls.Button;
using Size = System.Windows.Size;

namespace CodexQuotaTaskbar.Host.Tests.UI;

// Only hidden controls, synthetic frames/messages and an in-memory coordinator.
// Never Show/Start, create an HWND, observe OS input, or access account data.
[Collection("Native UI isolation")]
public sealed class CatHeadNoticeTests
{
    public static IEnumerable<object[]> GeometryCases()
    {
        foreach (var unified in new[] { false, true })
        foreach (var dpi in new[] { 96, 120, 144, 192 })
        foreach (var size in new[] { 160, 192, 240 }) yield return [unified, dpi, size];
    }

    [Theory]
    [MemberData(nameof(GeometryCases))]
    public void Exclamation_tracks_the_real_model_head_without_growing_the_card(bool unified, int dpi, int size) => Sta(() =>
    {
        var owner = Owner(unified);
        try
        {
            var root = (FrameworkElement)owner.Content;
            VisualTreeHelper.SetRootDpi(root, new DpiScale(dpi / 96d, dpi / 96d));
            owner.ConfigureBongoSurface(true, size);
            Present(owner);
            Layout(owner);
            var height = owner.DesiredHeight;
            var baseHeight = owner.BaseDesiredHeight;
            owner.SetDotMarker(5, canAcknowledge: true);
            Layout(owner);
            Assert.True(owner.DotMarkerIsCatHead);
            var button = Assert.IsType<Button>(owner.DotMarkerButton);
            var text = Assert.IsType<TextBlock>(button.Content);
            Assert.Equal("!", text.Text);
            Assert.Equal(20, text.FontSize);
            Assert.Equal(FontWeights.Bold, text.FontWeight);
            Assert.Equal(0, Assert.IsType<SolidColorBrush>(button.Background).Color.A);
            if (CapsuleThemePolicy.Resolve(SystemParameters.HighContrast, SystemParameters.IsGlassEnabled) != CapsuleSurfaceMode.OpaqueSystem)
                Assert.Equal(System.Windows.Media.Color.FromRgb(244, 205, 118), Assert.IsType<SolidColorBrush>(text.Foreground).Color);
            Assert.True(button.IsEnabled);
            Assert.Equal(24, button.Width);
            Assert.Equal(24, button.Height);
            var model = owner.BongoModelBoundsDip;
            var marker = owner.DotMarkerBoundsDip;
            Assert.Equal(size, model.Width, 5);
            Assert.Equal(24, marker.Width, 5);
            Assert.Equal(24, marker.Height, 5);
            var pixelDip = 96d / dpi;
            // Approved A landmark is the actual between-ear headline, not
            // the centre of the complete keyboard/mouse model canvas.
            Assert.InRange((marker.Left + marker.Width / 2 - model.Left) / model.Width, .60, .62);
            Assert.InRange(model.Top + .16 * model.Height - marker.Bottom, 3 - pixelDip, 3 + pixelDip);
            Assert.True(marker.Left >= 0 && marker.Right <= owner.LayoutWidth);
            Assert.True(marker.Top >= 0 && marker.Bottom <= owner.DesiredHeight);
            AssertNonOverlapping(marker, owner.StatusBallBoundsDip);
            Assert.Equal(height, owner.DesiredHeight);
            Assert.Equal(baseHeight, owner.BaseDesiredHeight);

            var originalTop = marker.Top;
            var originalModelTop = model.Top;
            owner.ShowDotTopExtension(new DotTopNoticeView(), 36);
            Layout(owner);
            Assert.Equal(37, owner.DotTopExtensionHeight);
            Assert.Equal(originalModelTop + 37, owner.BongoModelBoundsDip.Top, 5);
            Assert.InRange(Math.Abs(owner.DotMarkerBoundsDip.Top - originalTop - 37), 0, pixelDip);
            Assert.Equal(height + 37, owner.DesiredHeight, 5);
            owner.HideDotTopExtension();
            Layout(owner);
            Assert.InRange(Math.Abs(owner.DotMarkerBoundsDip.Top - originalTop), 0, pixelDip);
            Assert.Equal(height, owner.DesiredHeight, 5);
            AssertHidden(owner);
        }
        finally { owner.Close(); }
    });

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(99)] [InlineData(100)] [InlineData(1000)]
    public void Cat_uses_one_glyph_and_preserves_the_full_unread_count_in_accessible_text(int unread) => Sta(() =>
    {
        var owner = Owner(true);
        try
        {
            owner.ConfigureBongoSurface(true, 192); Present(owner);
            owner.SetDotMarker(unread, hasDraftOrPending: true, canAcknowledge: true);
            Layout(owner);
            var button = Assert.IsType<Button>(owner.DotMarkerButton);
            Assert.Equal("!", Assert.IsType<TextBlock>(button.Content).Text);
            Assert.Equal(unread > 0, owner.DotMarkerVisible);
            var tooltip = Assert.IsType<string>(button.ToolTip);
            Assert.Equal($"有 {unread} 条新消息 · 点击清除提醒", tooltip);
            Assert.Equal(tooltip, AutomationProperties.GetName(button));
            Assert.True(ToolTipService.GetShowOnDisabled(button));
            AssertHidden(owner);
        }
        finally { owner.Close(); }
    });

    [Fact]
    public void Missing_or_cleared_frame_restores_the_original_counter_and_white_ball_clearance() => Sta(() =>
    {
        var owner = Owner(true);
        try
        {
            owner.SetDotMarker(100);
            Layout(owner);
            var button = Assert.IsType<Button>(owner.DotMarkerButton);
            Assert.False(owner.DotMarkerIsCatHead);
            Assert.Equal("99+", Assert.IsType<TextBlock>(button.Content).Text);
            var old = owner.DotMarkerBoundsDip;
            owner.ConfigureBongoSurface(true, 192);
            Assert.False(owner.DotMarkerIsCatHead);
            Assert.Equal("99+", Assert.IsType<TextBlock>(button.Content).Text);
            Present(owner); Layout(owner);
            Assert.True(owner.DotMarkerIsCatHead);
            Assert.Equal("!", Assert.IsType<TextBlock>(button.Content).Text);
            Assert.False(button.IsEnabled); // Default third argument is deliberately not an acknowledgement capability.
            Assert.Equal("有 100 条新消息 · 点击清除提醒", Assert.IsType<string>(button.ToolTip));
            owner.SetDotMarker(100, canAcknowledge: true);
            Assert.True(button.IsEnabled);
            owner.ClearBongoFrame(); Layout(owner);
            Assert.False(owner.DotMarkerIsCatHead);
            Assert.True(button.IsEnabled); // Existing non-cat notification entry remains usable.
            Assert.Equal("99+", Assert.IsType<TextBlock>(button.Content).Text);
            Assert.Equal(9, Assert.IsType<TextBlock>(button.Content).FontSize);
            Assert.Equal(old, owner.DotMarkerBoundsDip);
            AssertNonOverlapping(owner.DotMarkerBoundsDip, owner.StatusBallBoundsDip);
            AssertHidden(owner);
        }
        finally { owner.Close(); }
    });

    [Fact]
    public void Actual_marker_click_sends_the_current_batch_token_and_waits_for_receiver_state_before_clearing() => Sta(() =>
    {
        using var fixture = new Fixture(true);
        var state = State();
        fixture.Coordinator.ApplyInlineDotState(state);
        var marker = Assert.IsType<Button>(fixture.Owner.DotMarkerButton);
        Assert.True(marker.IsEnabled);
        marker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var first = Assert.Single(fixture.Actions);
        Assert.Equal("ack-notice", first.Kind);
        Assert.Null(first.MessageId);
        Assert.Null(first.Text);
        Assert.Null(first.RequestId);
        Assert.Equal(0, first.Count);
        Assert.Equal(0, first.DraftRevision);
        Assert.Null(first.OriginalDraftRevision);
        Assert.Equal(state.ReadToken, first.ReadToken);
        Assert.Equal(state.SessionId, first.ReadSessionId);
        Assert.Equal(state.ReadSequence, first.ReadSequence);
        Assert.NotEqual(state.Sequence, first.ReadSequence);
        Assert.True(DotInlineWire.ValidAction(first));
        Assert.Equal(0, fixture.OpenRequests);
        Assert.Equal(state, Field<DotInlineState>(fixture.Coordinator, "inlineDotState"));
        Assert.True(fixture.Owner.DotMarkerVisible);
        Assert.Null(Field<object?>(fixture.Coordinator, "inlineDotView"));
        Assert.Null(fixture.Owner.DotTopContent);

        var newer = state with { Sequence = 42, UnreadCount = 100, ReadToken = "33333333333333333333333333333333", ReadSequence = 8 };
        fixture.Coordinator.ApplyInlineDotState(newer);
        marker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(2, fixture.Actions.Count);
        Assert.Equal(newer.ReadToken, fixture.Actions[1].ReadToken);
        Assert.Equal(newer.ReadSequence, fixture.Actions[1].ReadSequence);
        Assert.True(fixture.Owner.DotMarkerVisible);
        var acknowledged = newer with { Sequence = 43, UnreadCount = 0, EarlierUnreadCount = 0,
            Messages = [new("synthetic-message", "Synthetic retained history", null, false)] };
        fixture.Coordinator.ApplyInlineDotState(acknowledged);
        Assert.False(fixture.Owner.DotMarkerVisible);
        Assert.Equal(acknowledged, Field<DotInlineState>(fixture.Coordinator, "inlineDotState"));
        Assert.Equal(state.DraftText, acknowledged.DraftText);
        Assert.True(acknowledged.HasPendingAttempt && acknowledged.NeedsReview);
        AssertHidden(fixture.Owner);
    });

    [Theory]
    [InlineData("offline")] [InlineData("zero-unread")] [InlineData("missing-token")]
    [InlineData("token-not-N")] [InlineData("missing-session")] [InlineData("session-not-N")]
    [InlineData("zero-read-sequence")] [InlineData("negative-read-sequence")]
    public void A_disabled_or_stale_capability_cannot_emit_a_batch_action_even_if_a_click_is_queued(string invalid) => Sta(() =>
    {
        using var fixture = new Fixture(true);
        var initial = State();
        var state = invalid switch
        {
            "offline" => initial with { Online = false },
            "zero-unread" => initial with { UnreadCount = 0 },
            "missing-token" => initial with { ReadToken = null },
            "token-not-N" => initial with { ReadToken = Guid.Empty.ToString("D") },
            "missing-session" => initial with { SessionId = "" },
            "session-not-N" => initial with { SessionId = Guid.Empty.ToString("D") },
            "zero-read-sequence" => initial with { ReadSequence = 0 },
            "negative-read-sequence" => initial with { ReadSequence = -1 },
            _ => throw new ArgumentOutOfRangeException(nameof(invalid)),
        };
        fixture.Coordinator.ApplyInlineDotState(state);
        Assert.Null(OverlayCoordinator.CurrentNoticeAcknowledgement(state));
        var marker = Assert.IsType<Button>(fixture.Owner.DotMarkerButton);
        Assert.False(marker.IsEnabled);
        marker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Empty(fixture.Actions);
        Assert.Equal(0, fixture.OpenRequests);
        Assert.Equal(state, Field<DotInlineState>(fixture.Coordinator, "inlineDotState"));
        AssertHidden(fixture.Owner);
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void A_white_ball_or_frame_failure_keeps_the_original_open_notification_event(bool hadFrame) => Sta(() =>
    {
        using var fixture = new Fixture(hadFrame);
        fixture.Coordinator.ApplyInlineDotState(State());
        if (hadFrame) fixture.Owner.ClearBongoFrame();
        var marker = Assert.IsType<Button>(fixture.Owner.DotMarkerButton);
        Assert.False(fixture.Owner.DotMarkerIsCatHead);
        Assert.True(marker.IsEnabled);
        marker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, fixture.OpenRequests);
        Assert.Empty(fixture.Actions);
        AssertHidden(fixture.Owner);
    });

    private static DotInlineState State() => new(
        "11111111111111111111111111111111", 41, 19, "Synthetic unsent draft",
        [new("synthetic-message", "Synthetic unread history", null, true)],
        99, 98, "synthetic-message", 1, false, true, true, true, true, true, "Synthetic connected",
        AttemptState: "uncertain", ReadToken: "22222222222222222222222222222222", ReadSequence: 7);

    private sealed class Fixture : IDisposable
    {
        internal OverlayCoordinator Coordinator { get; } = new(false, unifiedPanel: true, observePointer: false);
        internal QuotaCapsuleWindow Owner { get; } = CatHeadNoticeTests.Owner(true);
        internal List<DotInlineAction> Actions { get; } = [];
        internal int OpenRequests;
        internal Fixture(bool cat)
        {
            Field<DispatcherTimer>(Coordinator, "timer").Stop();
            SetField(Coordinator, "bongoSelected", true);
            if (cat) { Owner.ConfigureBongoSurface(true, 192); Present(Owner); }
            Field<Dictionary<string, QuotaCapsuleWindow>>(Coordinator, "windows").Add(Owner.MonitorId, Owner);
            Owner.DotMarkerInvoked += (_, _) => Coordinator.HandleDotMarkerInvoked(Owner);
            Coordinator.InlineDotActionRequested += Actions.Add;
            Coordinator.OpenDotChatRequested += (_, _) => OpenRequests++;
            Coordinator.SetInlineDotMode(true);
            Coordinator.SetDotPresentationEnabled(true);
        }
        public void Dispose() => Coordinator.Dispose();
    }

    private static QuotaCapsuleWindow Owner(bool unified)
    {
        var owner = new QuotaCapsuleWindow("synthetic-cat-head", unified);
        owner.SetPointerObservationAllowed(false);
        owner.SetCompanion(true, false);
        Field<CompanionBallControl>(owner, "companion").RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        return owner;
    }
    private static void Present(QuotaCapsuleWindow owner) => owner.PresentBongoFrame(new BongoFrame(1, 1, [255, 255, 255, 255], 4));
    private static void Layout(QuotaCapsuleWindow owner)
    {
        var root = (FrameworkElement)owner.Content;
        for (var index = 0; index < 3; index++)
        {
            root.Measure(new Size(owner.LayoutWidth, owner.DesiredHeight));
            root.Arrange(new Rect(0, 0, owner.LayoutWidth, owner.DesiredHeight)); root.UpdateLayout();
        }
    }
    private static void AssertNonOverlapping(Rect first, Rect second)
    {
        var overlap = Rect.Intersect(first, second);
        Assert.True(overlap.IsEmpty || overlap.Width <= .001 || overlap.Height <= .001);
    }
    private static void AssertHidden(QuotaCapsuleWindow owner)
    {
        Assert.False(owner.IsVisible);
        Assert.Equal(nint.Zero, new WindowInteropHelper(owner).Handle);
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void SetField(object owner, string name, object? value) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            try { action(); } catch (Exception error) { failure = error; }
            finally { dispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Hidden synthetic cat-head test did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
