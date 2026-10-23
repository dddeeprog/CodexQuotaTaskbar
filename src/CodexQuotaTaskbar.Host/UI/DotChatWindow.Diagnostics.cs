using System.IO;
using System.Text.Json;
using CodexQuotaTaskbar.Host.Provider;

namespace CodexQuotaTaskbar.Host.UI;

public partial class DotChatWindow
{
    private DateTime diagnosticWrittenUtc;

    internal string DiagnosticJson() => JsonSerializer.Serialize(new
    {
        schema = 2, utc = DateTime.UtcNow, pid = Environment.ProcessId, attemptId, attemptState, attemptStartedUtc,
        hasPendingAttempt = pendingText is not null, closed, lifetimeCanceled = lifetime.IsCancellationRequested,
        nativeVisible = IsVisible, composerVisible = Composer.Visibility == System.Windows.Visibility.Visible,
        autoReplyActive = autoReplyDeadline is not null,
        autoReplyAwaitingPresentation,
        autoReplySecondsRemaining = autoReplyDeadline is { } due ? (double?)Math.Max(0, (due - opened.Elapsed).TotalSeconds) : null,
        placements = PlacementCount, placementResizes = PlacementResizeCount,
        placementStyleUpdates = PlacementStyleCount, maxPlacementMilliseconds,
        dismissCount, replyCount, dismissCountAtAttempt, replyCountAtAttempt, lastDismissUtc, lastReplyUtc,
        pendingSinceUtc = pendingText is null ? (DateTime?)null : pendingSince,
        pendingAgeSeconds = pendingText is null ? (double?)null : Math.Max(0, (DateTime.UtcNow - pendingSince).TotalSeconds),
        pendingLength = pendingText?.Length ?? 0, nativeDraftLength = Input.Text.Length,
        nativeDraft = pendingText is null ? "no_attempt" : Input.Text.Length == 0 ? "empty" : draftRevision == pendingDraftRevision && Input.Text == pendingText ? "same_as_attempt" : "edited",
        ready, busy, needsReview, webDraftPresent, receiptStructureReady, pageVisible = pageWindow.IsVisible,
        pendingBaselineComplete, baselineIdCount = beforeSend.Count, confirmedIdHash,
        confirmedUtc, confirmedTextLength, draftClearedOnReceipt, draftRevision,
        pendingDraftRevision = pendingText is null ? (long?)null : pendingDraftRevision,
        receiptNativeDraftRevision, receiptPendingDraftRevision,
        lastReadUtc, lastErrorUtc, lastReadError, consecutiveReadFailures,
        sendBlockedBy = closed ? "closed" : busy ? "busy" : pageWindow.IsVisible ? "page_visible" : pendingText is not null ? "pending_receipt" :
            needsReview ? "review_required" : webDraftPresent ? "web_draft" : !ready ? "not_ready" : !receiptStructureReady ? "receipt_structure" : string.IsNullOrWhiteSpace(Input.Text) ? "empty_draft" : "none",
        bridge = bridgeDiagnostic,
        candidates = messages.TakeLast(12).Select(m => new { idHash = DotBridgeDiagnostic.HashId(m.Id),
            role = m.Role is "user" or "assistant" ? m.Role : "unknown", idSource = DotBridgeDiagnostic.SafeSource(m.IdSource),
            roleSource = DotBridgeDiagnostic.SafeSource(m.RoleSource), textSource = DotBridgeDiagnostic.SafeSource(m.TextSource),
            idMissing = string.IsNullOrWhiteSpace(m.Id), duplicateId = messages.Count(other => other.Id == m.Id) > 1,
            inBaseline = beforeSend.Contains(m.Id), m.Confirmable,
            textLength = m.Text.Length, matchesPending = pendingText is not null && Normalize(m.Text) == Normalize(pendingText), time = m.Time })
    });

    private void WriteDiagnostics(bool force = false)
    {
        // Tests that construct a window never create files or initialize the account's browser.
        if (!started || !force && (busy || DateTime.UtcNow - diagnosticWrittenUtc < TimeSpan.FromSeconds(5))) return;
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaTaskbar", "Diagnostics");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"dot-chat-{Environment.ProcessId}.json");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, DiagnosticJson()); File.Move(temporary, path, overwrite: true);
            diagnosticWrittenUtc = DateTime.UtcNow;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
