using System;
using System.Linq;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

// Pure pose-engine checks. No window, cursor API, rendering callback, account or network is used.
public sealed class CompanionGazeTests
{
    private const double FrameMs = 1000d / 30;

    [Theory]
    [InlineData(1d, 0d)]
    [InlineData(-1d, 0d)]
    [InlineData(0d, 1d)]
    [InlineData(0d, -1d)]
    public void Pointer_direction_moves_both_eyes_without_changing_other_pose_channels(double x, double y)
    {
        var following = new CompanionPoseEngine(42);
        var centered = new CompanionPoseEngine(42);
        following.Gaze(x, y);
        for (var i = 1; i <= 60; i++)
        {
            following.Sample(i * FrameMs, true);
            centered.Sample(i * FrameMs, true);
        }
        foreach (var eye in new[] { CompanionPoseEngine.Left, CompanionPoseEngine.Right })
        {
            var dx = following.Pose[eye + 6] - centered.Pose[eye + 6];
            var dy = following.Pose[eye + 7] - centered.Pose[eye + 7];
            if (x == 0) Assert.InRange(Math.Abs(dx), 0, 1e-8);
            else Assert.True(dx * x > 10, "Horizontal gaze must follow the requested direction.");
            if (y == 0) Assert.InRange(Math.Abs(dy), 0, 1e-8);
            else Assert.True(dy * y > 6, "Vertical gaze must follow the requested direction.");
        }
        for (var channel = 0; channel < following.Pose.Length; channel++)
        {
            if (channel is 17 or 18 or 25 or 26) continue;
            Assert.Equal(centered.Pose[channel], following.Pose[channel]);
        }
    }

    [Fact]
    public void Pointer_target_survives_all_32_emotion_changes_and_non_gaze_expressions()
    {
        var following = new CompanionPoseEngine(42);
        var centered = new CompanionPoseEngine(42);
        // Set only once: switching expressions must not require another mouse movement.
        following.Gaze(1, -1);
        var now = 0d;
        var ids = CompanionEmotionDirector.EmotionIds.ToArray();
        new Random(17).Shuffle(ids);
        foreach (var id in ids)
        {
            following.SetEmotion(id, now, true);
            centered.SetEmotion(id, now, true);
            for (var frame = 0; frame < 180; frame++)
            {
                now += FrameMs;
                following.Sample(now, true);
                centered.Sample(now, true);
            }
            Assert.Equal(id, following.RequestedEmotion);
            Assert.Equal(centered.Definition.Id, following.Definition.Id);
            foreach (var eye in new[] { CompanionPoseEngine.Left, CompanionPoseEngine.Right })
            {
                var dx = following.Pose[eye + 6] - centered.Pose[eye + 6];
                var dy = following.Pose[eye + 7] - centered.Pose[eye + 7];
                if (following.Definition.Gaze)
                {
                    Assert.True(dx > 15, "A gaze-enabled expression must retain the existing pointer target.");
                    Assert.True(dy < -9, "A gaze-enabled expression must retain the existing pointer target.");
                }
                else
                {
                    Assert.InRange(Math.Abs(dx), 0, 1e-5);
                    Assert.InRange(Math.Abs(dy), 0, 1e-5);
                }
            }
        }
    }

    [Theory]
    [InlineData("00", 0.15)]
    [InlineData("06", 0.6)]
    public void Sleeping_expression_ignores_pointer_without_forcing_eyes_open(string id, double maximumOpen)
    {
        var following = new CompanionPoseEngine(42);
        var centered = new CompanionPoseEngine(42);
        following.Gaze(1, 1);
        var now = 0d;
        for (var frame = 0; frame < 60; frame++)
        {
            now += FrameMs;
            following.Sample(now, true);
            centered.Sample(now, true);
        }
        following.SetEmotion(id, now, true);
        centered.SetEmotion(id, now, true);
        for (var frame = 0; frame < 180; frame++)
        {
            // Repeated cursor updates while sleeping must not change the expression's openness.
            following.Gaze(frame % 2 == 0 ? 1 : -1, 1);
            now += FrameMs;
            following.Sample(now, true);
            centered.Sample(now, true);
        }
        Assert.False(following.Definition.Gaze);
        foreach (var eye in new[] { CompanionPoseEngine.Left, CompanionPoseEngine.Right })
        {
            Assert.Equal(centered.Pose[eye + 5], following.Pose[eye + 5]);
            Assert.InRange(following.Pose[eye + 5], 0, maximumOpen);
            Assert.InRange(Math.Abs(following.Pose[eye + 6] - centered.Pose[eye + 6]), 0, 1e-5);
            Assert.InRange(Math.Abs(following.Pose[eye + 7] - centered.Pose[eye + 7]), 0, 1e-5);
        }
    }

    [Fact]
    public void Reduced_motion_emotion_changes_remain_independent_of_pointer_updates()
    {
        var following = new CompanionPoseEngine(42);
        var centered = new CompanionPoseEngine(42);
        var now = 0d;
        foreach (var id in CompanionEmotionDirector.EmotionIds)
        {
            following.SetEmotion(id, now, false);
            centered.SetEmotion(id, now, false);
            following.Gaze(100, -100);
            now += 6000;
            following.Sample(now, false);
            centered.Sample(now, false);
            Assert.Equal(centered.Pose, following.Pose);
            Assert.False(following.Spinning);
        }
    }

    [Fact]
    public void Extended_switching_and_cursor_sampling_reuse_pose_arrays_with_bounded_allocation()
    {
        var engine = new CompanionPoseEngine(42);
        var pose = engine.Pose;
        var rings = engine.Rings;
        var leftRing = rings[0];
        var rightRing = rings[1];
        // Warm animation paths before measuring this thread. This is not a CPU/rendering benchmark.
        for (var frame = 1; frame <= 60; frame++) engine.Sample(frame * FrameMs, true);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var finite = true;
        var now = 2000d;
        for (var cycle = 0; cycle < 2; cycle++)
        {
            foreach (var id in CompanionEmotionDirector.EmotionIds)
            {
                engine.SetEmotion(id, now, true);
                for (var frame = 0; frame < 180; frame++)
                {
                    // Existing 200 ms control cadence feeds a target to a 30 fps pose sampler.
                    if (frame % 6 == 0) engine.Gaze(Math.Sin(now) * 1e6, Math.Cos(now) * 1e6);
                    now += FrameMs;
                    engine.Sample(now, true);
                    for (var channel = 0; channel < engine.Pose.Length; channel++)
                        finite &= double.IsFinite(engine.Pose[channel]);
                }
            }
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(finite);
        Assert.Same(pose, engine.Pose);
        Assert.Same(rings, engine.Rings);
        Assert.Same(leftRing, engine.Rings[0]);
        Assert.Same(rightRing, engine.Rings[1]);
        Assert.InRange(allocated, 0L, 2L * 1024 * 1024);
    }
}
