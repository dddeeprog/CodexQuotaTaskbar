using System;
using System.Windows;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

// Pure coordinate/pose checks. Never creates a window, reads/moves a real cursor, or initializes a browser.
public sealed class CompanionGazeMappingTests
{
    private const double FrameMs = 1000d / 30;

    [Fact]
    public void Far_pointer_angles_remain_distinct_instead_of_both_axes_saturating()
    {
        var mostlyRight = CompanionBallControl.NormalizeGaze(new Point(28 + 720, 28 + 240));
        var mostlyDown = CompanionBallControl.NormalizeGaze(new Point(28 + 240, 28 + 720));
        Assert.True(mostlyRight.X > mostlyRight.Y);
        Assert.True(mostlyDown.Y > mostlyDown.X);
        Assert.InRange(mostlyRight.X - mostlyDown.X, 0.6, 0.7);
        Assert.InRange(mostlyDown.Y - mostlyRight.Y, 0.6, 0.7);
        Assert.InRange(Math.Abs(mostlyRight.X / mostlyRight.Y - 3), 0, 1e-12);
        Assert.InRange(Math.Abs(mostlyDown.Y / mostlyDown.X - 3), 0, 1e-12);
        Assert.InRange(Math.Abs(mostlyRight.X * mostlyRight.X + mostlyRight.Y * mostlyRight.Y - 1), 0, 1e-12);
        Assert.InRange(Math.Abs(mostlyDown.X * mostlyDown.X + mostlyDown.Y * mostlyDown.Y - 1), 0, 1e-12);
    }

    [Theory]
    [InlineData(720d, 240d)]
    [InlineData(-720d, 240d)]
    [InlineData(720d, -240d)]
    [InlineData(-720d, -240d)]
    public void Far_direction_preserves_quadrant_and_is_independent_of_distance(double x, double y)
    {
        var near = CompanionBallControl.NormalizeGaze(new Point(28 + x, 28 + y));
        var far = CompanionBallControl.NormalizeGaze(new Point(28 + x * 1000, 28 + y * 1000));
        Assert.Equal(Math.Sign(x), Math.Sign(near.X));
        Assert.Equal(Math.Sign(y), Math.Sign(near.Y));
        Assert.InRange(Math.Abs(near.X - far.X), 0, 1e-12);
        Assert.InRange(Math.Abs(near.Y - far.Y), 0, 1e-12);
    }

    [Fact]
    public void Center_and_near_field_are_continuous_including_the_radial_limit()
    {
        var center = CompanionBallControl.NormalizeGaze(new Point(28, 28));
        Assert.Equal(0, center.X);
        Assert.Equal(0, center.Y);
        var halfway = CompanionBallControl.NormalizeGaze(new Point(28 + 36, 28));
        Assert.InRange(Math.Abs(halfway.X - 0.5), 0, 1e-12);
        Assert.Equal(0, halfway.Y);
        var previous = 0d;
        for (var i = 1; i <= 100; i++)
        {
            var current = CompanionBallControl.NormalizeGaze(new Point(28 + i, 28));
            Assert.InRange(current.X, previous, 1);
            Assert.InRange(current.X - previous, 0, 0.014);
            previous = current.X;
        }
        var inside = CompanionBallControl.NormalizeGaze(new Point(28 + 72 - 1e-6, 28));
        var outside = CompanionBallControl.NormalizeGaze(new Point(28 + 72 + 1e-6, 28));
        Assert.InRange(Math.Abs(inside.X - outside.X), 0, 1e-6);
        Assert.Equal(inside.Y, outside.Y);
    }

    [Fact]
    public void Finite_coordinates_including_extremes_produce_finite_bounded_targets()
    {
        foreach (var value in new[] { -double.MaxValue, -1e200, -1e12, -1d, 0d, 1d, 1e12, 1e200, double.MaxValue })
        {
            foreach (var point in new[] { new Point(value, value), new Point(value, -value), new Point(value, 28), new Point(28, value) })
            {
                var target = CompanionBallControl.NormalizeGaze(point);
                Assert.True(double.IsFinite(target.X));
                Assert.True(double.IsFinite(target.Y));
                Assert.InRange(target.X, -1, 1);
                Assert.InRange(target.Y, -1, 1);
                Assert.InRange(target.X * target.X + target.Y * target.Y, 0, 1 + 1e-12);
            }
        }
    }

    [Theory]
    [InlineData(1000d, 0d, 1d, 0d)]
    [InlineData(-1000d, 0d, -1d, 0d)]
    [InlineData(0d, 1000d, 0d, 1d)]
    [InlineData(0d, -1000d, 0d, -1d)]
    public void Mapped_pointer_uses_the_new_bounded_32_by_22_amplitude(double x, double y, double expectedX, double expectedY)
    {
        var target = CompanionBallControl.NormalizeGaze(new Point(28 + x, 28 + y));
        var following = new CompanionPoseEngine(42);
        var centered = new CompanionPoseEngine(42);
        following.Gaze(target.X, target.Y);
        SamplePair(following, centered, 0, 180, true);
        foreach (var eye in new[] { CompanionPoseEngine.Left, CompanionPoseEngine.Right })
        {
            var dx = following.Pose[eye + 6] - centered.Pose[eye + 6];
            var dy = following.Pose[eye + 7] - centered.Pose[eye + 7];
            Assert.InRange(Math.Abs(dx - expectedX * 32), 0, 1e-5);
            Assert.InRange(Math.Abs(dy - expectedY * 22), 0, 1e-5);
        }
    }

    [Theory]
    [InlineData("00", 0.15)]
    [InlineData("06", 0.6)]
    [InlineData("41", 1.3)]
    public void Mapped_pointer_respects_catalog_non_gaze_and_existing_eye_openness(string id, double maximumOpen)
    {
        var target = CompanionBallControl.NormalizeGaze(new Point(28 - 720, 28 + 240));
        var following = new CompanionPoseEngine(42);
        var centered = new CompanionPoseEngine(42);
        following.SetEmotion(id, 0, true);
        centered.SetEmotion(id, 0, true);
        following.Gaze(target.X, target.Y);
        var now = SamplePair(following, centered, 0, 180, true);
        Assert.False(following.Definition.Gaze);
        foreach (var eye in new[] { CompanionPoseEngine.Left, CompanionPoseEngine.Right })
        {
            Assert.Equal(centered.Pose[eye + 5], following.Pose[eye + 5]);
            Assert.InRange(following.Pose[eye + 5], 0, maximumOpen);
            Assert.InRange(Math.Abs(following.Pose[eye + 6] - centered.Pose[eye + 6]), 0, 1e-8);
            Assert.InRange(Math.Abs(following.Pose[eye + 7] - centered.Pose[eye + 7]), 0, 1e-8);
        }
        // The stored target is still usable on a later awake expression without another pointer update.
        following.SetEmotion("03", now, true);
        centered.SetEmotion("03", now, true);
        SamplePair(following, centered, now, 180, true);
        foreach (var eye in new[] { CompanionPoseEngine.Left, CompanionPoseEngine.Right })
        {
            Assert.True(following.Pose[eye + 6] - centered.Pose[eye + 6] < -25);
            Assert.True(following.Pose[eye + 7] - centered.Pose[eye + 7] > 5);
        }
    }

    [Fact]
    public void Mapped_pointer_does_not_override_reduced_motion()
    {
        var target = CompanionBallControl.NormalizeGaze(new Point(-1000, 500));
        var following = new CompanionPoseEngine(42);
        var centered = new CompanionPoseEngine(42);
        following.SetEmotion("03", 0, false);
        centered.SetEmotion("03", 0, false);
        following.Gaze(target.X, target.Y);
        SamplePair(following, centered, 0, 60, false);
        Assert.Equal(centered.Pose, following.Pose);
        Assert.False(following.Spinning);
    }

    private static double SamplePair(CompanionPoseEngine following, CompanionPoseEngine centered, double now, int frames, bool animated)
    {
        for (var frame = 0; frame < frames; frame++)
        {
            now += FrameMs;
            following.Sample(now, animated);
            centered.Sample(now, animated);
        }
        return now;
    }
}
