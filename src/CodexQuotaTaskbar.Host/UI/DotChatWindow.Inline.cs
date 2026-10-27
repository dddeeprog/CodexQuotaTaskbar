using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.Provider;

namespace CodexQuotaTaskbar.Host.UI;

public partial class DotChatWindow
{
    private readonly bool inlineMode;
    private readonly DotUnreadStore? injectedUnreadStore;
    private readonly DotUnreadInbox inlineInbox = new();
    private DotUnreadAcknowledgements? inlineReadAcknowledgements;
    private readonly DateTimeOffset inlineConnectedAt = DateTimeOffset.UtcNow;
    private readonly string inlineSessionId = Guid.NewGuid().ToString("N");
    private readonly object inlineOutputGate = new(), inlineSaveGate = new();
    private DotInlineState? inlineLatestOutput;
    private DotInlineState? inlineLastPublished;
    private DotUnreadSnapshot? inlineLatestSave;
    private DotUnreadStore? inlineStore;
    private Task inlineSaveTask = Task.CompletedTask;
    private bool inlineOutputWriting, inlineSaving, inlineStatePosted, inlinePersistenceFailed;
    private bool inlineStopping, inlineStopCompleted;
    private long inlineSequence, inlineNotificationSerial;
    // Retained state can still identify an earlier in-flight attempt. New
    // notification actions never write either draft or request correlation.
    private long inlineClientDraftRevision = 0;
    private string? inlineNotificationId, inlineLastRequestId = null;
    internal event Action<DotInlineState>? InlineStateChanged;

    private async Task StartInlineConnectionAsync()
    {
        inlineStore = injectedUnreadStore ?? DotUnreadStore.CreateDefault();
        try
        {
            if (await inlineStore.LoadAsync(lifetime.Token) is { } snapshot) inlineInbox.Restore(snapshot);
            inlinePersistenceFailed = inlineStore.LastLoadInvalid;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { inlinePersistenceFailed = true; }
        catch (OperationCanceledException) { return; }
        QueueInlineState();
        if (!closed && !inlineStopping) await ConnectAsync();
    }

    // A read can contain multiple new replies. The old single-latest tracker is
    // retained for the legacy UI only; inline observes every eligible stable ID.
    private void ObserveInlineMessages(IEnumerable<DotMessage> current)
    {
        if (!inlineMode || inlineStopping) return;
        var known = inlineInbox.CaptureKnownIds();
        var changed = false;
        foreach (var message in current)
        {
            if (message.Role != "assistant" || string.IsNullOrWhiteSpace(message.Id)
                || message.Id.Length > 256 || string.IsNullOrWhiteSpace(message.Text)
                || message.Time is not { } time || time < inlineConnectedAt && !known.Contains(message.Id)) continue;
            var before = inlineInbox.Find(message.Id);
            if (inlineInbox.ObserveNotification(message))
            {
                inlineNotificationId = message.Id; inlineNotificationSerial++; changed = true;
            }
            else
            {
                var after = inlineInbox.Find(message.Id);
                changed |= before != after;
            }
            known.Add(message.Id);
        }
        if (changed) QueueInlineSave();
    }

    private void HandleInlineAction(DotInlineAction action)
    {
        if (!inlineMode || closed || inlineStopping || !DotInlineWire.ValidAction(action)) return;
        // The island is now notification-only. A queued legacy composer event
        // must not replace a retained draft, submit, clear a review guard, open
        // a page, or acknowledge merely because a former reader was shown.
        // Existing in-flight send confirmation remains in the receive loop.
        if (action.Kind != "ack-notice" || action.ReadSessionId != inlineSessionId) return;
        inlineReadAcknowledgements ??= new(inlineInbox);
        if (inlineReadAcknowledgements.Acknowledge(action.ReadToken!, action.ReadSequence, action.MessageId))
            QueueInlineSave();
        QueueInlineState();
    }

    internal DotInlineState InlineSnapshot()
    {
        var sequence = ++inlineSequence;
        inlineReadAcknowledgements ??= new(inlineInbox);
        return new(inlineSessionId, sequence, inlineClientDraftRevision,
            Input.Text, inlineInbox.Messages.Select(item => new DotInlineMessage(item.Id, item.Text, item.Time, item.IsUnread)).ToArray(),
            inlineInbox.UnreadCount, inlineInbox.EarlierUnreadCount, inlineNotificationId, inlineNotificationSerial,
            ready, busy, needsReview, pendingText is not null, receiptStructureReady, webDraftPresent,
            (inlinePersistenceFailed ? "未读记录暂未保存 · " : "") + StatusText.Text,
            pageWindow.IsVisible, !closed, attemptState, inlineLastRequestId, ReplyAllowed: false,
            ReadToken: inlineReadAcknowledgements.Issue(sequence), ReadSequence: sequence);
    }

    private void QueueInlineState()
    {
        if (!inlineMode || inlineStatePosted || closed) return;
        inlineStatePosted = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
        {
            inlineStatePosted = false;
            if (closed) return;
            var state = InlineSnapshot();
            if (inlineLastPublished is { } prior && prior.Messages.SequenceEqual(state.Messages)
                && (prior with { Sequence = state.Sequence, ReadSequence = state.ReadSequence, Messages = state.Messages }) == state) return;
            inlineLastPublished = state;
            InlineStateChanged?.Invoke(state);
            if (!started || parent is null) return; // Constructed preview/test windows never write a real pipe.
            lock (inlineOutputGate)
            {
                inlineLatestOutput = state;
                if (inlineOutputWriting) return;
                inlineOutputWriting = true;
            }
            _ = Task.Run(WriteInlineStatesAsync);
        }));
    }

    private async Task WriteInlineStatesAsync()
    {
        try
        {
            while (true)
            {
                DotInlineState? state;
                lock (inlineOutputGate)
                {
                    state = inlineLatestOutput; inlineLatestOutput = null;
                    if (state is null) { inlineOutputWriting = false; return; }
                }
                var line = DotInlineWire.Serialize(state);
                if (line.Length > DotInlineWire.MaximumStateCharacters) throw new InvalidDataException("Dot state exceeds pipe bound.");
                await Console.Out.WriteLineAsync(line);
                await Console.Out.FlushAsync();
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            lock (inlineOutputGate) { inlineLatestOutput = null; inlineOutputWriting = false; }
            await Dispatcher.InvokeAsync(() => { if (!closed) Stop(); });
        }
    }

    private void QueueInlineSave()
    {
        if (inlineStore is null) return;
        lock (inlineSaveGate)
        {
            inlineLatestSave = inlineInbox.Capture();
            if (inlineSaving) return;
            inlineSaving = true;
            inlineSaveTask = SaveInlineInboxAsync();
        }
    }

    private async Task SaveInlineInboxAsync()
    {
        while (true)
        {
            DotUnreadSnapshot? snapshot;
            lock (inlineSaveGate)
            {
                snapshot = inlineLatestSave; inlineLatestSave = null;
                if (snapshot is null) { inlineSaving = false; return; }
            }
            try { await inlineStore!.SaveAsync(snapshot); inlinePersistenceFailed = false; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            { inlinePersistenceFailed = true; }
            QueueInlineState();
        }
    }

    private async Task FlushInlineAndStopAsync()
    {
        inlineStopping = true;
        try { await inlineSaveTask.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { inlinePersistenceFailed = true; }
        finally { inlineStopCompleted = true; Stop(); }
    }
}
