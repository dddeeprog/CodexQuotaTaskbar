using System.IO;
using System.Text.Json;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

// Pure local selection/engine/diagnostic state. Never construct a UI, run the app or inspect an account.
public sealed class CompanionDiagnosticsTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-23T00:00:00Z");

    [Fact]
    public void Initial_selection_exposes_its_actual_draw_metadata_and_both_clocks()
    {
        var director = new CompanionEmotionDirector(Start, 42);
        var frame = director.Evaluate(Start);
        Assert.Equal(1, frame.Cycle);
        Assert.Equal(1, frame.Position);
        Assert.Equal(1, frame.Draw);
        Assert.Equal(Start, frame.SelectedAtUtc);
        Assert.Equal("initial", frame.Reason);
        var diagnostics = new CompanionDiagnostics();
        var before = DateTimeOffset.UtcNow;
        var gaze = new CompanionGazeSample(4, -3, 2, -1, true);
        Assert.True(Observe(diagnostics, frame, gaze: gaze));
        var after = DateTimeOffset.UtcNow;
        var entry = Assert.Single(diagnostics.Entries);
        Assert.Equal(Environment.ProcessId, entry.Pid);
        Assert.Equal(1, entry.ControlId);
        Assert.Equal("selected", entry.Kind);
        Assert.Equal(frame.Emotion, entry.RequestedId);
        Assert.Equal(frame.Emotion, entry.EffectiveId);
        Assert.Equal(frame.Cycle, entry.Cycle);
        Assert.Equal(frame.Position, entry.Position);
        Assert.Equal(frame.Draw, entry.Draw);
        Assert.Equal(frame.SelectedAtUtc, entry.SelectedAtUtc);
        Assert.Equal("initial", entry.Reason);
        Assert.InRange(entry.Utc, before, after);
        Assert.True(double.IsFinite(entry.MonotonicMs) && entry.MonotonicMs >= 0);
        Assert.Equal(gaze, entry.Gaze);
    }

    [Fact]
    public void Actual_director_draws_record_two_complete_32_item_cycles_and_the_next_boundary()
    {
        var director = new CompanionEmotionDirector(Start, 42);
        var diagnostics = new CompanionDiagnostics();
        for (var draw = 0; draw < 65; draw++)
        {
            var now = Start.AddSeconds(draw * 6);
            var selection = director.Evaluate(now);
            Assert.True(Observe(diagnostics, selection));
            Assert.Equal(draw + 1, selection.Draw);
            Assert.Equal(draw / 32 + 1, selection.Cycle);
            Assert.Equal(draw % 32 + 1, selection.Position);
            Assert.Equal(now, selection.SelectedAtUtc);
            Assert.Equal(draw == 0 ? "initial" : "automatic", selection.Reason);
        }
        var entries = diagnostics.Entries;
        Assert.Equal(65, entries.Count);
        foreach (var cycle in entries.Where(entry => entry.Cycle <= 2).GroupBy(entry => entry.Cycle))
        {
            Assert.Equal(Enumerable.Range(1, 32), cycle.Select(entry => entry.Position));
            Assert.True(cycle.Select(entry => entry.RequestedId).ToHashSet().SetEquals(CompanionEmotionDirector.EmotionIds));
            Assert.All(cycle, entry => { Assert.False(entry.Preview); Assert.Equal("selected", entry.Kind); });
        }
        Assert.NotEqual(entries[31].RequestedId, entries[32].RequestedId);
        Assert.NotEqual(entries[63].RequestedId, entries[64].RequestedId);
        Assert.True(entries.Zip(entries.Skip(1), (previous, current) =>
            previous.Sequence < current.Sequence && previous.MonotonicMs <= current.MonotonicMs).All(value => value));
    }

    [Fact]
    public void Per_frame_clock_gaze_label_and_count_changes_do_not_append_or_rewrite_the_file()
    {
        var path = TemporaryPath();
        var diagnostics = new CompanionDiagnostics(path);
        var frame = Frame();
        Assert.True(Observe(diagnostics, frame));
        var unchanged = Assert.Single(diagnostics.Entries);
        var sentinel = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, sentinel);
        for (var index = 0; index < 2000; index++)
        {
            var varied = frame with { Count = index, Label = "Synthetic private task label " + index };
            Assert.False(Observe(diagnostics, varied, gaze: new(index % 32, -(index % 22), .5, -.5, true)));
        }
        Assert.Single(diagnostics.Entries);
        Assert.Equal(unchanged, diagnostics.Entries[0]);
        Assert.Equal(sentinel, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Interleaved_controls_and_event_kinds_are_deduplicated_independently()
    {
        var diagnostics = new CompanionDiagnostics();
        var frame = Frame();
        Assert.True(Observe(diagnostics, frame, control: 1));
        Assert.True(Observe(diagnostics, frame, control: 2));
        Assert.True(Observe(diagnostics, frame, control: 1, kind: "presented"));
        for (var index = 0; index < 100; index++)
        {
            Assert.False(Observe(diagnostics, frame, control: 1));
            Assert.False(Observe(diagnostics, frame, control: 2));
            Assert.False(Observe(diagnostics, frame, control: 1, kind: "presented"));
        }
        Assert.Equal(3, diagnostics.Entries.Count);
    }

    [Theory]
    [InlineData("visible")]
    [InlineData("reduced")]
    [InlineData("preview")]
    public void Necessary_state_changes_and_their_return_to_a_previous_state_are_recorded(string state)
    {
        var diagnostics = new CompanionDiagnostics();
        var frame = Frame();
        Assert.True(Observe(diagnostics, frame));
        Assert.True(Observe(diagnostics, state == "preview" ? frame with { Preview = true, Reason = "preview" } : frame,
            visible: state != "visible", reduced: state == "reduced"));
        Assert.True(Observe(diagnostics, frame));
        Assert.False(Observe(diagnostics, frame));
        Assert.Equal(3, diagnostics.Entries.Count);
    }

    [Theory]
    [InlineData("01", 2101d)]
    [InlineData("07", 1601d)]
    public void A_real_sequence_effective_definition_change_does_not_invent_another_random_draw(string requested, double settledAt)
    {
        var diagnostics = new CompanionDiagnostics();
        var frame = Frame(requested);
        var engine = new CompanionPoseEngine(42);
        engine.SetEmotion(requested, 0, true);
        Assert.Equal(requested, engine.RequestedEmotion);
        Assert.Equal(requested, engine.Definition.Id);
        Assert.True(diagnostics.Observe(1, "presented", frame, engine.RequestedEmotion, engine.Definition.Id,
            true, false, engine.GazeSample));
        engine.Sample(settledAt, true);
        Assert.Equal(requested, engine.RequestedEmotion);
        Assert.Equal("02", engine.Definition.Id);
        Assert.True(diagnostics.Observe(1, "presented", frame, engine.RequestedEmotion, engine.Definition.Id,
            true, false, engine.GazeSample));
        Assert.False(diagnostics.Observe(1, "presented", frame, engine.RequestedEmotion, engine.Definition.Id,
            true, false, engine.GazeSample));
        Assert.Equal(2, diagnostics.Entries.Count);
        Assert.Equal(new[] { requested, "02" }, diagnostics.Entries.Select(entry => entry.EffectiveId));
        Assert.All(diagnostics.Entries, entry =>
        {
            Assert.Equal(requested, entry.RequestedId);
            Assert.Equal(frame.Cycle, entry.Cycle);
            Assert.Equal(frame.Position, entry.Position);
            Assert.Equal(frame.Draw, entry.Draw);
            Assert.Equal(frame.SelectedAtUtc, entry.SelectedAtUtc);
        });
    }

    [Fact]
    public void Capacity_bounds_memory_and_json_while_sequence_numbers_continue_forward()
    {
        var path = TemporaryPath();
        var diagnostics = new CompanionDiagnostics(path, capacity: 4);
        for (var index = 0; index < 10; index++)
        {
            var frame = Frame(CompanionEmotionDirector.EmotionIds[index]) with
            { Position = index + 1, Draw = index + 1, SelectedAtUtc = Start.AddSeconds(index * 6), Reason = index == 0 ? "initial" : "automatic" };
            Assert.True(Observe(diagnostics, frame));
            using var current = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(Math.Min(4, index + 1), current.RootElement.GetProperty("Entries").GetArrayLength());
        }
        Assert.Equal(new long[] { 7, 8, 9, 10 }, diagnostics.Entries.Select(entry => entry.Draw));
        Assert.Equal(new long[] { 7, 8, 9, 10 }, diagnostics.Entries.Select(entry => entry.Sequence));
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1, json.RootElement.GetProperty("Schema").GetInt32());
        Assert.Equal(Environment.ProcessId, json.RootElement.GetProperty("Pid").GetInt32());
        Assert.Equal(4, json.RootElement.GetProperty("Entries").GetArrayLength());
    }

    [Fact]
    public void Entries_is_a_detached_snapshot_and_does_not_change_after_later_observations()
    {
        var diagnostics = new CompanionDiagnostics();
        Assert.True(Observe(diagnostics, Frame()));
        var previous = diagnostics.Entries;
        Assert.True(Observe(diagnostics, Frame("03") with { Position = 2, Draw = 2 }));
        Assert.Single(previous);
        Assert.Equal(2, diagnostics.Entries.Count);
        Assert.Equal("02", previous[0].RequestedId);
    }

    [Fact]
    public void Typed_json_contains_only_transition_metadata_and_never_the_frame_label()
    {
        var path = TemporaryPath();
        var diagnostics = new CompanionDiagnostics(path);
        var frame = Frame() with { Label = "SYNTHETIC-PRIVATE-MESSAGE-AND-CREDENTIAL", Count = 987654 };
        Assert.True(Observe(diagnostics, frame));
        var json = File.ReadAllText(path);
        Assert.DoesNotContain(frame.Label, json);
        Assert.DoesNotContain("\"Label\"", json);
        Assert.DoesNotContain("\"Count\"", json);
        using var parsed = JsonDocument.Parse(json);
        var entry = parsed.RootElement.GetProperty("Entries")[0];
        Assert.Equal("02", entry.GetProperty("RequestedId").GetString());
        Assert.Equal("02", entry.GetProperty("EffectiveId").GetString());
    }

    [Theory]
    [InlineData("requested")]
    [InlineData("effective")]
    [InlineData("kind")]
    [InlineData("reason")]
    [InlineData("control")]
    public void Invalid_identifiers_kinds_reasons_and_control_ids_are_rejected_without_a_file(string invalid)
    {
        var path = TemporaryPath();
        var diagnostics = new CompanionDiagnostics(path);
        var frame = Frame() with { Reason = invalid == "reason" ? "PRIVATE-INVALID-REASON" : "initial" };
        Assert.False(diagnostics.Observe(invalid == "control" ? 0 : 1, invalid == "kind" ? "PRIVATE-INVALID-KIND" : "selected",
            frame, invalid == "requested" ? "PRIVATE-INVALID-ID" : "02", invalid == "effective" ? "PRIVATE-INVALID-ID" : "02",
            true, false));
        Assert.Empty(diagnostics.Entries);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(double.NaN, 0, 0, 0)]
    [InlineData(0, double.PositiveInfinity, 0, 0)]
    [InlineData(0, 0, double.NegativeInfinity, 0)]
    [InlineData(0, 0, 0, double.NaN)]
    public void Nonfinite_gaze_samples_are_rejected_before_json_serialization(double tx, double ty, double ox, double oy)
    {
        var diagnostics = new CompanionDiagnostics();
        Assert.False(Observe(diagnostics, Frame(), gaze: new(tx, ty, ox, oy, true)));
        Assert.Empty(diagnostics.Entries);
    }

    [Fact]
    public void A_file_write_failure_does_not_throw_or_drop_the_accepted_in_memory_transition()
    {
        var directory = Path.GetDirectoryName(TemporaryPath())!;
        var diagnostics = new CompanionDiagnostics(directory);
        Assert.True(Observe(diagnostics, Frame()));
        Assert.Single(diagnostics.Entries);
        Assert.True(Observe(diagnostics, Frame("03") with { Position = 2, Draw = 2 }));
        Assert.Equal(2, diagnostics.Entries.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(513)]
    public void Out_of_range_capacity_is_rejected(int capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompanionDiagnostics(capacity: capacity));

    private static CompanionFrame Frame(string id = "02") =>
        new(id, 0, "Synthetic local task label.", false, 1, 1, 1, Start, "initial");

    private static bool Observe(CompanionDiagnostics diagnostics, CompanionFrame frame, int control = 1,
        string kind = "selected", bool visible = true, bool reduced = false, CompanionGazeSample gaze = default) =>
        diagnostics.Observe(control, kind, frame, frame.Emotion, frame.Emotion, visible, reduced, gaze);

    private static string TemporaryPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CqtCompanionDiagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        // Newly generated, test-owned local files are left for normal temporary-file cleanup.
        return Path.Combine(directory, "companion.json");
    }
}
