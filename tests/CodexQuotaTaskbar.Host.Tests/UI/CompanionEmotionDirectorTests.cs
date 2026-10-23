using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class CompanionEmotionDirectorTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-23T00:00:00Z");
    private static CodexSessionSnapshot Session(string id, CodexSessionState state, int second = 0,
        CodexSessionActivity activity = CodexSessionActivity.Unknown) => new(id, "任务", state, Start.AddSeconds(second)) { Activity = activity };
    private static CodexSessionsSnapshot Snapshot(params CodexSessionSnapshot[] sessions) => CodexSessionsSnapshot.Create(sessions, Start);

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(901)]
    public void Each_random_bag_visits_all_32_once_and_round_boundaries_never_repeat(int seed)
    {
        var director = new CompanionEmotionDirector(Start, seed);
        var frames = Enumerable.Range(0, 32 * 5)
            .Select(index => director.Evaluate(Start.AddSeconds(index * 6))).ToArray();
        Assert.Equal(32, CompanionEmotionDirector.EmotionIds.Length);
        Assert.Equal(32, CompanionEmotionDirector.EmotionIds.Distinct().Count());
        foreach (var round in frames.Chunk(32))
        {
            Assert.Equal(32, round.Select(frame => frame.Emotion).Distinct().Count());
            Assert.True(round.Select(frame => frame.Emotion).ToHashSet()
                .SetEquals(CompanionEmotionDirector.EmotionIds));
            Assert.All(round, frame => Assert.False(frame.Preview));
        }
        for (var index = 1; index < frames.Length; index++)
            Assert.NotEqual(frames[index - 1].Emotion, frames[index].Emotion);
    }

    [Fact]
    public void Seeded_sequences_repeat_for_the_same_seed_and_differ_across_seeds()
    {
        static string[] Draw(int seed)
        {
            var director = new CompanionEmotionDirector(Start, seed);
            return Enumerable.Range(0, 96).Select(i => director.Evaluate(Start.AddSeconds(i * 6)).Emotion).ToArray();
        }
        var first = Draw(42);
        Assert.Equal(first, Draw(42));
        Assert.False(first.SequenceEqual(Draw(43)));
    }

    [Fact]
    public void A_late_first_evaluation_draws_once_from_the_creation_clock_and_starts_a_full_dwell()
    {
        var control = new CompanionEmotionDirector(Start, 42);
        control.Evaluate(Start);
        var expectedSecond = control.Evaluate(Start.AddSeconds(6)).Emotion;
        var expectedThird = control.Evaluate(Start.AddSeconds(12)).Emotion;
        var director = new CompanionEmotionDirector(Start, 42);
        var firstRead = Start.AddHours(1);
        Assert.Equal(expectedSecond, director.Evaluate(firstRead).Emotion);
        Assert.Equal(expectedSecond, director.Evaluate(firstRead.AddSeconds(6).AddTicks(-1)).Emotion);
        Assert.Equal(expectedThird, director.Evaluate(firstRead.AddSeconds(6)).Emotion);
    }

    [Fact]
    public void Automatic_expression_changes_only_at_the_six_second_boundary()
    {
        var director = new CompanionEmotionDirector(Start, 42);
        var first = director.Evaluate(Start).Emotion;
        Assert.Equal(first, director.Evaluate(Start.AddSeconds(6).AddTicks(-1)).Emotion);
        var second = director.Evaluate(Start.AddSeconds(6)).Emotion;
        Assert.NotEqual(first, second);
        Assert.Equal(second, director.Evaluate(Start.AddSeconds(6)).Emotion);
        Assert.Equal(second, director.Evaluate(Start.AddSeconds(12).AddTicks(-1)).Emotion);
        Assert.NotEqual(second, director.Evaluate(Start.AddSeconds(12)).Emotion);
    }

    [Fact]
    public void Long_time_jumps_draw_one_item_and_give_that_item_a_full_visible_dwell()
    {
        var director = new CompanionEmotionDirector(Start, 42);
        var control = new CompanionEmotionDirector(Start, 42);
        Assert.Equal(control.Evaluate(Start).Emotion, director.Evaluate(Start).Emotion);
        var expectedSecond = control.Evaluate(Start.AddSeconds(6)).Emotion;
        var expectedThird = control.Evaluate(Start.AddSeconds(12)).Emotion;
        var late = Start.AddDays(1);
        Assert.Equal(expectedSecond, director.Evaluate(late).Emotion);
        Assert.Equal(expectedSecond, director.Evaluate(late.AddSeconds(6).AddTicks(-1)).Emotion);
        Assert.Equal(expectedThird, director.Evaluate(late.AddSeconds(6)).Emotion);
    }

    [Fact]
    public void Session_changes_update_count_and_label_without_selecting_an_expression_or_restarting_the_clock()
    {
        var director = new CompanionEmotionDirector(Start, 42);
        var control = new CompanionEmotionDirector(Start, 42);
        var first = director.Evaluate(Start).Emotion;
        Assert.Equal(first, control.Evaluate(Start).Emotion);
        var snapshot = Snapshot(Session("run", CodexSessionState.Running), Session("wait", CodexSessionState.Waiting),
            Session("done", CodexSessionState.Review), Session("fail", CodexSessionState.Failed));
        director.Apply(snapshot, Start.AddSeconds(2));
        var frame = director.Evaluate(Start.AddSeconds(2));
        Assert.Equal(first, frame.Emotion);
        Assert.Equal(2, frame.Count);
        Assert.Contains($"进行中 {snapshot.RunningCount}", frame.Label);
        Assert.Contains($"等待处理 {snapshot.WaitingCount}", frame.Label);
        Assert.Contains($"已完成 {snapshot.CompletedCount}", frame.Label);
        Assert.Equal(control.Evaluate(Start.AddSeconds(6)).Emotion, director.Evaluate(Start.AddSeconds(6)).Emotion);
    }

    [Fact]
    public void All_task_states_and_activities_remain_independent_of_random_personality()
    {
        var director = new CompanionEmotionDirector(Start, 42);
        var first = director.Evaluate(Start).Emotion;
        foreach (var state in Enum.GetValues<CodexSessionState>())
        foreach (var activity in Enum.GetValues<CodexSessionActivity>())
        {
            var snapshot = Snapshot(Session("a", state, activity: activity) with { Title = "报错了 生气 检索资料" });
            director.Apply(snapshot, Start.AddSeconds(1));
            var frame = director.Evaluate(Start.AddSeconds(1));
            Assert.Equal(first, frame.Emotion);
            Assert.Equal(snapshot.ActiveCount, frame.Count);
            Assert.False(frame.Preview);
        }
    }

    [Fact]
    public void Hover_does_not_replace_an_expression_or_delay_the_next_draw()
    {
        var director = new CompanionEmotionDirector(Start, 42);
        var control = new CompanionEmotionDirector(Start, 42);
        var first = director.Evaluate(Start).Emotion;
        control.Evaluate(Start);
        director.Interact(Start.AddSeconds(3), false);
        director.Interact(Start.AddSeconds(5), false);
        Assert.Equal(first, director.Evaluate(Start.AddSeconds(5)).Emotion);
        Assert.Equal(control.Evaluate(Start.AddSeconds(6)).Emotion, director.Evaluate(Start.AddSeconds(6)).Emotion);
    }

    [Fact]
    public void Taps_continue_through_the_same_32_item_bags_without_changing_session_count()
    {
        var director = new CompanionEmotionDirector(Start, 42);
        var snapshot = Snapshot(Session("a", CodexSessionState.Running), Session("b", CodexSessionState.Waiting));
        director.Apply(snapshot, Start);
        var frames = new List<CompanionFrame>();
        for (var index = 0; index < 32 * 3; index++)
        {
            var now = Start.AddSeconds(index);
            if (index > 0) director.Interact(now, true);
            frames.Add(director.Evaluate(now));
        }
        foreach (var round in frames.Chunk(32))
            Assert.True(round.Select(frame => frame.Emotion).ToHashSet().SetEquals(CompanionEmotionDirector.EmotionIds));
        for (var index = 1; index < frames.Count; index++)
            Assert.NotEqual(frames[index - 1].Emotion, frames[index].Emotion);
        Assert.All(frames, frame => { Assert.Equal(snapshot.ActiveCount, frame.Count); Assert.False(frame.Preview); });
    }

    [Fact]
    public void A_tap_draws_once_even_after_a_long_gap_and_restarts_a_full_six_seconds()
    {
        var director = new CompanionEmotionDirector(Start, 42);
        var control = new CompanionEmotionDirector(Start, 42);
        director.Evaluate(Start); control.Evaluate(Start);
        var second = control.Evaluate(Start.AddSeconds(6)).Emotion;
        var third = control.Evaluate(Start.AddSeconds(12)).Emotion;
        var tappedAt = Start.AddHours(1);
        director.Interact(tappedAt, true);
        Assert.Equal(second, director.Evaluate(tappedAt).Emotion);
        Assert.Equal(second, director.Evaluate(tappedAt.AddSeconds(6).AddTicks(-1)).Emotion);
        Assert.Equal(third, director.Evaluate(tappedAt.AddSeconds(6)).Emotion);
    }

    [Fact]
    public void Tour_still_shows_every_emotion_in_order_for_three_seconds_each_then_restores_random_current()
    {
        var director = new CompanionEmotionDirector(Start, 42);
        var original = director.Evaluate(Start).Emotion;
        var tourStart = Start.AddSeconds(2);
        director.ToggleTour(tourStart);
        for (var index = 0; index < 32; index++)
        {
            var frame = director.Evaluate(tourStart.AddSeconds(index * 3));
            Assert.True(frame.Preview);
            Assert.Equal(CompanionEmotionDirector.EmotionIds[index], frame.Emotion);
            Assert.Contains("不是任务状态", frame.Label);
            Assert.Equal(frame.Emotion, director.Evaluate(tourStart.AddSeconds(index * 3 + 3).AddTicks(-1)).Emotion);
        }
        var resumed = director.Evaluate(tourStart.AddSeconds(96));
        Assert.False(resumed.Preview);
        Assert.Equal(original, resumed.Emotion);
        Assert.Equal(original, director.Evaluate(tourStart.AddSeconds(102).AddTicks(-1)).Emotion);
        Assert.NotEqual(original, director.Evaluate(tourStart.AddSeconds(102)).Emotion);
    }

    [Fact]
    public void Task_updates_and_taps_neither_interrupt_nor_extend_the_96_second_preview()
    {
        var director = new CompanionEmotionDirector(Start, 42);
        var original = director.Evaluate(Start).Emotion;
        director.ToggleTour(Start);
        var snapshot = Snapshot(Session("wait", CodexSessionState.Waiting, 1), Session("fail", CodexSessionState.Failed, 2));
        director.Apply(snapshot, Start.AddSeconds(1));
        director.Interact(Start.AddSeconds(1), true);
        director.Interact(Start.AddSeconds(2), false);
        var first = director.Evaluate(Start.AddSeconds(2));
        Assert.True(first.Preview); Assert.Equal(CompanionEmotionDirector.EmotionIds[0], first.Emotion);
        Assert.Equal(snapshot.ActiveCount, first.Count);
        director.Interact(Start.AddSeconds(94), true);
        Assert.Equal(CompanionEmotionDirector.EmotionIds[31], director.Evaluate(Start.AddSeconds(95)).Emotion);
        var resumed = director.Evaluate(Start.AddSeconds(96));
        Assert.False(resumed.Preview);
        Assert.Equal(original, resumed.Emotion);
    }

    [Fact]
    public void Manual_preview_stop_restores_the_prior_item_without_consuming_the_random_bag()
    {
        var director = new CompanionEmotionDirector(Start, 42);
        var control = new CompanionEmotionDirector(Start, 42);
        var original = director.Evaluate(Start).Emotion;
        control.Evaluate(Start);
        director.ToggleTour(Start.AddSeconds(2));
        Assert.True(director.Evaluate(Start.AddSeconds(8)).Preview);
        director.ToggleTour(Start.AddSeconds(9));
        var resumed = director.Evaluate(Start.AddSeconds(9));
        Assert.False(resumed.Preview); Assert.Equal(original, resumed.Emotion);
        Assert.Equal(original, director.Evaluate(Start.AddSeconds(15).AddTicks(-1)).Emotion);
        Assert.Equal(control.Evaluate(Start.AddSeconds(6)).Emotion, director.Evaluate(Start.AddSeconds(15)).Emotion);
    }

    [Fact]
    public void Late_preview_expiration_restores_the_saved_item_with_a_full_dwell_instead_of_burning_unseen_rounds()
    {
        var director = new CompanionEmotionDirector(Start, 42);
        var control = new CompanionEmotionDirector(Start, 42);
        var original = director.Evaluate(Start).Emotion;
        control.Evaluate(Start);
        director.ToggleTour(Start.AddSeconds(2));
        Assert.True(director.Evaluate(Start.AddSeconds(3)).Preview);
        var late = Start.AddHours(1);
        Assert.Equal(original, director.Evaluate(late).Emotion);
        Assert.Equal(original, director.Evaluate(late.AddSeconds(6).AddTicks(-1)).Emotion);
        Assert.Equal(control.Evaluate(Start.AddSeconds(6)).Emotion, director.Evaluate(late.AddSeconds(6)).Emotion);
    }

    [Fact]
    public void Preview_before_first_evaluation_does_not_consume_the_random_bag()
    {
        var director = new CompanionEmotionDirector(Start, 42);
        var control = new CompanionEmotionDirector(Start, 42);
        director.ToggleTour(Start);
        Assert.Equal(CompanionEmotionDirector.EmotionIds[0], director.Evaluate(Start).Emotion);
        Assert.True(director.Evaluate(Start.AddSeconds(95)).Preview);
        var resumed = director.Evaluate(Start.AddSeconds(96));
        Assert.False(resumed.Preview);
        Assert.Equal(control.Evaluate(Start).Emotion, resumed.Emotion);
        Assert.Equal(control.Evaluate(Start.AddSeconds(6)).Emotion, director.Evaluate(Start.AddSeconds(102)).Emotion);
    }

    public static IEnumerable<object[]> EmotionCases => CompanionEmotionDirector.EmotionIds.Select(id => new object[] { id });

    [Theory]
    [MemberData(nameof(EmotionCases))]
    public void Native_engine_preserves_every_emotion_and_remains_finite_for_a_minute(string id)
    {
        var engine = new CompanionPoseEngine(42);
        engine.SetEmotion(id, 0, true);
        Assert.Equal(id, engine.RequestedEmotion);
        Assert.Equal(id, engine.Definition.Id);
        Assert.Equal(25, CompanionCatalog.Shared.Value.Expressions.Length);
        var values = new HashSet<double>();
        for (var i = 0; i <= 1800; i++)
        {
            engine.Gaze(Math.Sin(i), Math.Cos(i));
            engine.Sample(i * 1000.0 / 30, true);
            Assert.All(engine.Pose, value => Assert.True(double.IsFinite(value)));
            Assert.InRange(engine.Pose[2], .1, 2);
            Assert.All(engine.Rings, eye => Assert.All(eye, point => Assert.All(point, value => Assert.True(double.IsFinite(value)))));
            values.Add(engine.Pose[1]);
        }
        Assert.True(values.Count > 1, "Expression must not become a static replacement.");
    }

    [Theory]
    [MemberData(nameof(EmotionCases))]
    public void Reduced_motion_freezes_all_32_poses_and_gaze(string id)
    {
        var engine = new CompanionPoseEngine(42);
        engine.SetEmotion(id, 0, false);
        var pose = engine.Pose.ToArray();
        engine.Gaze(1, -1);
        engine.Sample(10000, false);
        Assert.Equal(pose, engine.Pose);
        Assert.False(engine.Spinning);
    }

    [Fact]
    public void Emotion_interrupt_starts_from_current_presentation_pose()
    {
        var engine = new CompanionPoseEngine(42);
        engine.SetEmotion("13", 0, true);
        engine.Sample(140, true);
        var previous = engine.Pose.ToArray();
        engine.SetEmotion("38", 140, true);
        engine.Sample(140, true);
        Assert.Equal(previous, engine.Pose);
    }

    [Fact]
    public void Completion_keeps_ribbons_and_confetti_and_catalog_is_shared()
    {
        var engine = new CompanionPoseEngine(42);
        engine.SetEmotion("33", 0, true);
        Assert.True(engine.Spinning);
        Assert.Equal(1, engine.BurstVersion);
        Assert.Same(CompanionCatalog.Shared.Value.Get("33"), engine.Definition);
    }
}
