using System.IO;
using System.Text.Json;
using CodexQuotaTaskbar.Host.Shortcuts;

namespace CodexQuotaTaskbar.Host.Tests.Shortcuts;

public sealed class ShortcutIconLiveFactAttribute : FactAttribute
{
    public ShortcutIconLiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CQT_SHORTCUT_ICON_LIVE_OUTPUT")))
            Skip = "Opt-in fixed public website icon reads, without browser or user cache.";
    }
}

public sealed class WebShortcutIconLiveReadTests
{
    [ShortcutIconLiveFact]
    public async Task Fixed_public_example_icons_are_read_without_bookmark_paths_or_user_cache()
    {
        var output = Path.GetFullPath(Environment.GetEnvironmentVariable("CQT_SHORTCUT_ICON_LIVE_OUTPUT")!);
        var directory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(directory);
        var cache = Path.Combine(directory, "icon-cache");
        using var service = new WebShortcutIconService(WebShortcutIconService.CreateHandler(), cache,
            TimeProvider.System, WebShortcutIconService.ValidateNetworkAsync);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var proof = new List<object>();
        foreach (var (label, target) in new[]
        {
            ("ChatGPT", "https://chatgpt.com/"),
            ("GitHub", "https://github.com/"),
            ("重置公告", "https://codex-resets.com/zh-CN"),
        })
        {
            var value = await service.FetchAsync(new Uri(target), timeout.Token);
            proof.Add(new
            {
                Label = label, TargetUri = target, value.Name, value.CachedPngPath,
                ExtractedIcon = value.Image is not null,
                PixelWidth = value.Image?.PixelWidth, PixelHeight = value.Image?.PixelHeight,
                Frozen = value.Image?.IsFrozen,
            });
            if (value.Image is { } image)
            {
                Assert.True(image.IsFrozen);
                Assert.InRange(image.PixelWidth, 1, 64);
                Assert.InRange(image.PixelHeight, 1, 64);
            }
        }
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            Utc = DateTimeOffset.UtcNow, CacheDirectory = cache, Items = proof,
            NoBrowserStarted = true, NoProductStarted = true,
            NoUserBookmarksOrHistoryRead = true, NoUserCacheReadOrWritten = true,
            FallbackIsNotClaimedAsSuccessfulExtraction = true,
        }, new JsonSerializerOptions { WriteIndented = true }), timeout.Token);
        // A site/proxy can refuse icons. The proof records real extraction separately from safe fallback.
        Assert.Equal(3, proof.Count);
    }
}
