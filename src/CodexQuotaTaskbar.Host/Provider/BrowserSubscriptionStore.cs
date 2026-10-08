using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexQuotaTaskbar.Core.Quota;

namespace CodexQuotaTaskbar.Host.Provider;

internal sealed class BrowserSubscriptionStore
{
    private const int MaximumFileBytes = 64 * 1024;
    private const int MaximumAccounts = 16;
    private static readonly TimeSpan FreshnessPeriod = TimeSpan.FromMinutes(3);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4,
    };
    private readonly string path;
    private readonly object sync = new();

    internal BrowserSubscriptionStore(string? path = null)
    {
        this.path = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexQuotaTaskbar",
            "subscription-web.json"));
    }

    internal SubscriptionExpirationSnapshot? Read(string? accountId, DateTimeOffset now)
    {
        if (!IsValidAccountId(accountId))
        {
            return null;
        }

        lock (sync)
        {
            var record = ReadRecords().FirstOrDefault(item =>
                string.Equals(item.AccountId, accountId, StringComparison.Ordinal));
            if (record is null || !IsValid(record, now) || record.ExpiresAt <= now)
            {
                return null;
            }

            var snapshot = SubscriptionExpirationSnapshot.Available(
                record.ExpiresAt,
                record.CheckedAt,
                SubscriptionExpirationSource.ChatGptWeb);
            return now - record.CheckedAt >= FreshnessPeriod ? snapshot.AsStale() : snapshot;
        }
    }

    internal bool TrySave(string accountId, DateTimeOffset expiresAt, DateTimeOffset checkedAt)
    {
        var record = new BrowserSubscriptionRecord(accountId, expiresAt, checkedAt);
        var now = DateTimeOffset.UtcNow;
        if (!IsValid(record, now) || expiresAt <= now)
        {
            return false;
        }

        lock (sync)
        {
            string? temporary = null;
            try
            {
                if (HasReparsePointAncestor())
                {
                    return false;
                }

                var records = ReadRecords()
                    .Where(item => !string.Equals(item.AccountId, accountId, StringComparison.Ordinal)
                        && IsValid(item, now) && item.ExpiresAt > now)
                    .Prepend(record)
                    .Take(MaximumAccounts)
                    .ToArray();
                var payload = JsonSerializer.SerializeToUtf8Bytes(records, JsonOptions);
                if (payload.Length > MaximumFileBytes)
                {
                    return false;
                }

                var directory = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(directory);
                temporary = Path.Combine(directory, $".subscription-web.{Guid.NewGuid():N}.tmp");
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.Write(payload);
                    output.Flush(true);
                }
                File.Move(temporary, path, true);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return false;
            }
            finally
            {
                if (temporary is not null)
                {
                    try
                    {
                        File.Delete(temporary);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
    }

    internal bool Clear()
    {
        lock (sync)
        {
            try
            {
                if (HasReparsePointAncestor())
                {
                    return false;
                }
                File.Delete(path);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return false;
            }
        }
    }

    private IReadOnlyList<BrowserSubscriptionRecord> ReadRecords()
    {
        try
        {
            if (!File.Exists(path) || HasReparsePointAncestor())
            {
                return [];
            }
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (input.Length is <= 0 or > MaximumFileBytes)
            {
                return [];
            }
            var payload = new byte[(int)input.Length];
            input.ReadExactly(payload);
            var records = JsonSerializer.Deserialize<BrowserSubscriptionRecord[]>(payload, JsonOptions);
            if (records is null || records.Length > MaximumAccounts
                || records.Any(record => record is null)
                || records.Select(record => record.AccountId).Distinct(StringComparer.Ordinal).Count() != records.Length)
            {
                return [];
            }
            return records;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return [];
        }
    }

    private bool HasReparsePointAncestor()
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            return true;
        }
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsValid(BrowserSubscriptionRecord record, DateTimeOffset now) =>
        IsValidAccountId(record.AccountId)
        && record.CheckedAt.Year is >= 2020 and <= 2100
        && record.CheckedAt <= now.AddMinutes(5)
        && record.ExpiresAt > record.CheckedAt
        && record.ExpiresAt <= record.CheckedAt.AddYears(10);

    private static bool IsValidAccountId(string? accountId) =>
        !string.IsNullOrWhiteSpace(accountId)
        && accountId.Length <= 256
        && accountId.AsSpan().Trim().Length == accountId.Length
        && !accountId.Any(char.IsControl);

    private sealed record BrowserSubscriptionRecord(string AccountId, DateTimeOffset ExpiresAt, DateTimeOffset CheckedAt);
}
