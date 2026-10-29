using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace CodexQuotaTaskbar.Host.UI;

internal readonly record struct CompanionGazeSample(double TargetX, double TargetY, double OffsetX, double OffsetY, bool Allowed);

internal sealed record CompanionDiagnosticEntry(long Sequence, DateTimeOffset Utc, double MonotonicMs, int Pid,
    int ControlId, string Kind, string RequestedId, string EffectiveId, long Cycle, int Position, long Draw,
    DateTimeOffset SelectedAtUtc, string Reason, bool Preview, bool Visible, bool Reduced, CompanionGazeSample Gaze);

// Narrow, bounded transition records: never serialize labels, task snapshots, messages or cursor coordinates.
internal sealed class CompanionDiagnostics
{
    private static CompanionDiagnostics local = new(
        string.Equals(Path.GetFileName(Environment.ProcessPath), "CodexQuotaTaskbar.exe", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexQuotaTaskbar", "Diagnostics", $"companion-{Environment.ProcessId}.json") : null);
    internal static CompanionDiagnostics Local => Volatile.Read(ref local);
    internal static void ConfigureDemo(string? path)
    {
        var previous = Interlocked.Exchange(ref local, new CompanionDiagnostics(path));
        _ = previous.FlushAsync().GetAwaiter().GetResult();
    }
    private readonly string? path;
    private readonly int capacity;
    private readonly object gate = new();
    private readonly Queue<CompanionDiagnosticEntry> entries = new();
    private readonly Dictionary<(int Control, string Kind), Identity> last = new();
    private long sequence;
    private long persistedSequence;
    private long snapshotWrites;
    private long snapshotWriteAttempts;
    private Task? writer;
    private TaskCompletionSource? wake;

    internal CompanionDiagnostics(string? path = null, int capacity = 192)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 512);
        this.path = path;
        this.capacity = capacity;
    }

    internal IReadOnlyList<CompanionDiagnosticEntry> Entries { get { lock (gate) return entries.ToArray(); } }

    internal bool Observe(int controlId, string kind, CompanionFrame selection, string requestedId, string effectiveId,
        bool visible, bool reduced, CompanionGazeSample gaze = default)
    {
        var identity = new Identity(requestedId, effectiveId, selection.Cycle, selection.Position, selection.Draw,
            selection.Preview, visible, reduced);
        lock (gate)
        {
            if (last.TryGetValue((controlId, kind), out var previous) && previous == identity) return false;
            if (controlId < 1 || kind is not ("selected" or "presented")
                || !CompanionEmotionDirector.EmotionIds.Contains(requestedId)
                || !CompanionEmotionDirector.EmotionIds.Contains(effectiveId)
                || selection.Reason is not ("initial" or "automatic" or "tap" or "preview")
                || !double.IsFinite(gaze.TargetX) || !double.IsFinite(gaze.TargetY)
                || !double.IsFinite(gaze.OffsetX) || !double.IsFinite(gaze.OffsetY)) return false;
            last[(controlId, kind)] = identity;
            entries.Enqueue(new(++sequence, DateTimeOffset.UtcNow, Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency,
                Environment.ProcessId, controlId, kind, requestedId, effectiveId, selection.Cycle, selection.Position,
                selection.Draw, selection.SelectedAtUtc, selection.Reason, selection.Preview, visible, reduced, gaze));
            while (entries.Count > capacity) entries.Dequeue();
            StartWriter();
            return true;
        }
    }

    // A disposed control can never produce another event; keep live controls' independent dedup intact.
    internal void ReleaseControl(int controlId)
    {
        lock (gate) { last.Remove((controlId, "selected")); last.Remove((controlId, "presented")); }
    }

    private void StartWriter()
    {
        if (path is null || writer is not null) return;
        wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
        writer = Task.Run(WritePendingAsync);
    }

    // One bounded snapshot and one worker per producer; neither serialization nor disk IO holds gate.
    private async Task WritePendingAsync()
    {
        while (true)
        {
            Task expedite;
            lock (gate) expedite = wake!.Task;
            await Task.WhenAny(Task.Delay(200), expedite).ConfigureAwait(false);
            CompanionDiagnosticEntry[] snapshot;
            long captured;
            lock (gate) { snapshot = entries.ToArray(); captured = sequence; }
            var success = Diagnostics.DiagnosticLogStore.WriteSnapshot(path!,
                JsonSerializer.Serialize(new { Schema = 1, Pid = Environment.ProcessId, Entries = snapshot }));
            lock (gate)
            {
                snapshotWriteAttempts++;
                if (success) { persistedSequence = captured; snapshotWrites++; }
                if (sequence == captured) { writer = null; wake = null; return; }
                wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    // Flush is explicit export/exit work, with no Dispatcher dependency and a bounded wait.
    // A refused write is observable and can be retried without losing the in-memory final event.
    internal async Task<bool> FlushAsync()
    {
        Task pending;
        long target;
        lock (gate)
        {
            target = sequence;
            if (path is null || persistedSequence >= target) return true;
            StartWriter();
            pending = writer!;
            wake!.TrySetResult();
        }
        try { await pending.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { return false; }
        lock (gate) return persistedSequence >= target;
    }

    private readonly record struct Identity(string Requested, string Effective, long Cycle, int Position, long Draw,
        bool Preview, bool Visible, bool Reduced);
}
