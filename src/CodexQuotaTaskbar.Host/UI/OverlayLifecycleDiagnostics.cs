using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using CodexQuotaTaskbar.Host.Platform;

namespace CodexQuotaTaskbar.Host.UI;

internal enum OverlayLifecycleEvent
{
    OwnerCreated, TopologyRemoved, VisibilityPolicy, Visibility, RendererStart, RendererStop,
    RendererStarted, RendererFirstFrame, RendererExited, RendererExitUnconfirmed, RendererFailed,
    LeftDown, RightDown, PrimaryInvoke, ContextInvoke, DetailsToggle, DetailsCreated, DetailsClosed, DetailsDeactivated,
    MenuFromIsland, MenuFromTray, MenuExplicitClose, MenuToggle, MenuHide, MenuDeactivated, MenuVisibility,
}
internal enum OverlayVisibilityReason { None, Available, Fullscreen, TaskbarHidden, Locked }
internal sealed record OverlayLifecycleEntry(long Sequence, DateTimeOffset Utc, long Tick, int Pid,
    OverlayLifecycleEvent Kind, int Owner, long Hwnd, bool Visible, OverlayVisibilityReason Reason,
    int RelatedPid, int InputTimestamp);

// Separate from expression diagnostics: fixed metadata only, bounded to 128
// transitions. No titles, messages, account IDs, paths or pointer coordinates.
internal sealed class OverlayLifecycleDiagnostics
{
    internal static OverlayLifecycleDiagnostics Local { get; } = new(
        string.Equals(Path.GetFileName(Environment.ProcessPath), "CodexQuotaTaskbar.exe", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexQuotaTaskbar", "Diagnostics", $"overlay-{Environment.ProcessId}.json") : null);
    private readonly string? path;
    private readonly object gate = new();
    private readonly Queue<OverlayLifecycleEntry> entries = new();
    private long sequence;
    private bool writeQueued;
    private static readonly JsonSerializerOptions JsonOptions = new()
    { Converters = { new JsonStringEnumConverter() } };

    internal OverlayLifecycleDiagnostics(string? path = null) => this.path = path;
    internal IReadOnlyList<OverlayLifecycleEntry> Entries { get { lock (gate) return entries.ToArray(); } }

    internal void Window(Window window, OverlayLifecycleEvent kind, OverlayVisibilityReason reason = default,
        int inputTimestamp = 0, bool deduplicate = false)
    {
        var relatedPid = 0;
        if (kind == OverlayLifecycleEvent.VisibilityPolicy)
        {
            _ = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out var pid);
            relatedPid = unchecked((int)pid);
        }
        Record(kind, RuntimeHelpers.GetHashCode(window), new WindowInteropHelper(window).Handle.ToInt64(),
            window.IsVisible, reason, relatedPid, inputTimestamp, deduplicate);
    }

    internal void Record(OverlayLifecycleEvent kind, int owner = 0, long hwnd = 0, bool visible = false,
        OverlayVisibilityReason reason = default, int relatedPid = 0, int inputTimestamp = 0, bool deduplicate = false)
    {
        lock (gate)
        {
            if (deduplicate && entries.LastOrDefault(e => e.Owner == owner && e.Kind == kind) is { } last
                && last.Hwnd == hwnd && last.Reason == reason && last.RelatedPid == relatedPid) return;
            entries.Enqueue(new(++sequence, DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), Environment.ProcessId,
                kind, owner, hwnd, visible, reason, relatedPid, inputTimestamp));
            while (entries.Count > 128) entries.Dequeue();
            if (path is not null && !writeQueued)
            {
                writeQueued = true;
                _ = FlushAsync();
            }
        }
    }

    // Coalesce bursts off the UI thread; diagnostics must not stall a click or
    // create one writer/task per event. Nothing runs while the log is unchanged.
    private async Task FlushAsync()
    {
        while (true)
        {
            await Task.Delay(200).ConfigureAwait(false);
            OverlayLifecycleEntry[] snapshot;
            long written;
            lock (gate) { snapshot = entries.ToArray(); written = sequence; }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path!)!);
                await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(
                    new { Schema = 1, TickFrequency = Stopwatch.Frequency, Entries = snapshot }, JsonOptions))
                    .ConfigureAwait(false);
                File.Move(path + ".tmp", path!, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            lock (gate)
            {
                if (written != sequence) continue;
                writeQueued = false;
                return;
            }
        }
    }
}
