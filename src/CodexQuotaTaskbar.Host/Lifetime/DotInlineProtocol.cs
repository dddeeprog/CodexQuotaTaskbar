namespace CodexQuotaTaskbar.Host.Lifetime;

// Only ephemeral in-process-pipe data. Never serialize these records to diagnostics.
internal sealed record DotInlineAction(string Kind, string? Text = null, long DraftRevision = 0,
    string? MessageId = null, string? RequestId = null, int Count = 0, long? OriginalDraftRevision = null,
    string? ReadToken = null, string? ReadSessionId = null, long ReadSequence = 0);

internal sealed record DotInlineMessage(string Id, string Text, DateTimeOffset? Time, bool Unread);

internal sealed record DotInlineState(string SessionId, long Sequence, long ClientDraftRevision,
    string DraftText, DotInlineMessage[] Messages, int UnreadCount, int EarlierUnreadCount,
    string? NotificationId, long NotificationSerial, bool Ready, bool Busy, bool NeedsReview,
    bool HasPendingAttempt, bool ReceiptStructureReady, bool WebDraftPresent, string Status,
    bool PageVisible = false, bool Online = true, string AttemptState = "none", string? LastRequestId = null,
    bool RecoveryRequired = false, bool RecoveryCanReview = false, bool ReplyAllowed = true,
    string? ReadToken = null, long ReadSequence = 0)
{
    internal bool CanSubmit => ReplyAllowed && Online && Ready && !Busy && !NeedsReview && !HasPendingAttempt && !RecoveryRequired
        && ReceiptStructureReady && !WebDraftPresent && !PageVisible;
}
