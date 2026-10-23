namespace CodexQuotaTaskbar.Host.Lifetime;

// UI-thread owned. The existing timer is armed only while a latest placement remains unsent.
internal sealed class DotAnchorSendPump(Func<DotAnchor, Task<bool>> send, Action<bool> setTimer)
{
    private DotAnchor? latest, sent;
    private bool active, sending;
    private long generation;

    internal void Update(DotAnchor value) { latest = value; Arm(); }
    internal void Start() { generation++; active = true; sent = null; Arm(); }
    internal void Stop() { generation++; active = false; setTimer(false); }

    private void Arm() => setTimer(active && !sending && latest is not null && latest != sent);

    internal async Task FlushAsync()
    {
        setTimer(false);
        if (!active || sending || latest is null || latest == sent) return;
        var value = latest;
        var currentGeneration = generation;
        sending = true;
        try { if (await send(value) && generation == currentGeneration) sent = value; }
        finally { sending = false; Arm(); }
    }
}

// The pipe reader can drain old placements while the UI is busy; only one latest callback is queued.
internal sealed class DotAnchorMailbox(Action<Action> schedule, Action<DotAnchor> apply)
{
    private readonly object gate = new();
    private DotAnchor? latest;
    private bool queued;

    internal void Post(DotAnchor value)
    {
        lock (gate)
        {
            latest = value;
            if (queued) return;
            queued = true;
        }
        schedule(Drain);
    }

    internal void Clear() { lock (gate) latest = null; }

    private void Drain()
    {
        DotAnchor? value;
        lock (gate) { value = latest; latest = null; queued = false; }
        if (value is not null) apply(value);
    }
}
