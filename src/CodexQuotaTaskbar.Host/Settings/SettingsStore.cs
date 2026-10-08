using System.IO;
using System.Text.Json;

namespace CodexQuotaTaskbar.Host.Settings;

internal sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim saveGate = new(1, 1);

    internal static SettingsStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexQuotaTaskbar",
        "settings.json"));

    internal async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
            {
                return AppSettings.Default;
            }

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken);
            return settings is { LowQuotaThreshold: >= 1 and <= 100 }
                ? settings with { MaterialTransparencyPercent = settings.EffectiveMaterialTransparencyPercent }
                : AppSettings.Default;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return AppSettings.Default;
        }
    }

    internal async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await saveGate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("设置路径无效。");
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                var normalized = settings with { MaterialTransparencyPercent = settings.EffectiveMaterialTransparencyPercent };
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
                {
                    await JsonSerializer.SerializeAsync(stream, normalized, JsonOptions, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, path, true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            saveGate.Release();
        }
    }
}
