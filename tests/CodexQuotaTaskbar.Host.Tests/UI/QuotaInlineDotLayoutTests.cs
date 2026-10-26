using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Host.Bongo;
using CodexQuotaTaskbar.Host.Overlay;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

[Collection("Native UI isolation")]
public sealed class QuotaInlineDotLayoutTests
{
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Top_reply_keeps_existing_task_controls_and_closing_restores_base_height(bool unified) => Sta(() =>
    {
        var owner = new QuotaCapsuleWindow("inline-layout", unified);
        try
        {
            owner.ApplySessions(Sessions(4));
            var tasks = Assert.IsType<StackPanel>(owner.FindName("PanelSessionsList"));
            var before = tasks.Children.Cast<UIElement>().ToArray();
            var beforeHeight = owner.DesiredHeight;
            var view = new DotInlineView();
            view.InputControl.Text = "Synthetic draft";
            view.InputControl.Select(3, 4);
            owner.ShowInlineDot(view);
            Assert.True(owner.DotInlineVisible);
            Assert.Equal(view.PreferredHeightDip + 1, owner.DotInlineAreaHeight);
            Assert.Equal(unified ? Visibility.Visible : Visibility.Collapsed, Assert.IsType<Grid>(owner.FindName("PanelSessionsRegion")).Visibility);
            Assert.Same(owner, Window.GetWindow(view));
            Assert.Equal(nint.Zero, new WindowInteropHelper(owner).Handle);
            Assert.Equal(owner.DesiredHeight, owner.ReservedHeight);
            Assert.Equal(unified ? 4 : 0, tasks.Children.Count);
            Assert.Equal(before, tasks.Children.Cast<UIElement>());
            Assert.Same(view, owner.HideInlineDot());
            Assert.False(owner.DotInlineVisible);
            Assert.Null(view.Parent);
            Assert.Equal(beforeHeight, owner.DesiredHeight);
            Assert.Equal("Synthetic draft", view.DraftText);
            Assert.Equal(3, view.InputControl.SelectionStart);
            Assert.Equal(4, view.InputControl.SelectionLength);
            Assert.Equal(before, tasks.Children.Cast<UIElement>());
        }
        finally { owner.HideInlineDot(); owner.Close(); }
    });

    [Fact]
    public void Task_updates_and_expansion_state_remain_visible_below_the_top_reply() => Sta(() =>
    {
        var owner = new QuotaCapsuleWindow("inline-tasks", true);
        try
        {
            owner.ApplySessions(Sessions(4));
            owner.TogglePanelSessions();
            Assert.False(owner.PanelExpanded);
            owner.ShowInlineDot(new DotInlineView());
            var inlineHeight = owner.DesiredHeight;
            owner.ApplySessions(Sessions(6));
            Assert.Equal(inlineHeight, owner.DesiredHeight);
            Assert.False(owner.PanelExpanded);
            owner.HideInlineDot();
            Assert.False(owner.PanelExpanded);
            Assert.Equal(6, Assert.IsType<StackPanel>(owner.FindName("PanelSessionsList")).Children.Count);
            Assert.Equal(80 + QuotaCapsuleWindow.PanelSessionHeight(6, false), owner.DesiredHeight);
        }
        finally { owner.HideInlineDot(); owner.Close(); }
    });

    [Theory]
    [InlineData(true, 160)] [InlineData(true, 192)] [InlineData(true, 240)]
    [InlineData(false, 160)] [InlineData(false, 192)] [InlineData(false, 240)]
    public void Model_size_and_white_ball_fallback_keep_the_same_inline_view(bool unified, int size) => Sta(() =>
    {
        var owner = new QuotaCapsuleWindow("inline-model", unified);
        try
        {
            var view = new DotInlineView();
            owner.ShowInlineDot(view);
            var baseHeight = owner.DesiredHeight;
            owner.ConfigureBongoSurface(true, size);
            owner.PresentBongoFrame(new BongoFrame(1, 1, [255, 255, 255, 255], 4));
            Assert.True(owner.BongoHasFrame);
            Assert.True(owner.DesiredHeight > baseHeight);
            Assert.Same(owner, Window.GetWindow(view));
            owner.ClearBongoFrame();
            Assert.True(owner.DotInlineVisible);
            Assert.Equal(baseHeight, owner.DesiredHeight);
            Assert.Same(owner, Window.GetWindow(view));
        }
        finally { owner.HideInlineDot(); owner.Close(); }
    });

    [Fact]
    public void A_single_view_moves_to_another_card_without_recreating_its_draft() => Sta(() =>
    {
        var first = new QuotaCapsuleWindow("first", true);
        var second = new QuotaCapsuleWindow("second", true);
        try
        {
            var view = new DotInlineView();
            view.InputControl.Text = "Offline example";
            view.InputControl.Select(1, 3);
            first.ShowInlineDot(view);
            Assert.Throws<InvalidOperationException>(() => second.ShowInlineDot(view));
            first.HideInlineDot();
            second.ShowInlineDot(view);
            Assert.Same(second, Window.GetWindow(view));
            Assert.Equal("Offline example", view.DraftText);
            Assert.Equal(1, view.InputControl.SelectionStart);
            Assert.Equal(3, view.InputControl.SelectionLength);
        }
        finally { first.HideInlineDot(); second.HideInlineDot(); first.Close(); second.Close(); }
    });

    [Fact]
    public void Reminder_marker_and_draft_entry_never_grow_the_card() => Sta(() =>
    {
        var owner = new QuotaCapsuleWindow("marker", true);
        try
        {
            var height = owner.DesiredHeight;
            owner.SetDotMarker(3);
            Assert.True(owner.DotMarkerVisible);
            Assert.Equal(height, owner.DesiredHeight);
            owner.SetDotMarker(0, true);
            Assert.False(owner.DotMarkerVisible); // retained drafts no longer expose a reply entry
            owner.SetDotMarker(0);
            Assert.False(owner.DotMarkerVisible);
            Assert.Equal(height, owner.DesiredHeight);
        }
        finally { owner.Close(); }
    });

    [Theory]
    [InlineData(768, 1, 199, 233, true)]
    [InlineData(480, 1, 199, 233, true)]
    [InlineData(430, 1, 199, 233, false)]
    [InlineData(768, 2, 199, 233, false)]
    [InlineData(1080, 2, 199, 233, true)]
    [InlineData(1080, -1, 199, 233, false)]
    public void Inline_reply_respects_available_physical_height(int workHeight, double scale, double baseHeight, double replyHeight, bool fits) =>
        Assert.Equal(fits, OverlayCoordinator.CanFitInlineDot(workHeight, scale, baseHeight, replyHeight));

    private static CodexSessionsSnapshot Sessions(int count) => new(Enumerable.Range(0, count)
        .Select(index => new CodexSessionSnapshot("example-" + index, "Example task " + index,
            CodexSessionState.Running, DateTimeOffset.Parse("2026-10-24T12:00:00Z"))).ToArray(), DateTimeOffset.UtcNow);

    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { error = e; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Inline card test timed out.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
