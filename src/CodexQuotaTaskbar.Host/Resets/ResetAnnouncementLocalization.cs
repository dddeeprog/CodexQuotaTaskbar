using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexQuotaTaskbar.Host.Resets;

// The website's localized feed only supplies display text; v1 remains the event authority.
internal static class ResetAnnouncementLocalization
{
    private static readonly Regex Links = new("https?://[^\\s<>\"'，。！？；：、（）【】]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static IReadOnlyList<ResetAnnouncement> Apply(
        ReadOnlyMemory<byte> utf8Json, IReadOnlyList<ResetAnnouncement> announcements)
    {
        using var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions { MaxDepth = 12 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Localized public feed events are missing.");
        }

        var unique = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in events.EnumerateArray())
        {
            var id = ReadString(item, "tweet_id");
            if (id is null || id.Length is < 1 or > 64) continue;
            if (!unique.TryAdd(id, item)) duplicates.Add(id);
        }

        return Array.AsReadOnly(announcements.Select(announcement =>
        {
            if (duplicates.Contains(announcement.Id) || !unique.TryGetValue(announcement.Id, out var item))
                return announcement;

            var timestamp = ReadString(item, "announced_at");
            var expectedKind = announcement.Kind == ResetKind.Banked ? "banked" : "regular";
            var original = ReadString(item, "text");
            var displayText = ReadString(item, "display_text");
            if (timestamp is null || !HasTimeZone(timestamp) ||
                !item.GetProperty("announced_at").TryGetDateTimeOffset(out var announcedAt) ||
                announcedAt != announcement.AnnouncedAt || ReadString(item, "reset_type") != expectedKind ||
                original is null || CodexResetFeedClient.Preview(original) != announcement.Text ||
                string.IsNullOrWhiteSpace(displayText))
            {
                return announcement;
            }

            var chinese = WithoutLinks(displayText);
            return chinese.EnumerateRunes().Any(IsHan)
                ? announcement with { ChineseText = chinese }
                : announcement;
        }).ToArray());
    }

    internal static string WithoutLinks(string text)
    {
        // Remove invisible formatting before matching, so a zero-width character cannot hide a URL.
        var visible = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format ||
                (Rune.IsControl(rune) && !Rune.IsWhiteSpace(rune))) continue;
            visible.Append(rune.ToString());
        }
        return CodexResetFeedClient.Preview(Links.Replace(visible.ToString(), " "));
    }

    private static string? ReadString(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String) return null;
        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            // JSON can parse successfully while a string contains an unpaired UTF-16 escape.
            return null;
        }
    }

    private static bool HasTimeZone(string timestamp) => timestamp.EndsWith('Z') ||
        (timestamp.Length >= 6 && timestamp[^6] is '+' or '-' && timestamp[^3] == ':');

    private static bool IsHan(Rune rune) => rune.Value is
        >= 0x3400 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF;
}
