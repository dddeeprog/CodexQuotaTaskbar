using CodexQuotaTaskbar.Core.Sessions;

namespace CodexQuotaTaskbar.Host.UI;

// Expression selection is independent of task state. No message text is inspected.
internal sealed class CompanionEmotionDirector
{
    internal static readonly string[] EmotionIds = ["00", "01", "02", "03", "04", "05", "06", "07",
        "10", "11", "12", "13", "14", "15", "16", "17", "18", "19", "20", "21",
        "30", "31", "32", "33", "34", "35", "36", "37", "38", "39", "40", "41"];
    internal static readonly TimeSpan RotationInterval = TimeSpan.FromSeconds(12);
    private readonly Random random;
    private readonly string[] cycle = (string[])EmotionIds.Clone();
    private int position = EmotionIds.Length;
    private long cycleNumber, draw;
    private DateTimeOffset selectedAt;
    private string reason = "initial";
    private string current = "";
    private DateTimeOffset nextChange;
    private DateTimeOffset? hiddenAt;
    private DateTimeOffset? tourStarted;
    private CodexSessionsSnapshot snapshot = CodexSessionsSnapshot.Empty;

    internal CompanionEmotionDirector(DateTimeOffset createdAt, int? randomSeed = null)
    {
        random = randomSeed is { } seed ? new Random(seed) : Random.Shared;
        current = NextEmotion(createdAt, "initial");
        nextChange = createdAt + RotationInterval;
    }

    internal void Apply(CodexSessionsSnapshot value, DateTimeOffset now) => snapshot = value;

    internal void Interact(DateTimeOffset now, bool tap)
    {
        // Personality is decorative. Input still routes to Dot/drag/typing outside
        // this director, but never consumes a bag item or extends its deadline.
    }

    internal void SetVisible(bool visible, DateTimeOffset now)
    {
        if (!visible) { hiddenAt ??= now; return; }
        if (hiddenAt is not { } paused) return;
        var duration = now > paused ? now - paused : TimeSpan.Zero;
        nextChange += duration;
        if (tourStarted is { } start) tourStarted = start + duration;
        hiddenAt = null;
    }

    internal void ToggleTour(DateTimeOffset now)
    {
        if (tourStarted is null) tourStarted = now;
        else
        {
            tourStarted = null;
            nextChange = now + RotationInterval;
        }
    }

    internal CompanionFrame Evaluate(DateTimeOffset now)
    {
        if (hiddenAt is { } paused) now = paused;
        var preview = false;
        var id = current;
        if (tourStarted is { } start)
        {
            var seconds = (now - start).TotalSeconds;
            if (seconds < EmotionIds.Length * 3)
            {
                id = EmotionIds[Math.Clamp((int)(seconds / 3), 0, EmotionIds.Length - 1)];
                preview = true;
            }
            else
            {
                tourStarted = null;
                nextChange = now + RotationInterval;
            }
        }
        else if (hiddenAt is null && now >= nextChange)
        {
            current = id = NextEmotion(now, "automatic");
            // A hidden/suspended interval must not skip unseen expressions or run a catch-up loop.
            nextChange = now + RotationInterval;
        }

        var label = preview ? $"表情试玩 · {Array.IndexOf(EmotionIds, id) + 1}/32（不是任务状态）"
            : $"进行中 {snapshot.RunningCount} · 等待处理 {snapshot.WaitingCount} · 已完成 {snapshot.CompletedCount}";
        return new(id, snapshot.ActiveCount, label, preview, cycleNumber, position, draw,
            selectedAt, preview ? "preview" : reason);
    }

    private string NextEmotion(DateTimeOffset now, string cause)
    {
        if (position == cycle.Length)
        {
            for (var i = cycle.Length - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (cycle[i], cycle[j]) = (cycle[j], cycle[i]);
            }
            if (cycle[0] == current)
            {
                var j = random.Next(1, cycle.Length);
                (cycle[0], cycle[j]) = (cycle[j], cycle[0]);
            }
            position = 0;
            cycleNumber++;
        }
        draw++;
        selectedAt = now;
        reason = cause;
        return cycle[position++];
    }
}

internal sealed record CompanionFrame(string Emotion, int Count, string Label, bool Preview,
    long Cycle = 0, int Position = 0, long Draw = 0, DateTimeOffset SelectedAtUtc = default, string Reason = "initial");
