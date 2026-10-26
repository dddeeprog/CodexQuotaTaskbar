using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.Overlay;
using CodexQuotaTaskbar.Host.UI;
using Button = System.Windows.Controls.Button;
using Size = System.Windows.Size;

namespace CodexQuotaTaskbar.Host.Tests.UI;

// These tests construct hidden synthetic controls only. Visible HWND / real timer
// coverage is in the task's isolated coordinator proof, never the user's process.
[Collection("Native UI isolation")]
public sealed class DotCatCompactTests
{
    [Theory]
    [InlineData("Short synthetic message.")]
    [InlineData("One\nTwo\nThree")]
    [InlineData("")]
    public void Cat_marker_open_is_a_temporary_summary_request_without_a_reader_or_reply(string text) => Sta(() =>
    {
        using var fixture = new Fixture();
        var state = State(1, text);
        fixture.Coordinator.ApplyInlineDotState(state);
        fixture.Coordinator.OpenInlineDot(fixture.Owner);
        var proof = Proof(fixture.Coordinator);
        Assert.False(proof.GetProperty("inlineWanted").GetBoolean());
        Assert.True(proof.GetProperty("notificationWanted").GetBoolean());
        Assert.False(proof.GetProperty("replyAllowed").GetBoolean());
        Assert.Null(Field<object?>(fixture.Coordinator, "inlineDotView"));
        Assert.Equal(state, Field<DotInlineState>(fixture.Coordinator, "inlineDotState"));
        Assert.Empty(fixture.Actions);
        Assert.Equal(fixture.Owner.BaseDesiredHeight, fixture.Owner.DesiredHeight);
        Assert.Null(fixture.Owner.DotTopContent); // hidden owner must not reserve a row
    });

    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(true, 0, false)]
    [InlineData(false, 1, true)]
    [InlineData(true, 1, false)]
    public void Cat_open_with_no_readable_unread_message_never_creates_an_empty_container(bool online, int count, bool unread) => Sta(() =>
    {
        using var fixture = new Fixture();
        var state = State(1, "Synthetic state") with
        {
            Online = online, UnreadCount = count,
            Messages = count == 0 ? [] : [new("received", "Synthetic state", null, unread)],
        };
        fixture.Coordinator.ApplyInlineDotState(state);
        fixture.Coordinator.OpenInlineDot(fixture.Owner);
        Assert.False(Proof(fixture.Coordinator).GetProperty("notificationWanted").GetBoolean());
        Assert.False(Proof(fixture.Coordinator).GetProperty("inlineWanted").GetBoolean());
        Assert.Null(fixture.Owner.DotTopContent);
        Assert.Equal(0, fixture.Owner.DotTopExtensionHeight);
        Assert.Null(Field<object?>(fixture.Coordinator, "inlineDotView"));
        Assert.Empty(fixture.Actions);
    });

    [Fact]
    public void Cat_replay_expiry_close_and_new_notice_preserve_all_unread_draft_and_pending_state() => Sta(() =>
    {
        using var fixture = new Fixture();
        var state = State(1, "Synthetic unread") with
        {
            Messages = [new("older", "Synthetic older unread", null, true), new("received", "Synthetic unread", null, true)],
            UnreadCount = 4, EarlierUnreadCount = 2,
            DraftText = "Synthetic unsent draft", ClientDraftRevision = 19,
            HasPendingAttempt = true, NeedsReview = true, AttemptState = "uncertain", LastRequestId = "synthetic-request",
        };
        fixture.Coordinator.ApplyInlineDotState(state);
        fixture.Coordinator.OpenInlineDot(fixture.Owner);
        SetField(fixture.Coordinator, "dotNotificationDeadline", TimeSpan.Zero);
        fixture.Coordinator.CheckDotNotificationTimeout(TimeSpan.FromSeconds(10));
        Assert.False(Proof(fixture.Coordinator).GetProperty("notificationWanted").GetBoolean());
        Assert.True(fixture.Owner.DotMarkerVisible);
        Assert.Equal(state, Field<DotInlineState>(fixture.Coordinator, "inlineDotState"));
        fixture.Coordinator.OpenInlineDot(fixture.Owner);
        Assert.True(Proof(fixture.Coordinator).GetProperty("notificationWanted").GetBoolean());
        fixture.Coordinator.CloseInlineDot();
        Assert.False(Proof(fixture.Coordinator).GetProperty("notificationWanted").GetBoolean());
        Assert.False(Proof(fixture.Coordinator).GetProperty("timeoutArmed").GetBoolean());
        var next = state with { Sequence = 2, NotificationId = "new", NotificationSerial = 2,
            Messages = [..state.Messages, new("new", "Synthetic new unread", null, true)], UnreadCount = 5 };
        fixture.Coordinator.ApplyInlineDotState(next);
        Assert.True(Proof(fixture.Coordinator).GetProperty("notificationWanted").GetBoolean());
        Assert.False(Proof(fixture.Coordinator).GetProperty("inlineWanted").GetBoolean());
        Assert.Equal(next, Field<DotInlineState>(fixture.Coordinator, "inlineDotState"));
        Assert.Empty(fixture.Actions);
    });

    [Fact]
    public void Repeated_snapshot_does_not_restart_cat_notice_but_a_new_notification_can() => Sta(() =>
    {
        using var fixture = new Fixture();
        var state = State(1, "Synthetic unread");
        fixture.Coordinator.ApplyInlineDotState(state);
        var deadline = TimeSpan.FromSeconds(10);
        SetField(fixture.Coordinator, "dotNotificationDeadline", deadline);
        fixture.Coordinator.ApplyInlineDotState(state with { Sequence = 2 });
        Assert.Equal(deadline, Field<TimeSpan?>(fixture.Coordinator, "dotNotificationDeadline"));
        fixture.Coordinator.CheckDotNotificationTimeout(deadline);
        fixture.Coordinator.ApplyInlineDotState(state with { Sequence = 3 });
        Assert.False(Proof(fixture.Coordinator).GetProperty("notificationWanted").GetBoolean());
        fixture.Coordinator.ApplyInlineDotState(state with { Sequence = 4, NotificationSerial = 2 });
        Assert.True(Proof(fixture.Coordinator).GetProperty("notificationWanted").GetBoolean());
        Assert.Null(Field<TimeSpan?>(fixture.Coordinator, "dotNotificationDeadline"));
        Assert.Empty(fixture.Actions);
    });

    [Theory]
    [InlineData(160d)] [InlineData(224d)] [InlineData(248d)]
    public void Long_cat_summary_and_click_stay_one_row_instead_of_allocating_body_space(double width) => Sta(() =>
    {
        using var fixture = new Fixture();
        fixture.Coordinator.ApplyInlineDotState(State(1, new string('文', 6000)));
        var notice = new DotTopNoticeView();
        notice.Display(new("received", string.Join("\n", Enumerable.Repeat(new string('文', 100), 60)), null, true), 8);
        notice.OpenRequested += (_, _) => fixture.Coordinator.OpenInlineDot(fixture.Owner);
        notice.Measure(new Size(width, notice.PreferredHeightDip));
        notice.Arrange(new Rect(0, 0, width, notice.PreferredHeightDip)); notice.UpdateLayout();
        Assert.Equal(36, notice.ActualHeight);
        Assert.InRange(notice.DesiredSize.Height, 1, 36);
        var text = Assert.Single(Descendants(notice).OfType<TextBlock>(), item => item.Text == notice.SummaryText);
        Assert.Equal(TextWrapping.NoWrap, text.TextWrapping);
        Assert.Equal(TextTrimming.CharacterEllipsis, text.TextTrimming);
        Assert.Empty(Descendants(notice).OfType<TextBox>());
        Assert.Empty(Descendants(notice).OfType<ScrollViewer>());
        Assert.Single(Descendants(notice).OfType<Button>())
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(Proof(fixture.Coordinator).GetProperty("inlineWanted").GetBoolean());
        Assert.Null(Field<object?>(fixture.Coordinator, "inlineDotView"));
        Assert.Empty(fixture.Actions);
    });

    [Fact]
    public void A_late_reply_attachment_in_cat_mode_cannot_construct_or_reserve_any_reader() => Sta(() =>
    {
        using var fixture = new Fixture();
        SetField(fixture.Coordinator, "inlineDotWanted", true);
        Invoke(fixture.Coordinator, "AttachInlineDot", fixture.Owner);
        Assert.False(Proof(fixture.Coordinator).GetProperty("inlineWanted").GetBoolean());
        Assert.Null(Field<object?>(fixture.Coordinator, "inlineDotView"));
        Assert.Null(fixture.Owner.DotTopContent);
        Assert.Equal(0, fixture.Owner.DotTopExtensionHeight);
        // The obsolete reader class is retained for historical proof sources only;
        // no coordinator field, constructor path or active mode can own it.
        Assert.DoesNotContain(typeof(OverlayCoordinator).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(DotReadOnlyView));
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Detaching_or_disposing_a_card_suppresses_synchronous_presentation_reentry(bool dispose) => Sta(() =>
    {
        using var fixture = new Fixture();
        var state = State(1, "Synthetic unread while rebuilding");
        fixture.Coordinator.ApplyInlineDotState(state);
        var notice = new DotTopNoticeView();
        notice.Display(state.Messages[0], state.UnreadCount);
        fixture.Owner.ShowDotTopExtension(notice, notice.PreferredHeightDip);
        SetField(fixture.Coordinator, "dotTopNotice", notice);
        SetField(fixture.Coordinator, "inlineDotOwner", fixture.Owner);
        var kindField = typeof(OverlayCoordinator).GetField("dotTopKind", BindingFlags.Instance | BindingFlags.NonPublic)!;
        kindField.SetValue(fixture.Coordinator, Enum.Parse(kindField.FieldType, "Notice"));
        var deadline = TimeSpan.FromSeconds(50);
        SetField(fixture.Coordinator, "dotNotificationDeadline", deadline);
        var callbacks = 0;
        fixture.Owner.PresentationChanged += (_, _) =>
        {
            callbacks++;
            Assert.True(Field<bool>(fixture.Coordinator, "dotPresentationUpdating"));
            // Hide raises this synchronously; no layout, bounds, queued open or
            // state refresh may put a child back into the retiring card.
            Invoke(fixture.Coordinator, "RefreshInlineDotPresentation");
            fixture.Coordinator.OpenInlineDot(fixture.Owner);
        };
        if (dispose) fixture.Coordinator.Dispose();
        else Invoke(fixture.Coordinator, "PrepareInlineDotOwnerForClose", fixture.Owner);
        Assert.Equal(1, callbacks);
        Assert.Null(notice.Parent);
        Assert.Null(fixture.Owner.DotTopContent);
        Assert.Null(Field<object?>(fixture.Coordinator, "inlineDotOwner"));
        Assert.False(Field<bool>(fixture.Coordinator, "dotPresentationUpdating"));
        Assert.Null(Field<object?>(fixture.Coordinator, "inlineDotView"));
        Assert.Empty(fixture.Actions);
        if (!dispose)
        {
            Assert.True(Proof(fixture.Coordinator).GetProperty("notificationWanted").GetBoolean());
            Assert.Equal(deadline, Field<TimeSpan?>(fixture.Coordinator, "dotNotificationDeadline"));
            Assert.Equal(state, Field<DotInlineState>(fixture.Coordinator, "inlineDotState"));
        }
    });

    private sealed class Fixture : IDisposable
    {
        internal OverlayCoordinator Coordinator { get; } = new(false, unifiedPanel: true, observePointer: false);
        internal QuotaCapsuleWindow Owner { get; } = new("cat-compact-hidden", true);
        internal List<DotInlineAction> Actions { get; } = [];
        internal Fixture()
        {
            Field<DispatcherTimer>(Coordinator, "timer").Stop();
            SetField(Coordinator, "bongoSelected", true);
            Field<Dictionary<string, QuotaCapsuleWindow>>(Coordinator, "windows").Add(Owner.MonitorId, Owner);
            Coordinator.SetInlineDotMode(true); Coordinator.SetDotPresentationEnabled(true);
            Coordinator.InlineDotActionRequested += Actions.Add;
        }
        public void Dispose() => Coordinator.Dispose();
    }

    private static DotInlineState State(long sequence, string text) => new(
        "synthetic-cat-session", sequence, 0, "", [new("received", text, null, true)],
        1, 0, "received", 1, true, false, false, false, true, false, "Synthetic connected");
    private static JsonElement Proof(OverlayCoordinator c) => JsonSerializer.SerializeToElement(c.InlineDotProofState());
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void SetField(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) { var child = VisualTreeHelper.GetChild(node, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Synthetic hidden cat test exceeded its bound.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
