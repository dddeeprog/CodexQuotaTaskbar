using System.Text.Json;
using CodexQuotaTaskbar.Host.Resets;

namespace CodexQuotaTaskbar.Host.Tests.Resets;

public sealed class ResetScheduleLiveFactAttribute : FactAttribute
{
    public ResetScheduleLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CQT_RESET_SCHEDULE_LIVE_TEST") != "1")
            Skip = "Opt-in read-only public status check; no account, product startup or message submission.";
    }
}

public sealed class ResetScheduleLiveReadTests
{
    [ResetScheduleLiveFact]
    public async Task Real_public_status_is_read_by_the_product_client_without_account_or_browser()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var service = new ResetScheduleClient();
        var value = await service.FetchAsync(lifetime.Token);
        Assert.NotNull(value.LastUpdatedAt);
        Assert.False(value.IsLoading);
        if (value.Schedule is { } schedule)
        {
            Assert.True(ResetScheduleClient.IsAllowedSource(schedule.SourceUri));
            Assert.InRange(schedule.Id.Length, 1, 64);
            Assert.True(schedule.ScheduledFor is null || schedule.ScheduledFor >= schedule.AnnouncedAt);
        }
        var proof = new
        {
            utc = DateTimeOffset.UtcNow,
            endpoint = ResetScheduleClient.StatusUri,
            value.LastUpdatedAt,
            value.SourceGeneratedAt,
            value.IsStale,
            scheduled = value.Schedule is not null,
            deadlineUtc = value.Schedule?.ScheduledFor,
            deadlineLocal = value.Schedule?.ScheduledFor is { } due ? (DateTimeOffset?)TimeZoneInfo.ConvertTime(due, TimeZoneInfo.Local) : null,
            localTimeZone = TimeZoneInfo.Local.Id,
            source = value.Schedule?.SourceUri,
            noAccountContext = true,
            noBrowserStarted = true,
            noProductStarted = true,
        };
        var directory = Environment.GetEnvironmentVariable("CQT_RESET_SCHEDULE_PROOF_DIR");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "public-client-read.json"),
                JsonSerializer.Serialize(proof, new JsonSerializerOptions { WriteIndented = true }), lifetime.Token);
        }
    }
}
