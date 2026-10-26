namespace CodexQuotaTaskbar.Host.Bongo;

// The caller supplies the exact displayed document. No UI or agreement is inferred here.
internal sealed class CoreConsentGate(CoreTermsDocument document, ICoreConsentStore store)
{
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private volatile CoreConsentState state;
    private long generation;
    internal CoreConsentState State => state;
    internal bool CanUseCore => state == CoreConsentState.Accepted;
    internal event Action<CoreConsentState>? StateChanged;

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var requested = Interlocked.Increment(ref generation);
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var loaded = await store.LoadAsync(document, cancellationToken);
            if (requested == Volatile.Read(ref generation)) SetState(loaded);
        }
        finally { operationGate.Release(); }
    }

    internal async Task<bool> RecordDecisionAsync(CoreConsentDecision decision, CancellationToken cancellationToken)
    {
        if (decision == CoreConsentDecision.Closed) return false;
        if (decision is not (CoreConsentDecision.Accepted or CoreConsentDecision.Declined)) throw new ArgumentOutOfRangeException(nameof(decision));
        var requested = Interlocked.Increment(ref generation);
        // A queued/saving older acceptance must never re-enable Core after a newer decline.
        if (decision == CoreConsentDecision.Declined) SetState(CoreConsentState.Declined);
        // Once explicitly declined, finish that small local write even during shutdown.
        // Otherwise an older in-flight acceptance could remain as the restart receipt.
        var persistenceToken = decision == CoreConsentDecision.Declined ? CancellationToken.None : cancellationToken;
        await operationGate.WaitAsync(persistenceToken);
        try
        {
            if (requested != Volatile.Read(ref generation)) return false;
            await store.SaveDecisionAsync(document, decision, persistenceToken);
            if (requested != Volatile.Read(ref generation)) return false;
            SetState(decision == CoreConsentDecision.Accepted ? CoreConsentState.Accepted : CoreConsentState.Declined);
            return true;
        }
        finally { operationGate.Release(); }
    }

    internal void RequireAccepted()
    {
        if (!CanUseCore) throw new InvalidOperationException("尚未确认 Live2D Cubism Core 组件条款。");
    }

    private void SetState(CoreConsentState value)
    {
        if (state == value) return;
        state = value;
        StateChanged?.Invoke(value);
    }
}
