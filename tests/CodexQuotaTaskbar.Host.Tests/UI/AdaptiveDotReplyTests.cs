using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.UI;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace CodexQuotaTaskbar.Host.Tests.UI;

[Collection("Native UI isolation")]
public sealed class AdaptiveDotReplyTests
{
    [Theory]
    [InlineData(192)] [InlineData(224)]
    public void Empty_and_short_replies_use_only_their_native_content_height(double width) => Sta(() =>
    {
        var empty = new DotInlineView(); empty.ApplyState(State() with { Status = "" }); Layout(empty, width);
        Assert.Equal(Visibility.Collapsed, empty.MessageScroller.Visibility);
        Assert.Equal(0, empty.MessageViewportHeightDip);
        Assert.InRange(empty.PreferredHeightDip, 100, 135);
        Assert.InRange(empty.InputViewportHeightDip, 36, 40);
        AssertActionsFit(empty);

        var shortReply = new DotInlineView(); shortReply.ApplyState(State(messages: [Message("short", "Synthetic short reply")]));
        Layout(shortReply, width);
        Assert.InRange(shortReply.MessageViewportHeightDip, 18, 44);
        Assert.True(shortReply.PreferredHeightDip > empty.PreferredHeightDip);
        Assert.InRange(shortReply.PreferredHeightDip, 130, 180);
        AssertActionsFit(shortReply);
    });

    [Fact]
    public void Long_messages_are_bounded_and_scroll_instead_of_creating_a_tall_blank_shell() => Sta(() =>
    {
        var view = new DotInlineView();
        view.ApplyState(State(messages: [Message("long", string.Join("\n", Enumerable.Repeat("Synthetic long message line", 90)))]));
        Layout(view, 224);
        Assert.InRange(view.MessageViewportHeightDip, 22, 72);
        Assert.InRange(view.PreferredHeightDip, 170, 232);
        Assert.True(view.MessageScroller.ScrollableHeight > view.MessageViewportHeightDip);
        view.MessageScroller.ScrollToBottom(); view.UpdateLayout();
        Assert.True(view.MessageScroller.VerticalOffset > 0);
        AssertActionsFit(view);
    });

    [Fact]
    public void Input_grows_by_wrapped_lines_then_scrolls_and_shrinks_without_losing_selection() => Sta(() =>
    {
        var view = new DotInlineView(); view.ApplyState(State()); Layout(view, 224);
        var emptyHeight = view.PreferredHeightDip;
        view.InputControl.Text = "one\ntwo\nthree"; Layout(view, 224);
        Assert.True(view.PreferredHeightDip > emptyHeight);
        Assert.InRange(view.InputViewportHeightDip, 50, 56);
        view.InputControl.Text = string.Join("\n", Enumerable.Repeat("Synthetic input line", 40));
        view.InputControl.Select(4, 8); var revision = view.ClientDraftRevision; Layout(view, 224);
        Assert.InRange(view.InputViewportHeightDip, 68, 72);
        Assert.True(view.InputControl.ExtentHeight > view.InputControl.ViewportHeight);
        Assert.InRange(view.PreferredHeightDip, emptyHeight + 1, 232);
        Assert.Equal(4, view.InputControl.SelectionStart); Assert.Equal(8, view.InputControl.SelectionLength);
        Assert.Equal(revision, view.ClientDraftRevision);
        view.InputControl.Text = "one line"; view.InputControl.Select(1, 3); Layout(view, 224);
        Assert.Equal(emptyHeight, view.PreferredHeightDip);
        Assert.Equal(1, view.InputControl.SelectionStart); Assert.Equal(3, view.InputControl.SelectionLength);
        AssertActionsFit(view);
    });

    [Fact]
    public void A_tight_screen_limits_optional_content_without_hiding_or_clipping_the_composer() => Sta(() =>
    {
        var view = new DotInlineView(); view.ApplyState(State()); Layout(view, 224);
        var floor = view.MinimumRequiredHeightDip;
        Assert.True(view.SetAvailableHeight(floor + 2));
        view.InputControl.Text = string.Join("\n", Enumerable.Repeat("Synthetic retained input", 30));
        view.InputControl.Select(3, 5); var revision = view.ClientDraftRevision;
        view.ApplyState(State(sequence: 2, messages: [Message("new", new string('文', 5000))]) with
        { DraftText = view.DraftText, ClientDraftRevision = revision, HasPendingAttempt = true });
        Layout(view, 224);
        Assert.Equal(floor, view.MinimumRequiredHeightDip);
        Assert.True(view.SetAvailableHeight(floor + 2));
        Assert.InRange(view.PreferredHeightDip, floor, floor + 2);
        Assert.InRange(view.InputViewportHeightDip, 36, 40);
        Assert.Equal(3, view.InputControl.SelectionStart); Assert.Equal(5, view.InputControl.SelectionLength);
        Assert.Equal(revision, view.ClientDraftRevision); Assert.False(view.SendControl.IsEnabled);
        AssertActionsFit(view);
        var before = view.PreferredHeightDip;
        Assert.False(view.SetAvailableHeight(floor - 1)); Assert.Equal(before, view.PreferredHeightDip);
        Assert.True(view.SetAvailableHeight(232)); Layout(view, 224);
        Assert.True(view.PreferredHeightDip > before);
        Assert.True(view.MessageScroller.ScrollableHeight > 0);
    });

    [Fact]
    public void Same_line_edits_and_unchanged_state_do_not_emit_reposition_events() => Sta(() =>
    {
        var view = new DotInlineView(); view.ApplyState(State()); Layout(view, 224);
        var changes = 0; view.PreferredHeightChanged += (_, _) => changes++;
        foreach (var text in new[] { "a", "ab", "abc", "same line" }) view.InputControl.Text = text;
        view.ApplyState(State(sequence: 2) with { DraftText = view.DraftText, ClientDraftRevision = view.ClientDraftRevision });
        Assert.Equal(0, changes);
        view.InputControl.Text = "one\ntwo\nthree";
        Assert.Equal(1, changes);
        var height = view.PreferredHeightDip;
        view.RecalculatePreferredHeight(224); Assert.Equal(1, changes); Assert.Equal(height, view.PreferredHeightDip);
    });

    [Fact]
    public void Narrow_width_remeasures_native_wrapping_without_resetting_draft_or_focus() => Sta(() =>
    {
        var view = new DotInlineView(); view.ApplyState(State());
        view.InputControl.Text = new string('文', 31); view.InputControl.Select(2, 7); Layout(view, 224);
        var wideHeight = view.PreferredHeightDip; var revision = view.ClientDraftRevision;
        Layout(view, 192);
        Assert.True(view.PreferredHeightDip >= wideHeight);
        Assert.InRange(view.PreferredHeightDip, 100, 232);
        Assert.Equal(2, view.InputControl.SelectionStart); Assert.Equal(7, view.InputControl.SelectionLength);
        Assert.Equal(revision, view.ClientDraftRevision); Assert.False(view.IsKeyboardFocusWithin);
        AssertActionsFit(view);
    });

    [Fact]
    public void Stale_receipt_cannot_shrink_a_new_multiline_draft_or_reenable_pending_send() => Sta(() =>
    {
        var view = new DotInlineView(); view.ApplyState(State()); view.InputControl.Text = "Synthetic earlier text";
        var sentRevision = view.ClientDraftRevision;
        view.InputControl.Text = "new one\nnew two\nnew three"; view.InputControl.Select(3, 5); Layout(view, 224);
        var height = view.PreferredHeightDip; var revision = view.ClientDraftRevision;
        view.ApplyState(State(sequence: 2) with { DraftText = "", ClientDraftRevision = sentRevision, HasPendingAttempt = true });
        Layout(view, 224);
        Assert.Equal("new one\nnew two\nnew three", view.DraftText);
        Assert.Equal(height, view.PreferredHeightDip); Assert.Equal(revision, view.ClientDraftRevision);
        Assert.Equal(3, view.InputControl.SelectionStart); Assert.Equal(5, view.InputControl.SelectionLength);
        Assert.False(view.SendControl.IsEnabled);
    });

    private static void AssertActionsFit(DotInlineView view)
    {
        Assert.InRange(view.SendControl.ActualHeight, 26, 27);
        // Offscreen roots inherit IsVisible=false from an unshown owner. Verify
        // the actual local presentation chain and arranged bounds without Show().
        var buttons = Descendants(view).OfType<Button>().Where(button => LocallyPresented(button, view)).ToArray();
        Assert.Contains(buttons, button => AutomationProperties.GetName(button) == "收起回复框，保留草稿和接收");
        foreach (var button in buttons)
        {
            var location = button.TransformToAncestor(view).Transform(new Point());
            Assert.InRange(location.Y, -.1, view.PreferredHeightDip);
            Assert.InRange(location.Y + button.ActualHeight, 0, view.PreferredHeightDip + .1);
        }
    }
    private static bool LocallyPresented(DependencyObject element, DependencyObject root)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement ui && ui.Visibility != Visibility.Visible) return false;
            if (ReferenceEquals(current, root)) return true;
        }
        return false;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static DotInlineMessage Message(string id, string text) => new(id, text, DateTimeOffset.UnixEpoch, true);
    private static DotInlineState State(long sequence = 1, DotInlineMessage[]? messages = null) => new(
        "synthetic-adaptive-session", sequence, 0, "", messages ?? [], messages?.Length ?? 0,
        0, null, 0, true, false, false, false, true, false, "已连接");
    private static void Layout(DotInlineView view, double width)
    {
        view.RecalculatePreferredHeight(width); view.Measure(new Size(width, view.PreferredHeightDip));
        view.Arrange(new Rect(0, 0, width, view.PreferredHeightDip)); view.UpdateLayout();
    }
    private static void Sta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try { test(); } catch (Exception error) { failure = error; } finally { dispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Synthetic adaptive-view proof timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
