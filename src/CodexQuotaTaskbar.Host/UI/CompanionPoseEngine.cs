using System.Text.Json;

namespace CodexQuotaTaskbar.Host.UI;

// Native adaptation of tycoding/grok-ball (MIT). See Assets/Companion/LICENSE.txt.
// The immutable catalog is shared by all monitors; no browser or script runtime is needed.
internal sealed class CompanionCatalog
{
    internal static readonly Lazy<CompanionCatalog> Shared = new(Load);
    public double[][] Head { get; set; } = [];
    public double[][][][] Expressions { get; set; } = [];
    public CompanionDefinition[] Emotions { get; set; } = [];
    internal CompanionDefinition Get(string id) => Emotions.First(d => d.Id == id);

    private static CompanionCatalog Load()
    {
        using var stream = typeof(CompanionCatalog).Assembly.GetManifestResourceStream(
            "CodexQuotaTaskbar.Host.Assets.Companion.poses.json") ?? throw new InvalidOperationException("Missing companion poses.");
        var result = JsonSerializer.Deserialize<CompanionCatalog>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Invalid companion poses.");
        if (!result.Emotions.Select(d => d.Id).SequenceEqual(CompanionEmotionDirector.EmotionIds)
            || result.Expressions.Length != 25 || result.Head.Length < 3
            || result.Emotions.Any(d => d.Base.Length != 27 || d.Pool.Length == 0 || d.Pool.Any(i => i < 0 || i >= result.Expressions.Length)))
            throw new InvalidOperationException("Incomplete companion catalog.");
        return result;
    }
}

internal sealed class CompanionDefinition
{
    public string Id { get; set; } = "02";
    public string Name { get; set; } = "";
    public bool Gaze { get; set; }
    public bool Antics { get; set; }
    public double Transition { get; set; }
    public double Openness { get; set; }
    public int[] Pool { get; set; } = [];
    public double[] PoolMs { get; set; } = [];
    public double[]? BlinkMs { get; set; }
    public double PoolSpeed { get; set; }
    public double[] Base { get; set; } = [];
    public CompanionAnimation[] Anims { get; set; } = [];
    public CompanionSequence? Sequence { get; set; }
}
internal sealed class CompanionSequence
{
    public CompanionKeyframe[] Frames { get; set; } = [];
    public JsonElement Settle { get; set; }
}
internal sealed class CompanionKeyframe
{
    public double At { get; set; }
    public double[] Pose { get; set; } = [];
}
internal sealed class CompanionAnimation
{
    public string Target { get; set; } = "";
    public string Prop { get; set; } = "";
    public string Type { get; set; } = "";
    public double Amp { get; set; }
    public double Period { get; set; }
    public double Phase { get; set; }
    public double PhaseMs { get; set; }
    public double Speed { get; set; }
    public double Decay { get; set; }
    public double Interval { get; set; }
    public double Dur { get; set; }
    public double Depth { get; set; } = 1;

    internal double Sample(double t, double seed)
    {
        var period = Period > 0 ? Period : Type == "pulse" ? 1000 : Type == "scan" ? 800 : Type == "glance" ? 3600 : 2000;
        var phase = Math.Tau * t / period + Phase;
        var p = ((t + PhaseMs) % period) / period;
        var s = t / 1000 * (Speed > 0 ? Speed : 8);
        var blink = (t + PhaseMs + seed * 97) % (Interval > 0 ? Interval : 3800);
        var duration = Dur > 0 ? Dur : 200;
        return Type switch
        {
            "sine" => Amp * Math.Sin(phase),
            "pulse" => Amp * .5 * (1 - Math.Cos(phase)),
            "scan" => Amp * (p < .5 ? p * 4 - 1 : 3 - p * 4),
            "glance" => Amp * Math.Tanh(2.8 * Math.Sin(Math.Tau * p + Phase)),
            "jitter" => (Math.Sin(s * 3.1 + seed) + Math.Sin(s * 5.7 + seed * 2.3) + Math.Sin(s * 9.3 + seed * 4.1)) / 3 * Amp * (Decay > 0 ? Math.Clamp(1 - t / Decay, 0, 1) : 1),
            "blink" => blink >= duration ? 0 : -Depth * Math.Sin(Math.PI * blink / duration),
            _ => throw new InvalidOperationException("Unsupported companion animation: " + Type),
        };
    }
}

internal sealed class CompanionPoseEngine
{
    // Body x,y,scale,rotate,breathe,ribbons,confetti,sketch,zzz,orbit,yaw; two eyes:
    // x,y,scaleX,scaleY,rotate,open,lookX,lookY. Arrays are reused, not allocated every frame.
    internal const int Left = 11, Right = 19;
    private readonly CompanionCatalog catalog = CompanionCatalog.Shared.Value;
    private readonly Random random;
    private readonly double seed;
    private readonly double[] previous = new double[27];
    internal readonly double[] Pose = new double[27];
    internal readonly double[][][] Rings;
    private readonly double[][][] ringStart;
    private double[][][] ringTarget;
    private readonly Spring ring = new(1), open = new(1);
    private Spring? spin;
    private readonly Queue<(double At, double Value)> blinks = new();
    private double start, transitionStart, transitionDuration, lastTime, nextPool, nextBlink, nextAntic, bounceAt = -1;
    private double gazeX, gazeY, gazeTargetX, gazeTargetY, ringSpeed = 8;
    private int poolPosition;
    private bool sequenceDone, initialized;
    internal CompanionDefinition Definition { get; private set; }
    internal string RequestedEmotion { get; private set; } = "";
    internal int BurstVersion { get; private set; }
    internal int SpinVersion { get; private set; }
    internal bool Spinning => spin is not null;
    internal int PoolPosition => poolPosition;
    // Leave at least half of a 12-second item to see another eye shape. Preserve
    // the original range ratio and every faster rhythm; do not rewrite the asset.
    internal static double PoolIntervalScale(CompanionDefinition definition) =>
        Math.Min(1, CompanionEmotionDirector.RotationInterval.TotalMilliseconds / 2 / definition.PoolMs[1]);

    internal CompanionPoseEngine(int? randomSeed = null)
    {
        random = randomSeed.HasValue ? new Random(randomSeed.Value) : new Random();
        seed = random.NextDouble() * 100;
        Definition = catalog.Get("02");
        Rings = Copy(catalog.Expressions[0]);
        ringStart = Copy(Rings);
        ringTarget = catalog.Expressions[0];
        SetEmotion("02", 0, false);
    }

    internal void SetEmotion(string id, double now, bool animated)
    {
        if (RequestedEmotion == id) return;
        RequestedEmotion = id;
        Enter(catalog.Get(id), now, animated);
    }

    private void Enter(CompanionDefinition definition, double now, bool animated)
    {
        Array.Copy(Pose, previous, Pose.Length);
        Definition = definition;
        start = transitionStart = lastTime = now;
        transitionDuration = initialized && animated ? definition.Transition : 0;
        initialized = true;
        sequenceDone = false;
        poolPosition = 0;
        SetRing(definition.Pool[0], definition.PoolSpeed >= 10 ? 10 : 8);
        nextPool = now + Between(definition.PoolMs) * PoolIntervalScale(definition);
        nextBlink = definition.BlinkMs is null ? double.PositiveInfinity : now + Between(definition.BlinkMs);
        nextAntic = now + 2500 + random.NextDouble() * 2500;
        blinks.Clear();
        spin = null;
        bounceAt = -1;
        if (animated && definition.BlinkMs is not null) Blink(now);
        if (animated && definition.Base[5] > 0) Spin();
        if (animated && definition.Base[6] > 0) BurstVersion++;
        if (!animated) Sample(now, false);
    }

    internal CompanionGazeSample GazeSample => new(gazeTargetX, gazeTargetY, gazeX, gazeY, Definition.Gaze);
    internal void Gaze(double x, double y) { gazeTargetX = Math.Clamp(x, -1, 1) * 32; gazeTargetY = Math.Clamp(y, -1, 1) * 22; }

    internal void Sample(double now, bool animated)
    {
        var dt = Math.Clamp((now - lastTime) / 1000, .001, .05);
        lastTime = now;
        var t = Math.Max(0, now - start);
        Array.Copy(Definition.Base, Pose, Pose.Length);
        if (animated && !sequenceDone && Definition.Sequence is { Frames.Length: > 0 } seq)
        {
            var frames = seq.Frames;
            var last = frames[^1];
            if (t >= last.At)
            {
                // Legacy wake sequences requested another emotion (02). Hold this
                // item's own final pose instead; only the director selects IDs.
                if (seq.Settle.ValueKind == JsonValueKind.Object
                    || seq.Settle.ValueKind == JsonValueKind.String && seq.Settle.GetString() == "hold")
                    Array.Copy(last.Pose, Pose, Pose.Length);
                else
                {
                    sequenceDone = true;
                    Array.Copy(last.Pose, previous, Pose.Length);
                    transitionStart = now;
                    transitionDuration = Definition.Transition;
                }
            }
            else if (t <= frames[0].At) Array.Copy(frames[0].Pose, Pose, Pose.Length);
            else for (var i = 0; i < frames.Length - 1; i++)
                if (t >= frames[i].At && t < frames[i + 1].At)
                { Lerp(frames[i].Pose, frames[i + 1].Pose, Ease((t - frames[i].At) / (frames[i + 1].At - frames[i].At)), Pose); break; }
        }
        if (!animated)
        {
            ring.X = 1; ring.V = 0;
            open.X = open.Target = Definition.Openness; open.V = 0;
            spin = null; bounceAt = -1; blinks.Clear();
            transitionDuration = 0;
        }
        else
        {
            var ph = Math.Tau * now / 3600;
            Pose[2] += Pose[4] * Math.Sin(ph);
            Pose[1] += Pose[4] * 55 * Math.Sin(ph + .6);
            foreach (var a in Definition.Anims)
            {
                var v = a.Sample(t, seed);
                if (a.Target == "body") AddAnimation(0, a.Prop, v);
                if (a.Target is "eyes" or "left") AddAnimation(Left, a.Prop, v);
                if (a.Target is "eyes" or "right") AddAnimation(Right, a.Prop, v);
            }
            if (now >= nextPool)
            {
                if (Definition.Pool.Length > 1)
                { poolPosition = (poolPosition + 1 + random.Next(Definition.Pool.Length - 1)) % Definition.Pool.Length; SetRing(Definition.Pool[poolPosition], Definition.PoolSpeed); }
                nextPool = now + Between(Definition.PoolMs) * PoolIntervalScale(Definition);
            }
            if (now >= nextBlink && Definition.BlinkMs is { } interval)
            { Blink(now); nextBlink = now + Between(interval); }
            while (blinks.TryPeek(out var key) && now >= key.At) { open.Target = key.Value; blinks.Dequeue(); }
            if (blinks.Count == 0) open.Target = Definition.Openness;
            if (Definition.Antics && now >= nextAntic)
            {
                if (spin is null && bounceAt < 0)
                { var pick = random.NextDouble(); if (pick < .45) Spin(); else if (pick < .8) bounceAt = now; else Blink(now); }
                nextAntic = now + 9000 + random.NextDouble() * 9000;
            }
            var steps = (int)Math.Ceiling(dt * 120);
            for (var i = 0; i < steps; i++)
            {
                ring.Step(ringSpeed, dt / steps);
                open.Step(26, dt / steps);
                spin?.Step(6.2, dt / steps);
            }
            if (spin is not null && Math.Abs(spin.Target - spin.X) < .01 && Math.Abs(spin.V) < .05) spin = null;
            Pose[10] = spin?.X ?? 0;
            if (bounceAt >= 0)
            {
                var elapsed = (now - bounceAt) / 1000;
                ReadOnlySpan<double> heights = [48, 28, 14, 6];
                ReadOnlySpan<double> durations = [.5, .382, .27, .177];
                for (var i = 0; i < 4; i++)
                {
                    if (elapsed < durations[i]) { var n = elapsed / durations[i]; Pose[1] -= 4 * heights[i] * n * (1 - n); break; }
                    elapsed -= durations[i]; if (i == 3) bounceAt = -1;
                }
            }
        }
        var k = animated ? 1 - Math.Exp(-5.66 * dt) : 1;
        gazeX += ((animated && Definition.Gaze ? gazeTargetX : 0) - gazeX) * k;
        gazeY += ((animated && Definition.Gaze ? gazeTargetY : 0) - gazeY) * k;
        for (var e = 0; e < 2; e++)
        {
            var offset = e == 0 ? Left : Right;
            Pose[offset + 6] += gazeX; Pose[offset + 7] += gazeY;
            if (animated && Definition.Gaze)
            {
                var w = now / 1000;
                Pose[offset + 6] += 1.4 * Math.Sin(.42 * w + e) + .5 * Math.Sin(w + e * 2);
                Pose[offset + 7] += .9 * Math.Sin(.58 * w + e);
            }
            Pose[offset + 2] = Math.Max(.05, Pose[offset + 2] * 1.15);
            Pose[offset + 3] = Math.Max(.05, Pose[offset + 3] * 1.15);
            Pose[offset + 5] = Math.Clamp(Pose[offset + 5], 0, 1.3) * Math.Clamp(open.X, .02, 1.5);
            for (var i = 0; i < Rings[e].Length; i++)
                for (var axis = 0; axis < 2; axis++) Rings[e][i][axis] = ringStart[e][i][axis] + (ringTarget[e][i][axis] - ringStart[e][i][axis]) * Math.Clamp(ring.X, 0, 1.35);
        }
        if (animated && transitionDuration > 0 && now - transitionStart < transitionDuration)
            Lerp(previous, Pose, Ease((now - transitionStart) / transitionDuration), Pose);
    }

    private void SetRing(int index, double speed)
    {
        for (var e = 0; e < 2; e++) for (var i = 0; i < Rings[e].Length; i++) Array.Copy(Rings[e][i], ringStart[e][i], 2);
        ringTarget = catalog.Expressions[index]; ringSpeed = speed; ring.X = 0; ring.Target = 1;
    }
    private void Spin() { spin = new Spring(0) { Target = Math.Tau * (random.Next(2) == 0 ? -1 : 1) }; SpinVersion++; }
    private void Blink(double now)
    {
        blinks.Clear();
        foreach (var (at, value) in new[] { (0, .05), (70, .05), (150, 1.08), (300, 1.0) }) blinks.Enqueue((now + at, value));
        if (random.NextDouble() < .14) { blinks.Enqueue((now + 370, .05)); blinks.Enqueue((now + 480, 1)); }
    }
    private double Between(double[] range) => range[0] + random.NextDouble() * (range[1] - range[0]);
    private void AddAnimation(int offset, string prop, double value)
    {
        var index = prop switch { "x" => 0, "y" => 1, "scale" or "scaleX" => 2, "scaleY" => 3, "rotate" => offset == 0 ? 3 : 4, "open" => 5, "lookX" => 6, "lookY" => 7, _ => -1 };
        if (index < 0) throw new InvalidOperationException("Unsupported companion property: " + prop);
        Pose[offset + index] += value;
        if (offset != 0 && prop == "scale") Pose[offset + 3] += value;
    }
    private static double[][][] Copy(double[][][] value) => value.Select(e => e.Select(p => p.ToArray()).ToArray()).ToArray();
    private static double Ease(double t) => t < .5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    private static void Lerp(double[] a, double[] b, double t, double[] output)
    { for (var i = 0; i < output.Length; i++) output[i] = a[i] + (b[i] - a[i]) * t; }
    private sealed class Spring(double value)
    {
        internal double X = value, V, Target = value;
        internal void Step(double frequency, double dt) { V += (-2 * frequency * V - frequency * frequency * (X - Target)) * dt; X += V * dt; }
    }
}
