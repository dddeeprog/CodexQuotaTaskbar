using CodexQuotaTaskbar.Host.Platform;
using System.Windows;
using Point = System.Windows.Point;

namespace CodexQuotaTaskbar.Host.UI;

// Only the currently held mapped keys are kept, in press order, never a key history.
// The standard-style keyboard paw follows the last still-held key; auto-repeat is inert.
internal sealed class CompanionTypingMotion
{
    private readonly List<ushort> held = new(CompanionTypingKeyboard.Keys.Count);
    private bool enabled, mouseLeft, mouseRight;
    private double pointerX, pointerY, targetX, targetY, previousSample;
    private bool sampled;

    internal int HeldCount => held.Count;

    internal void SetEnabled(bool value) { enabled = value; if (!value) Cancel(); }
    internal void Cancel() { held.Clear(); mouseLeft = mouseRight = sampled = false; pointerX = pointerY = targetX = targetY = previousSample = 0; }

    internal bool Input(CompanionInputEvent input)
    {
        if (input.Kind == CompanionInputKind.Reset) { Cancel(); return true; }
        if (!enabled) return false;
        if (input.Kind == CompanionInputKind.Key)
        {
            if (!CompanionTypingKeyboard.TryGet(input.PhysicalKey, out _)) return false;
            var index = held.IndexOf(input.PhysicalKey);
            if (input.IsDown)
            {
                if (index >= 0) return false;
                held.Add(input.PhysicalKey);
            }
            else
            {
                if (index < 0) return false;
                held.RemoveAt(index);
            }
            return true;
        }
        if (input.Kind != CompanionInputKind.MouseButton) return false;
        if (input.MouseButton == CompanionMouseButton.Left) { var changed = mouseLeft != input.IsDown; mouseLeft = input.IsDown; return changed; }
        if (input.MouseButton == CompanionMouseButton.Right) { var changed = mouseRight != input.IsDown; mouseRight = input.IsDown; return changed; }
        return false;
    }

    internal void Pointer(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return;
        var length = Math.Sqrt(x * x + y * y);
        var scale = length > 1 ? 1 / length : 1;
        targetX = x * scale;
        targetY = y * scale;
    }

    internal CompanionTypingFrame Sample(double now)
    {
        if (!enabled || !double.IsFinite(now) || now < 0) return CompanionTypingFrame.Rest;
        if (!sampled) { pointerX = targetX; pointerY = targetY; sampled = true; }
        else
        {
            // The existing 5Hz gaze target is smoothed by the existing render clock.
            // Never add a separate pointer poller or turn a long hidden gap into catch-up work.
            var blend = 1 - Math.Exp(-Math.Clamp(now - previousSample, 0, 1000) / 75);
            pointerX += (targetX - pointerX) * blend;
            pointerY += (targetY - pointerY) * blend;
        }
        previousSample = Math.Max(previousSample, now);
        var key = held.Count == 0 ? (ushort)0 : held[^1];
        return new(true, key, pointerX, pointerY, mouseLeft, mouseRight);
    }
}

internal readonly record struct CompanionTypingFrame(bool DecorationsVisible, ushort Key,
    double MouseX, double MouseY, bool MouseLeftDown, bool MouseRightDown)
{
    internal bool Active => Key != 0 || MouseLeftDown || MouseRightDown;
    internal double BodyY => Key == 0 ? 0 : .8;
    internal double ScaleX => 1;
    internal double ScaleY => 1;
    internal static CompanionTypingFrame Rest => default;
    internal static CompanionTypingFrame Idle => new(true, 0, 0, 0, false, false);
}

internal readonly record struct CompanionTypingKeyCap(ushort Scan, double X, double Y, double Width);

// Complete five-row 60% footprint, including punctuation and both modifiers. No F/numpad block.
// Scan positions are physical US-layout positions, not translated characters or the active layout.
internal static class CompanionTypingKeyboard
{
    internal static IReadOnlyList<CompanionTypingKeyCap> Keys { get; } = Create();
    internal static bool TryGet(ushort scan, out CompanionTypingKeyCap cap)
    {
        foreach (var candidate in Keys) if (candidate.Scan == scan) { cap = candidate; return true; }
        cap = default; return false;
    }
    internal static Point Project(double x, double y) => new(63 + 14 * x - 7.4 * y, 207 + 2.1 * x + 8.1 * y);
    private static IReadOnlyList<CompanionTypingKeyCap> Create()
    {
        var keys = new List<CompanionTypingKeyCap>(61);
        void Row(int row, params (ushort Scan, double Width)[] values)
        {
            var x = 0d;
            foreach (var (scan, width) in values) { keys.Add(new(scan, x, row, width)); x += width; }
        }
        Row(0, (0x29,1),(2,1),(3,1),(4,1),(5,1),(6,1),(7,1),(8,1),(9,1),(10,1),(11,1),(12,1),(13,1),(14,2));
        Row(1, (15,1.5),(16,1),(17,1),(18,1),(19,1),(20,1),(21,1),(22,1),(23,1),(24,1),(25,1),(26,1),(27,1),(43,1.5));
        Row(2, (58,1.75),(30,1),(31,1),(32,1),(33,1),(34,1),(35,1),(36,1),(37,1),(38,1),(39,1),(40,1),(28,2.25));
        Row(3, (42,2.25),(44,1),(45,1),(46,1),(47,1),(48,1),(49,1),(50,1),(51,1),(52,1),(53,1),(54,2.75));
        Row(4, (29,1.25),(0x15b,1.25),(56,1.25),(57,6.25),(0x138,1.25),(0x15c,1.25),(0x15d,1.25),(0x11d,1.25));
        return keys.AsReadOnly();
    }
}
