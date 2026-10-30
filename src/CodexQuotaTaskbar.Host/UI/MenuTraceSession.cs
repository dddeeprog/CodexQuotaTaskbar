using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexQuotaTaskbar.Host.Diagnostics;

namespace CodexQuotaTaskbar.Host.UI;

internal enum MenuTraceKind
{
    ShowRequest, ShowAccepted, ShowRejected, ActualShowBefore, ActualShowAfter, Activated, Deactivated,
    ActivateResult, FocusResult, Prepare, Play, PendingConsume, RenderStart, Complete, Cancel, Normalize,
    FrameSample, NativeShow, NativeActivation, NativePosition, PositionRequest, PositionActual,
    SizeChanged, Loaded, MaterialEnable, BackgroundChange, EpisodeStop, SessionStop,
}
internal enum MenuTraceStop { None, Hidden, Deadline, Disposed, Exception, PlacementFailed }
internal enum MenuTraceStatus { Disabled, Armed, Recording, Stopped }
internal enum MenuTraceSource { None, Tray, Island }
internal enum MenuTraceRole { None, Menu, Details, Capsule, Other }
internal readonly record struct MenuTraceInput(long Sequence = 0, long Tick = 0, int Timestamp = 0, MenuTraceSource Source = default);
internal readonly record struct MenuTraceSurface(double Opacity, double ScaleX, double ScaleY, double OffsetX, double OffsetY);
internal readonly record struct MenuTraceBounds(int Left, int Top, int Right, int Bottom, uint Dpi);
internal readonly record struct MenuTraceState(bool Visible = false, bool Loaded = false, bool Preparing = false,
    bool Closing = false, MenuTraceRole Foreground = default, MenuTraceSurface Shell = default,
    MenuTraceSurface Content = default, MenuTraceBounds Bounds = default, bool Result = false, int NativeValue = 0,
    long MotionGeneration = 0, long PendingGeneration = 0);
internal sealed record MenuTraceEntry(long Tick, long Generation, MenuTraceKind Kind, MenuTraceSource Source, MenuTraceState State);
internal sealed record MenuTraceEpisode(long Generation, long StartTick, long EndTick, MenuTraceStop Stop,
    MenuTraceInput Input, int Dropped, int Samples, bool Shown, MenuTraceEntry[] Entries);
internal sealed record MenuTraceSnapshot(int Schema, long TickFrequency, bool Enabled, MenuTraceStatus Status,
    int Accepted, int Remaining, int Records, int Samples, MenuTraceKind Summary, MenuTraceEpisode[] Episodes);

// Process-wide, opt-in and finite. No UI/native operations live here. The timer closes
// admission independently of the Dispatcher; UI unsubscription can happen later.
internal sealed class MenuTraceSession : IDisposable
{
    private static readonly Lazy<MenuTraceSession> LocalSession = new(CreateLocal);
    internal static MenuTraceSession Local => LocalSession.Value;
    internal const int MaximumFileBytes = 512 * 1024;
    private const int EpisodeCapacity = 128;
    private const int SessionCapacity = 512;
    private readonly object gate = new();
    private readonly bool enabled;
    private readonly Func<long> clock;
    private readonly long frequency;
    private readonly Action<byte[]>? writer;
    private readonly Func<TimeSpan, Action, IDisposable> schedule;
    private readonly List<MenuTraceEpisode> episodes = new(3);
    private readonly List<MenuTraceEntry> entries = new(EpisodeCapacity);
    private IDisposable? deadline;
    private Task writes = Task.CompletedTask;
    private long activeGeneration;
    private long startTick;
    private int accepted, records, samples, dropped, episodeSamples;
    private MenuTraceInput input;
    private bool active, disposed, shown;
    private static readonly JsonSerializerOptions JsonOptions = new() { Converters = { new JsonStringEnumConverter() } };

    internal MenuTraceSession(bool enabled, Func<long>? clock = null, long frequency = 0, Action<byte[]>? writer = null,
        Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        this.enabled = enabled;
        this.clock = clock ?? Stopwatch.GetTimestamp;
        this.frequency = frequency > 0 ? frequency : Stopwatch.Frequency;
        this.writer = writer;
        this.schedule = schedule ?? Schedule;
        if (enabled) QueueSnapshot();
    }

    internal bool Enabled => enabled;
    internal event Action<long>? EpisodeStopped;

    private static MenuTraceSession CreateLocal()
    {
        var enabled = string.Equals(Path.GetFileName(Environment.ProcessPath), "CodexQuotaTaskbar.exe", StringComparison.OrdinalIgnoreCase)
            && Environment.GetEnvironmentVariable("CQT_MENU_TRACE_ONCE") == "1";
        if (!enabled) return new(false);
        var path = Path.Combine(DiagnosticLogStore.DefaultDirectory, $"menu-trace-{Environment.ProcessId}.json");
        return new(true, writer: bytes => WriteFile(path, bytes));
    }

    private static void WriteFile(string path, byte[] bytes)
    {
        if (bytes.Length > MaximumFileBytes || !DiagnosticLogStore.SafePath(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!DiagnosticLogStore.SafePath(path)) return;
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        file.Write(bytes);
    }

    private static IDisposable Schedule(TimeSpan due, Action action) =>
        new System.Threading.Timer(_ => action(), null, due, Timeout.InfiniteTimeSpan);

    internal bool Begin(long generation, MenuTraceInput correlation = default, Func<MenuTraceState>? capture = null)
    {
        lock (gate)
        {
            if (!enabled || disposed || active || accepted >= 3 || generation <= activeGeneration) return false;
            active = true; activeGeneration = generation; startTick = clock(); input = correlation;
            accepted++; dropped = episodeSamples = 0; shown = false; entries.Clear();
            try
            {
                var state = capture?.Invoke() ?? default;
                var now = clock();
                if (Expired(now)) { Stop(generation, MenuTraceStop.Deadline); return false; }
                Add(MenuTraceKind.ShowRequest, state, startTick);
                Add(MenuTraceKind.ShowAccepted, state, startTick);
                var remaining = TimeSpan.FromSeconds(.6 - (now - startTick) / (double)frequency);
                deadline = schedule(remaining, () => Stop(generation, MenuTraceStop.Deadline));
            }
            catch { Stop(generation, MenuTraceStop.Exception); }
            return active;
        }
    }

    internal bool IsActive(long generation)
    {
        lock (gate)
        {
            if (!active || generation != activeGeneration) return false;
            if (Expired(clock())) { Stop(generation, MenuTraceStop.Deadline); return false; }
            return true;
        }
    }

    // capture is called only after deadline/generation checks, including once capacity
    // is exhausted: full buffers no longer ask WPF or native code for another sample.
    internal bool Observe(long generation, MenuTraceKind kind, Func<MenuTraceState> capture)
    {
        lock (gate)
        {
            if (!active || generation != activeGeneration) return false;
            var tick = clock();
            if (Expired(tick)) { Stop(generation, MenuTraceStop.Deadline); return false; }
            if (kind == MenuTraceKind.ActualShowAfter) shown = true;
            if (entries.Count >= EpisodeCapacity || records >= SessionCapacity) { dropped++; return false; }
            try { Add(kind, capture(), tick); return true; }
            catch { Stop(generation, MenuTraceStop.Exception); return false; }
        }
    }

    private bool Expired(long tick) => tick - startTick >= frequency * .6;

    private void Add(MenuTraceKind kind, MenuTraceState state, long tick)
    {
        if (entries.Count >= EpisodeCapacity || records >= SessionCapacity) { dropped++; return; }
        entries.Add(new(tick, activeGeneration, kind, input.Source, state)); records++;
        if (kind == MenuTraceKind.FrameSample) { samples++; episodeSamples++; }
    }

    internal void Stop(long generation, MenuTraceStop reason)
    {
        lock (gate)
        {
            if (!active || generation != activeGeneration) return;
            active = false; // Revokes capture before timer disposal or UI callback.
            try { deadline?.Dispose(); } catch { }
            finally { deadline = null; }
            var end = clock();
            var lastState = entries.LastOrDefault()?.State ?? default;
            Add(MenuTraceKind.EpisodeStop, lastState, end);
            if (accepted == 3 || disposed) Add(MenuTraceKind.SessionStop, lastState, end);
            episodes.Add(new(generation, startTick, end, reason, input, dropped, episodeSamples, shown, entries.ToArray()));
            entries.Clear();
            QueueSnapshot();
            // Subscribers must marshal only cleanup, never inspect WPF on this thread.
            try { EpisodeStopped?.Invoke(generation); } catch { }
        }
    }

    private void QueueSnapshot()
    {
        if (writer is null) return;
        var snapshot = new MenuTraceSnapshot(1, frequency, enabled,
            disposed || accepted == 3 && !active ? MenuTraceStatus.Stopped : active ? MenuTraceStatus.Recording : MenuTraceStatus.Armed,
            accepted, 3 - accepted, records, samples,
            disposed || accepted == 3 ? MenuTraceKind.SessionStop : accepted == 0 ? MenuTraceKind.ShowRequest : MenuTraceKind.EpisodeStop,
            episodes.ToArray());
        var previous = writes;
        writes = Task.Run(async () =>
        {
            try
            {
                await previous.ConfigureAwait(false);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
                // Fixed, typed records normally fit well below this bound. Never write
                // an oversized document, even if a future schema grows unexpectedly.
                if (bytes.Length <= MaximumFileBytes) writer(bytes);
            }
            catch { /* Diagnostics must never fail the UI or another snapshot. */ }
        });
    }

    internal Task FlushAsync() { lock (gate) return writes; }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (active) Stop(activeGeneration, MenuTraceStop.Disposed);
        }
    }
}

