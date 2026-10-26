using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Host.Bongo;
using CodexQuotaTaskbar.Host.Overlay;
using CodexQuotaTaskbar.Host.UI;
using Size = System.Windows.Size;
using Point = System.Windows.Point;

namespace CodexQuotaTaskbar.Host.Tests.UI;

[Collection("Native UI isolation")]
public sealed class DotTopExtensionLayoutTests
{
    [Theory]
    [InlineData(1d)] [InlineData(1.5d)] [InlineData(2d)]
    public void A_top_row_extends_upward_and_keeps_the_base_bottom(double scale)
    {
        var work = new ScreenRect(-1920, -200, 0, 1200);
        var original = new ScreenRect(-1500, 200, -1500 + (int)(280 * scale), 200 + (int)(320 * scale));
        var extra = (int)Math.Round(37 * scale);
        var expanded = OverlayCoordinator.CalculateTopExtensionBounds(work, original, extra);
        Assert.Equal(original.Bottom, expanded.Bottom);
        Assert.Equal(original.Top - extra, expanded.Top);
        Assert.Equal(original.Left, expanded.Left);
        // Reconciliation always receives the same base, not its previous expanded result.
        Assert.Equal(expanded, OverlayCoordinator.CalculateTopExtensionBounds(work, original, extra));
        Assert.Equal(original, OverlayCoordinator.CalculateTopExtensionBounds(work, original, 0));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1080)]
    public void A_top_edge_moves_the_whole_card_down_only_as_much_as_needed(int workTop)
    {
        var work = new ScreenRect(-1920, workTop, 0, workTop + 900);
        var original = new ScreenRect(-1500, workTop + 10, -1220, workTop + 329);
        var expanded = OverlayCoordinator.CalculateTopExtensionBounds(work, original, 37);
        Assert.Equal(work.Top, expanded.Top);
        Assert.Equal(original.Height + 37, expanded.Height);
        Assert.Equal(27, expanded.Bottom - original.Bottom);
        Assert.Equal(original, OverlayCoordinator.CalculateTopExtensionBounds(work, original, 0));
    }

    [Fact]
    public void Top_extension_keeps_room_for_the_existing_classic_task_stack()
    {
        var work = new ScreenRect(0, 0, 1000, 800);
        var original = new ScreenRect(500, 600, 806, 712);
        var result = OverlayCoordinator.CalculateTopExtensionBounds(work, original, 181, 120);
        Assert.Equal(680, result.Bottom);
        Assert.Equal(120, work.Bottom - result.Bottom);
        Assert.Equal(293, result.Height);
    }

    [Fact]
    public void Too_small_physical_space_is_rejected_instead_of_cropping_a_reminder()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OverlayCoordinator.CalculateTopExtensionBounds(
            new(0, 0, 280, 300), new(0, 0, 280, 290), 37));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Notice_is_above_the_original_body_and_tasks_stay_in_the_tree(bool modelFirst) => Sta(() =>
    {
        var owner = new QuotaCapsuleWindow("top-notice", true);
        try
        {
            owner.ApplySessions(Sessions());
            var list = (StackPanel)owner.FindName("PanelSessionsList");
            var tasks = list.Children.Cast<UIElement>().ToArray();
            var before = owner.BaseDesiredHeight;
            if (modelFirst) { owner.ConfigureBongoSurface(true, 192); owner.PresentBongoFrame(Frame()); before = owner.BaseDesiredHeight; }
            var notice = new DotTopNoticeView();
            owner.ShowDotTopExtension(notice, notice.PreferredHeightDip);
            if (!modelFirst) { owner.ConfigureBongoSurface(true, 192); owner.PresentBongoFrame(Frame()); before = owner.BaseDesiredHeight; }
            var content = (FrameworkElement)owner.Content;
            content.Measure(new Size(owner.LayoutWidth, owner.DesiredHeight));
            content.Arrange(new Rect(0, 0, owner.LayoutWidth, owner.DesiredHeight)); content.UpdateLayout();
            Assert.Same(owner, Window.GetWindow(notice));
            Assert.Equal(37, owner.DotTopExtensionHeight);
            Assert.Equal(before + 37, owner.DesiredHeight, 5);
            Assert.Equal(Visibility.Visible, ((Grid)owner.FindName("PanelSessionsRegion")).Visibility);
            Assert.Equal(tasks, list.Children.Cast<UIElement>());
            var noticeTop = notice.TransformToAncestor(content).Transform(new Point(0, 0)).Y;
            var quotaTop = ((Grid)owner.FindName("QuotaHeader")).TransformToAncestor(content).Transform(new Point(0, 0)).Y;
            Assert.True(quotaTop > noticeTop + notice.PreferredHeightDip);
            owner.ClearBongoFrame();
            Assert.Same(notice, owner.DotTopContent);
            Assert.True(owner.DotTopExtensionVisible);
            owner.HideDotTopExtension();
            Assert.Equal(0, owner.DotTopExtensionHeight);
            Assert.Equal(owner.BaseDesiredHeight, owner.DesiredHeight);
            Assert.Equal(tasks, list.Children.Cast<UIElement>());
        }
        finally { owner.HideDotTopExtension(); owner.Close(); }
    });

    [Fact]
    public void A_saved_white_reply_can_be_hidden_for_a_cat_notice_without_losing_its_draft() => Sta(() =>
    {
        var owner = new QuotaCapsuleWindow("top-switch", true);
        try
        {
            var reply = new DotInlineView(); reply.InputControl.Text = "Synthetic unsent draft"; reply.InputControl.Select(2, 4);
            owner.ShowInlineDot(reply); owner.HideDotTopExtension();
            var notice = new DotTopNoticeView(); owner.ShowDotTopExtension(notice, notice.PreferredHeightDip);
            Assert.False(owner.DotInlineVisible); Assert.Same(notice, owner.DotTopContent);
            Assert.Empty(Descendants(notice).OfType<TextBox>());
            owner.HideDotTopExtension(); owner.ShowInlineDot(reply);
            Assert.Equal("Synthetic unsent draft", reply.DraftText); Assert.Equal(2, reply.InputControl.SelectionStart); Assert.Equal(4, reply.InputControl.SelectionLength);
        }
        finally { owner.HideDotTopExtension(); owner.Close(); }
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) { var child = VisualTreeHelper.GetChild(node, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private static BongoFrame Frame() => new(1, 1, [255, 255, 255, 255], 4);
    private static CodexSessionsSnapshot Sessions() => new(Enumerable.Range(0, 3).Select(i => new CodexSessionSnapshot("task-" + i, "Example task", CodexSessionState.Running, DateTimeOffset.UtcNow)).ToArray(), DateTimeOffset.UtcNow);
    private static void Sta(Action action)
    {
        Exception? error = null; var thread = new Thread(() => { try { action(); } catch (Exception e) { error = e; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
