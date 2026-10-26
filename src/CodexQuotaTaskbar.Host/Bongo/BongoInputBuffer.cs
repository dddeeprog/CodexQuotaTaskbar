using System.Text.Json.Serialization;
using CodexQuotaTaskbar.Host.Platform;

namespace CodexQuotaTaskbar.Host.Bongo;

internal readonly record struct BongoKeyEdge(
    [property: JsonPropertyName("hid")] ushort Hid,
    [property: JsonPropertyName("down")] bool Down);

internal sealed record BongoInputBatch(bool Reset, IReadOnlyList<BongoKeyEdge> Keys, bool MouseLeft, bool MouseRight);

// A bounded, transient handoff to the renderer, never an input history or text.
// Repeated downs are ignored without changing key priority. Mouse buttons are
// current-state snapshots: a complete tap between drains need not be visible,
// but its release cannot leave a button held in the renderer.
internal sealed class BongoInputBuffer
{
    internal const int MaximumPendingEdges = 256;
    internal const int MaximumEdgesPerBatch = 64;
    private readonly object gate = new();
    private readonly Queue<BongoKeyEdge> edges = new(MaximumPendingEdges);
    private readonly HashSet<ushort> held = new();
    private bool resetPending, mouseLeft, mouseRight;

    internal int PendingEdgeCount { get { lock (gate) return edges.Count; } }
    internal int HeldKeyCount { get { lock (gate) return held.Count; } }

    internal void Accept(CompanionInputEvent input)
    {
        lock (gate)
        {
            if (input.Kind == CompanionInputKind.Reset) { ResetCore(); return; }
            if (input.Kind == CompanionInputKind.MouseButton)
            {
                if (input.MouseButton == CompanionMouseButton.Left) mouseLeft = input.IsDown;
                else if (input.MouseButton == CompanionMouseButton.Right) mouseRight = input.IsDown;
                return;
            }
            if (input.Kind != CompanionInputKind.Key || !BongoPhysicalKey.TryGetHid(input.PhysicalKey, out var hid)) return;
            if (input.IsDown == held.Contains(hid)) return;
            if (edges.Count == MaximumPendingEdges)
            {
                // Discard uncertain in-flight state, including this triggering
                // edge. The next batch resets the renderer before newer edges.
                // Dropping a release is safe here because Reset releases all.
                ResetCore();
                return;
            }
            if (input.IsDown) held.Add(hid);
            else held.Remove(hid);
            edges.Enqueue(new(hid, input.IsDown));
        }
    }

    internal void Reset() { lock (gate) ResetCore(); }

    private void ResetCore()
    {
        edges.Clear();
        held.Clear();
        mouseLeft = mouseRight = false;
        resetPending = true;
    }

    // The consumer applies Reset before Keys, then both mouse snapshots. No
    // mutable backing state is exposed, and one drain can never exceed 64 edges.
    internal BongoInputBatch Drain()
    {
        lock (gate)
        {
            var pending = new BongoKeyEdge[Math.Min(edges.Count, MaximumEdgesPerBatch)];
            for (var index = 0; index < pending.Length; index++) pending[index] = edges.Dequeue();
            var batch = new BongoInputBatch(resetPending, Array.AsReadOnly(pending), mouseLeft, mouseRight);
            resetPending = false;
            return batch;
        }
    }
}
