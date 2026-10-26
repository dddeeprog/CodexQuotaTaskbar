using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

[Collection("Native UI isolation")]
public sealed class DotNoticeDisplayCacheTests
{
    [Fact]
    public void Repeated_body_uses_the_new_message_and_acknowledgement_watermark() => Sta(() =>
    {
        var view = new DotTopNoticeView(); var first = State();
        view.Display(new("old", "Same synthetic body", null, true), first);
        var next = first with { SessionId = Guid.NewGuid().ToString("N"), ReadToken = Guid.NewGuid().ToString("N"), ReadSequence = 24 };
        view.Display(new("new", "Same synthetic body", null, true), next);
        var action = Assert.IsType<DotInlineAction>(view.CreateAcknowledgement(singleDisplayedMessage: true));
        Assert.Equal("new", action.MessageId); Assert.Equal(next.ReadToken, action.ReadToken);
        Assert.Equal(next.SessionId, action.ReadSessionId); Assert.Equal(24, action.ReadSequence);
        view.Display(new("integer-only", "Same synthetic body", null, true), 2);
        Assert.Equal("integer-only", view.DisplayedMessageId); Assert.Null(view.CreateAcknowledgement());
    });

    [Fact]
    public void Width_content_and_font_changes_recompute_completeness() => Sta(() =>
    {
        var view = new DotTopNoticeView();
        view.Display(new("one", "WWWWWWWW", null, true), 1); Arrange(view, 280);
        Assert.True(view.CanDisplayEntireText(120)); Assert.False(view.CanDisplayEntireText(1));
        Assert.True(view.CanDisplayEntireText(120));
        var summary = Descendants(view).OfType<TextBlock>().Single(t => t.Text == view.SummaryText);
        summary.FontSize = 36;
        Assert.False(view.CanDisplayEntireText(120));
        summary.FontSize = 12;
        Assert.True(view.CanDisplayEntireText(120));
        view.Display(new("two", new string('W', 80), null, true), 1);
        Assert.False(view.CanDisplayEntireText(120));
        view.Display(new("three", "Short", null, true), 1);
        Assert.True(view.CanDisplayEntireText(120));
    });

    [Fact]
    public void Earlier_mode_keeps_new_tokens_and_does_not_leak_into_normal_hints_after_dpi_or_contrast_change() => Sta(() =>
    {
        var view = new DotTopNoticeView(); var state = State() with { EarlierUnreadCount = 4 };
        view.DisplayEarlier(state); Arrange(view, 280);
        var newer = state with { ReadToken = Guid.NewGuid().ToString("N"), ReadSequence = 55 };
        view.DisplayEarlier(newer);
        Assert.Null(view.DisplayedMessageId); Assert.False(view.CanDisplayEntireText(10000));
        Assert.Null(view.CreateAcknowledgement(singleDisplayedMessage: true));
        var batch = Assert.IsType<DotInlineAction>(view.CreateAcknowledgement());
        Assert.Equal(newer.ReadToken, batch.ReadToken); Assert.Equal(55, batch.ReadSequence);
        VisualTreeHelper.SetRootDpi(view, new DpiScale(1.5, 1.5));
        view.ApplyHighContrast(true); view.FlowDirection = FlowDirection.RightToLeft;
        view.Display(new("normal", "Synthetic short", null, true), newer);
        Arrange(view, 280);
        Assert.Equal("normal", view.DisplayedMessageId); Assert.True(view.CanDisplayEntireText(10000));
        var button = Assert.Single(Descendants(view).OfType<Button>());
        Assert.DoesNotContain("更早", Assert.IsType<string>(button.ToolTip));
        Assert.Equal(newer.ReadToken, view.CreateAcknowledgement(singleDisplayedMessage: true)!.ReadToken);
        view.ApplyHighContrast(false); VisualTreeHelper.SetRootDpi(view, new DpiScale(1, 1));
        Arrange(view, 280); Assert.True(view.CanDisplayEntireText(10000));
    });

    private static DotInlineState State() => new(Guid.NewGuid().ToString("N"), 1, 0, "", [], 2, 0, null, 0,
        true, false, false, false, true, false, "synthetic", ReplyAllowed: false,
        ReadToken: Guid.NewGuid().ToString("N"), ReadSequence: 3);

    private static void Arrange(FrameworkElement view, double width)
    { view.Measure(new(width, 36)); view.Arrange(new(0, 0, width, 36)); view.UpdateLayout(); }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "The isolated control check must finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
