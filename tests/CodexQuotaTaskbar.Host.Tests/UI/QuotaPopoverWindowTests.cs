using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Host.UI;
using System.Windows.Media.Animation;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class QuotaPopoverWindowTests
{
    [Fact]
    public void Details_blur_the_capture_layer_behind_the_content_surface()
    {
        Assert.Equal("BackdropLayer", QuotaPopoverWindow.GlassCaptureTargetName);
        Assert.Equal(SessionStackWindow.SessionGlassBlurRadius, QuotaPopoverWindow.GlassBlurRadius);
        Assert.Equal(SessionStackWindow.SessionGlassMerging, QuotaPopoverWindow.GlassMerging);
    }

    [Theory]
    [InlineData("codex", "Codex · 7 天额度")]
    [InlineData("CODEX", "Codex · 7 天额度")]
    [InlineData("codex_bengalfox", "Spark · 7 天额度")]
    [InlineData("other_limit", "其他 · 7 天额度")]
    public void Formats_quota_source_in_window_title(string limitId, string expected)
    {
        var window = new QuotaWindowSnapshot(
            QuotaWindowKind.Primary,
            50,
            10080,
            DateTimeOffset.UtcNow.AddDays(7),
            limitId);

        Assert.Equal(expected, QuotaPopoverWindow.FormatWindowTitle(window));
    }

    [Fact]
    public void Orders_codex_above_spark_and_spark_above_other_sources()
    {
        Assert.True(QuotaPopoverWindow.SourceRank("codex") < QuotaPopoverWindow.SourceRank("codex_bengalfox"));
        Assert.True(QuotaPopoverWindow.SourceRank("codex_bengalfox") < QuotaPopoverWindow.SourceRank("other_limit"));
    }

    [Theory]
    [InlineData(800, 100, 1128, 460, 0, 10)]
    [InlineData(800, 620, 1128, 980, 0, -10)]
    [InlineData(400, 300, 728, 660, 10, 0)]
    [InlineData(1192, 300, 1520, 660, -10, 0)]
    public void Details_enter_from_the_direction_of_the_island_and_session_group(
        int left, int top, int right, int bottom, double expectedX, double expectedY)
    {
        var anchor = new ScreenRect(800, 480, 1120, 600);
        var offset = QuotaPopoverWindow.CalculateEntranceOffset(anchor, new ScreenRect(left, top, right, bottom));

        Assert.Equal((expectedX, expectedY), offset);
        Assert.InRange(QuotaPopoverWindow.EntranceAnimationMilliseconds, 150, 300);
        Assert.InRange(QuotaPopoverWindow.ExitAnimationMilliseconds, 100, 180);
        Assert.Equal(EasingMode.EaseOut, QuotaPopoverWindow.ExitEasingMode);
    }
}
