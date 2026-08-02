using CodexQuotaTaskbar.Core.Quota;

namespace CodexQuotaTaskbar.Core.Tests.Quota;

public sealed class QuotaCapsuleProjectionTests
{
    [Theory]
    [InlineData(72, "Cool")]
    [InlineData(29, "Amber")]
    [InlineData(9, "Critical")]
    public void Projects_remaining_percentage_to_accessible_color_token(double remaining, string token)
    {
        var window = new QuotaWindowSnapshot(QuotaWindowKind.Primary, remaining, 300, DateTimeOffset.UtcNow.AddHours(1));
        var projection = QuotaCapsuleProjection.Create(QuotaSnapshot.Available([window], DateTimeOffset.UtcNow));

        Assert.Equal(token, projection.Rows[0].ColorToken);
    }

    [Fact]
    public void Orders_shorter_window_first_and_limits_capsule_to_two_rows()
    {
        var snapshot = QuotaSnapshot.Available([
            new(QuotaWindowKind.Secondary, 41, 10080, DateTimeOffset.UtcNow.AddDays(7)),
            new(QuotaWindowKind.Primary, 72, 300, DateTimeOffset.UtcNow.AddHours(5)),
            new(QuotaWindowKind.Other, 90, 60, DateTimeOffset.UtcNow.AddHours(1), "other")], DateTimeOffset.UtcNow);

        var result = QuotaCapsuleProjection.Create(snapshot);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("5 小时", result.Rows[0].Label);
        Assert.Equal(72, result.Rows[0].RemainingPercent);
        Assert.Equal("7 天", result.Rows[1].Label);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1)]
    [InlineData(101)]
    public void Rejects_invalid_remaining_percentage(double remaining)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new QuotaWindowSnapshot(QuotaWindowKind.Primary, remaining, 60, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Seven_day_window_fills_five_hour_row_when_five_hour_limit_is_absent()
    {
        var resetsAt = DateTimeOffset.UtcNow.AddDays(7);
        var result = QuotaCapsuleProjection.Create(QuotaSnapshot.Available([
            new(QuotaWindowKind.Secondary, 50, 10080, resetsAt)], DateTimeOffset.UtcNow));

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("5 小时", result.Rows[0].Label);
        Assert.Equal("50%", result.Rows[0].Text);
        Assert.Equal(50, result.Rows[0].RemainingPercent);
        Assert.Equal(resetsAt, result.Rows[0].ResetsAt);
        Assert.Equal("7 天", result.Rows[1].Label);
        Assert.Equal(result.Rows[1].Text, result.Rows[0].Text);
        Assert.Equal(result.Rows[1].RemainingPercent, result.Rows[0].RemainingPercent);
    }

    [Fact]
    public void Unavailable_snapshot_is_explicit()
    {
        var result = QuotaCapsuleProjection.Create(QuotaSnapshot.Unavailable("未登录"));

        Assert.Equal(QuotaAvailability.Unavailable, result.Availability);
        Assert.Equal("未登录", result.StatusText);
    }
}
