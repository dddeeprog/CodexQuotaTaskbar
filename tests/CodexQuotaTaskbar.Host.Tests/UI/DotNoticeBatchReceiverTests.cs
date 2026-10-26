using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.Provider;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

[Collection("Native UI isolation")]
public sealed class DotNoticeBatchReceiverTests : IDisposable
{
    private static string TestRoot => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DianDao.NoticeBatchReceiverTests"));
    private readonly string directory = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));

    public DotNoticeBatchReceiverTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void Real_receiver_batch_action_keeps_draft_pending_guard_and_persisted_message_history() => Sta(async () =>
    {
        var guardPath = Path.Combine(directory, "synthetic-send-guard.json");
        var guard = new DotInlineSendGuard(guardPath);
        var checkpoint = new DotInlineSendCheckpoint(true, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            7, Guid.NewGuid().ToString("N"));
        await guard.SaveAsync(checkpoint);
        var guardBefore = await File.ReadAllBytesAsync(guardPath);
        var store = new DotUnreadStore(Path.Combine(directory, "synthetic-unread.json"));
        var window = new DotChatWindow(inline: true, unreadStore: store);
        try
        {
            // Exercise the production receiver action and persistence paths,
            // without Start/Connect, HWNDs, a browser or stdout pipe traffic.
            Set(window, "inlineStatePosted", true);
            Set(window, "inlineStore", store);
            ((TextBox)window.FindName("Input")).Text = "synthetic preserved native draft";
            Set(window, "pendingText", "synthetic in-flight attempt");
            Set(window, "needsReview", true);
            Set(window, "attemptState", "unknown");
            var inbox = Get<DotUnreadInbox>(window, "inlineInbox");
            inbox.ObserveNotification(Message("first"));
            inbox.ObserveNotification(Message("second"));
            await store.SaveAsync(inbox.Capture());
            var before = window.InlineSnapshot();
            var action = new DotInlineAction("ack-notice", ReadToken: before.ReadToken,
                ReadSessionId: before.SessionId, ReadSequence: before.ReadSequence);
            Assert.True(DotInlineWire.ValidAction(action));
            Assert.Null(action.MessageId);

            Invoke(window, "HandleInlineAction", action with { ReadSessionId = Guid.NewGuid().ToString("N") });
            Assert.Equal(2, inbox.UnreadCount);
            Invoke(window, "HandleInlineAction", action);
            await Get<Task>(window, "inlineSaveTask");
            var after = window.InlineSnapshot();

            Assert.Equal(0, after.UnreadCount);
            Assert.Equal(2, after.Messages.Length);
            Assert.Equal(before.Messages.Select(item => item with { Unread = false }), after.Messages);
            Assert.Equal(before.DraftText, after.DraftText);
            Assert.Equal(before.ClientDraftRevision, after.ClientDraftRevision);
            Assert.True(after.HasPendingAttempt);
            Assert.True(after.NeedsReview);
            Assert.Equal(before.AttemptState, after.AttemptState);
            Assert.False(after.ReplyAllowed);
            Assert.False(after.CanSubmit);
            Assert.Equal(guardBefore, await File.ReadAllBytesAsync(guardPath));
            Assert.Equal(checkpoint, await guard.LoadAsync());

            var restored = new DotUnreadInbox();
            restored.Restore(Assert.IsType<DotUnreadSnapshot>(await store.LoadAsync()));
            Assert.Equal(0, restored.UnreadCount);
            Assert.Equal(2, restored.Messages.Count);
            Assert.False(restored.ObserveNotification(Message("first")));
            Assert.True(restored.ObserveNotification(Message("later")));
            Assert.Equal(1, restored.UnreadCount);
            Assert.False(window.IsVisible);
            Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Handle);
            Assert.Null(window.Browser.CoreWebView2);
        }
        finally { window.Stop(); }
    });

    [Fact]
    public void An_offline_parent_does_not_clear_local_unread_or_change_an_existing_send_checkpoint() => Sta(async () =>
    {
        var guardPath = Path.Combine(directory, "offline-send-guard.json");
        var guard = new DotInlineSendGuard(guardPath);
        await guard.SaveAsync(new(true, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            4, Guid.NewGuid().ToString("N")));
        var checkpointBefore = await File.ReadAllBytesAsync(guardPath);
        var writes = 0;
        using var receiver = new DotChatProcess(inline: true, sendGuardPath: guardPath,
            persistCheckpoint: _ => { writes++; return Task.CompletedTask; });
        var session = Guid.NewGuid().ToString("N");
        var inbox = new DotUnreadInbox(); inbox.ObserveNotification(Message("first"));
        var registry = new DotUnreadAcknowledgements(inbox); var token = registry.Issue(8)!;
        var state = new DotInlineState(session, 99, 3, "synthetic preserved draft",
            [new("first", "synthetic first", DateTimeOffset.UtcNow, true)], 1, 0, "first", 1,
            false, false, true, true, true, false, "synthetic offline", Online: false,
            ReplyAllowed: false, ReadToken: token, ReadSequence: 8);
        Invoke(receiver, "AcceptState", state);
        var before = receiver.LatestState!;

        var action = new DotInlineAction("ack-notice", ReadToken: token, ReadSessionId: session, ReadSequence: 8);
        Assert.False(await receiver.SendInlineActionAsync(action));

        Assert.Equal(1, inbox.UnreadCount);
        Assert.Equal(before, receiver.LatestState);
        Assert.False(receiver.IsRunning);
        Assert.Equal(0, writes);
        Assert.Equal(checkpointBefore, await File.ReadAllBytesAsync(guardPath));
        Assert.True((await guard.LoadAsync()).Uncertain);
    });

    private static DotMessage Message(string id) => new(id, "assistant", "synthetic " + id, DateTimeOffset.UtcNow);
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Invoke(object target, string name, object value)
    {
        try { target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, [value]); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
    }

    private static void Sta(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try
            {
                var task = action(); var frame = new DispatcherFrame();
                _ = task.ContinueWith(_ => dispatcher.BeginInvoke((Action)(() => frame.Continue = false)), TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
                task.GetAwaiter().GetResult();
            }
            catch (Exception error) { failure = error; }
            finally { dispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Synthetic notification batch receiver check timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(full), TestRoot, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(full), "N", out _)
            || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing cleanup outside this own temporary batch-receiver test directory.");
        Directory.Delete(full, recursive: true);
    }
}
