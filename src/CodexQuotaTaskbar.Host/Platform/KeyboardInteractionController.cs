namespace CodexQuotaTaskbar.Host.Platform;

// Carries transient physical state for local drawing, never text or an input history.
internal sealed class KeyboardInteractionController(Func<IKeyboardActivitySource> sourceFactory, Action<CompanionInputEvent> input) : IDisposable
{
    private IKeyboardActivitySource? source;
    private bool armed;
    private bool disposed;
    internal bool IsRunning => source?.IsRunning == true;
    internal bool Unavailable { get; private set; }

    internal void Update(bool enabled, bool canAnimate)
    {
        if (disposed) return;
        var desired = enabled && canAnimate;
        if (armed == desired) return;
        armed = desired;
        Unavailable = false;
        if (!desired) { ReleaseSource(); return; }
        try
        {
            source = sourceFactory();
            source.Activity += OnActivity;
            if (source.TryStart()) return;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException) { }
        Unavailable = true;
        ReleaseSource();
        // A failed registration is not retried for every render/reconcile tick. Toggle or visibility resets it.
    }

    private void OnActivity(CompanionInputEvent value)
    {
        // A read failure stops the source before reporting Reset. It must still release a held paw.
        if (!disposed && armed && (IsRunning || value.Kind == CompanionInputKind.Reset)) input(value);
    }

    private void ReleaseSource()
    {
        var previous = source;
        source = null;
        if (previous is null) return;
        previous.Activity -= OnActivity;
        try { previous.Stop(); }
        finally
        {
            try { previous.Dispose(); }
            finally { input(new(CompanionInputKind.Reset)); }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        armed = false;
        ReleaseSource();
    }
}
