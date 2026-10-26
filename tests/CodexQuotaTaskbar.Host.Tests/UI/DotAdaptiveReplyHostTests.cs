using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.Overlay;
using CodexQuotaTaskbar.Host.UI;
using Size = System.Windows.Size;

namespace CodexQuotaTaskbar.Host.Tests.UI;

[Collection("Native UI isolation")]
public sealed class DotAdaptiveReplyHostTests
{
    [Theory]
    [InlineData(true, 224d)]
    // Classic: 306 - asymmetric glass margins (12 + 22) - border (0.5 + 0.5) - reply margins 24.
    [InlineData(false, 247d)]
    public void Initial_content_measure_uses_the_real_reply_slot_width(bool unified, double width) => Sta(() =>
    {
        var owner = new QuotaCapsuleWindow("adaptive-width", unified);
        try
        {
            Assert.Equal(width, owner.DotReplyContentWidthDip, 3);
            var view = new DotInlineView();
            view.ApplyState(State(Guid.NewGuid().ToString("N"), 1, view, "A short received message."));
            owner.ShowInlineDot(view); Arrange(owner);
            Assert.Equal(view.PreferredHeightDip + 1, owner.DotTopExtensionHeight, 3);
            Assert.True(view.PreferredHeightDip < DotInlineView.ViewHeightDip);
            Assert.Same(view, owner.DotTopContent);
        }
        finally { owner.HideDotTopExtension(); owner.Close(); }
    });

    [Fact]
    public void Attached_reply_grows_and_shrinks_without_reopening_or_replacing_tasks() => Sta(() =>
    {
        var owner = new QuotaCapsuleWindow("adaptive-content", true);
        try
        {
            owner.ApplySessions(Sessions());
            var tasks = ((StackPanel)owner.FindName("PanelSessionsList")).Children.Cast<UIElement>().ToArray();
            var view = new DotInlineView();
            var session = Guid.NewGuid().ToString("N");
            view.ApplyState(State(session, 1, view, "Short reply."));
            owner.ShowInlineDot(view); Arrange(owner);
            var compact = owner.DotTopExtensionHeight;
            var visualParent = view.Parent;
            var selected = view.SelectedMessageId;
            var baseHeight = owner.BaseDesiredHeight;

            view.InputControl.Text = string.Join("\n", Enumerable.Range(0, 15).Select(i => $"Synthetic draft line {i}"));
            view.InputControl.Select(3, 5);
            view.ApplyState(State(session, 2, view, string.Concat(Enumerable.Repeat("A longer received message with several wrapped lines. ", 40))));
            view.RecalculatePreferredHeight(owner.DotReplyContentWidthDip); Arrange(owner);
            Assert.True(owner.DotTopExtensionHeight > compact);
            Assert.InRange(view.PreferredHeightDip, 1, DotInlineView.ViewHeightDip);
            Assert.Same(view, owner.DotTopContent);
            Assert.Same(visualParent, view.Parent);
            Assert.Equal(selected, view.SelectedMessageId);
            Assert.Equal(3, view.InputControl.SelectionStart);
            Assert.Equal(5, view.InputControl.SelectionLength);
            Assert.Equal(baseHeight, owner.BaseDesiredHeight);
            Assert.Equal(baseHeight + view.PreferredHeightDip + 1, owner.DesiredHeight, 3);
            Assert.Equal(Visibility.Visible, ((Grid)owner.FindName("PanelSessionsRegion")).Visibility);
            Assert.Equal(tasks, ((StackPanel)owner.FindName("PanelSessionsList")).Children.Cast<UIElement>());

            view.InputControl.Text = "Short draft";
            view.InputControl.Select(1, 4);
            view.ApplyState(State(session, 3, view, "Short reply."));
            view.RecalculatePreferredHeight(owner.DotReplyContentWidthDip); Arrange(owner);
            Assert.Equal(compact, owner.DotTopExtensionHeight, 3);
            Assert.Same(view, owner.DotTopContent);
            Assert.Same(visualParent, view.Parent);
            Assert.Equal(1, view.InputControl.SelectionStart);
            Assert.Equal(4, view.InputControl.SelectionLength);
            Assert.Equal(tasks, ((StackPanel)owner.FindName("PanelSessionsList")).Children.Cast<UIElement>());
            Assert.False(view.IsKeyboardFocusWithin);
        }
        finally { owner.HideDotTopExtension(); owner.Close(); }
    });

    [Fact]
    public void Edits_within_the_same_input_line_budget_do_not_resize_the_card() => Sta(() =>
    {
        var owner = new QuotaCapsuleWindow("adaptive-stable", true);
        try
        {
            var view = new DotInlineView();
            view.ApplyState(State(Guid.NewGuid().ToString("N"), 1, view, "Short received message."));
            owner.ShowInlineDot(view); Arrange(owner);
            var parent = view.Parent;
            var height = owner.DesiredHeight;
            var notifications = 0;
            owner.PresentationChanged += (_, _) => notifications++;
            foreach (var draft in new[] { "a", "ab", "abc", "abcd" })
            {
                view.InputControl.Text = draft;
                view.RecalculatePreferredHeight(owner.DotReplyContentWidthDip);
                Arrange(owner);
            }
            Assert.Equal(0, notifications);
            Assert.Equal(height, owner.DesiredHeight);
            Assert.Same(parent, view.Parent);
            Assert.Same(view, owner.DotTopContent);
        }
        finally { owner.HideDotTopExtension(); owner.Close(); }
    });

    [Fact]
    public void Hidden_reply_size_changes_do_not_reopen_it_or_modify_the_cat_notice() => Sta(() =>
    {
        var owner = new QuotaCapsuleWindow("adaptive-hidden", true);
        try
        {
            var view = new DotInlineView();
            view.InputControl.Text = "Saved synthetic draft";
            view.InputControl.Select(2, 4);
            owner.ShowInlineDot(view); owner.HideDotTopExtension();
            var notice = new DotTopNoticeView();
            owner.ShowDotTopExtension(notice, notice.PreferredHeightDip);
            var catHeight = owner.DesiredHeight;
            view.InputControl.Text += "\nMore synthetic draft lines\nAnother line\nFinal line";
            view.InputControl.Select(2, 4);
            view.RecalculatePreferredHeight(owner.DotReplyContentWidthDip);
            Assert.Same(notice, owner.DotTopContent);
            Assert.Equal(catHeight, owner.DesiredHeight);
            Assert.False(owner.DotInlineVisible);
            owner.HideDotTopExtension();
            Assert.Equal(owner.BaseDesiredHeight, owner.DesiredHeight);
            view.InputControl.Text = "Saved synthetic draft";
            view.InputControl.Select(2, 4);
            view.RecalculatePreferredHeight(owner.DotReplyContentWidthDip);
            Assert.Null(owner.DotTopContent);
            owner.ShowInlineDot(view); Arrange(owner);
            Assert.Same(view, owner.DotTopContent);
            Assert.Equal(2, view.InputControl.SelectionStart);
            Assert.Equal(4, view.InputControl.SelectionLength);
        }
        finally { owner.HideDotTopExtension(); owner.Close(); }
    });

    [Theory]
    [InlineData(1d)]
    [InlineData(1.25d)]
    [InlineData(1.5d)]
    [InlineData(2d)]
    public void Content_growth_and_shrink_keep_the_base_anchor_at_the_top_edge(double scale) => Sta(() =>
    {
        var view = new DotInlineView();
        var session = Guid.NewGuid().ToString("N");
        view.ApplyState(State(session, 1, view, "Short reply."));
        view.RecalculatePreferredHeight(224);
        var compact = (int)Math.Round((view.PreferredHeightDip + 1) * scale);
        view.InputControl.Text = string.Join("\n", Enumerable.Repeat("Multiple synthetic draft lines", 15));
        view.ApplyState(State(session, 2, view, string.Concat(Enumerable.Repeat("Long synthetic reply. ", 50))));
        view.RecalculatePreferredHeight(224);
        var expanded = (int)Math.Round((view.PreferredHeightDip + 1) * scale);
        Assert.True(expanded > compact);
        var work = new ScreenRect(-1920, -1080, 0, 1000);
        var baseBounds = new ScreenRect(-1400, -1060, -1400 + (int)Math.Round(280 * scale), -1060 + (int)Math.Round(200 * scale));
        var first = OverlayCoordinator.CalculateTopExtensionBounds(work, baseBounds, compact);
        var bigger = OverlayCoordinator.CalculateTopExtensionBounds(work, baseBounds, expanded);
        Assert.Equal(work.Top, first.Top);
        Assert.Equal(work.Top, bigger.Top);
        Assert.Equal(baseBounds.Height + expanded, bigger.Height);
        Assert.Equal(first, OverlayCoordinator.CalculateTopExtensionBounds(work, baseBounds, compact));
        Assert.Equal(baseBounds, OverlayCoordinator.CalculateTopExtensionBounds(work, baseBounds, 0));
    });

    private static void Arrange(QuotaCapsuleWindow owner)
    {
        var content = (FrameworkElement)owner.Content;
        content.Measure(new Size(owner.LayoutWidth, owner.DesiredHeight));
        content.Arrange(new Rect(0, 0, owner.LayoutWidth, owner.DesiredHeight));
        content.UpdateLayout();
    }

    private static DotInlineState State(string session, long sequence, DotInlineView view, string message) => new(
        session, sequence, view.ClientDraftRevision, view.DraftText,
        [new("received", message, DateTimeOffset.Parse("2026-10-24T12:00:00Z"), true)],
        1, 0, null, 0, true, false, false, false, true, false, "Connected");

    private static CodexSessionsSnapshot Sessions() => new(Enumerable.Range(0, 3)
        .Select(i => new CodexSessionSnapshot("task-" + i, "Synthetic task", CodexSessionState.Running, DateTimeOffset.UtcNow))
        .ToArray(), DateTimeOffset.UtcNow);

    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { error = e; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
