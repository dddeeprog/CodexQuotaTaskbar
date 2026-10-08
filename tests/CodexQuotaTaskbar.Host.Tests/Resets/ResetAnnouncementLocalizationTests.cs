using System.Text;
using System.Text.Json;
using CodexQuotaTaskbar.Host.Resets;

namespace CodexQuotaTaskbar.Host.Tests.Resets;

public sealed class ResetAnnouncementLocalizationTests
{
    private const string Original = "A reset was announced. https://t.co/example";
    private static readonly ResetAnnouncement Announcement = new("123", ResetKind.Banked,
        DateTimeOffset.Parse("2026-10-08T03:44:55Z"), Original,
        new Uri("https://x.com/thsottiaux/status/123"), false);

    [Fact]
    public void Applies_matching_chinese_text_without_changing_source_identity_or_original()
    {
        var result = Apply(Event(display: "已发放备用重置。 https://t.co/example"));

        Assert.Equal("已发放备用重置。", result.ChineseText);
        Assert.Equal(Announcement, result with { ChineseText = null });
        Assert.Null(Announcement.ChineseText);
    }

    [Fact]
    public void Equivalent_time_zone_offset_matches_the_same_instant()
    {
        Assert.Equal("已重置", Apply(Event(at: "2026-10-08T11:44:55+08:00")).ChineseText);
    }

    [Theory]
    [InlineData("999", "2026-10-08T03:44:55Z", "banked", Original)]
    [InlineData("123", "2026-10-08T03:44:56Z", "banked", Original)]
    [InlineData("123", "2026-10-08T03:44:55", "banked", Original)]
    [InlineData("123", "not a date", "banked", Original)]
    [InlineData("123", "2026-10-08T03:44:55Z", "regular", Original)]
    [InlineData("123", "2026-10-08T03:44:55Z", "predicted", Original)]
    [InlineData("123", "2026-10-08T03:44:55Z", "banked", "Different original")]
    public void Does_not_apply_mismatched_identity_time_kind_or_original(string id, string at, string kind, string original)
    {
        Assert.Null(Apply(Event(id: id, at: at, kind: kind, original: original)).ChineseText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \t")]
    [InlineData("Reset has landed across all accounts.")]
    [InlineData("https://x.com/中文")]
    public void Missing_empty_or_untranslated_display_text_is_not_labelled_chinese(string? display)
    {
        Assert.Null(Apply(Event(display: display)).ChineseText);
    }

    [Fact]
    public void Missing_display_field_and_malformed_individual_items_are_skipped()
    {
        var result = Apply("null", "42", "{}", "{\"tweet_id\":false}",
            "{\"tweet_id\":\"999\",\"display_text\":\"无关\"}", Event());
        Assert.Equal("已重置", result.ChineseText);
        Assert.Null(Apply("{\"tweet_id\":\"123\",\"display_text\":false}").ChineseText);
        Assert.Null(Apply("{\"tweet_id\":\"123\"}").ChineseText);
    }

    [Fact]
    public void Duplicate_ids_are_ambiguous_even_if_one_item_is_invalid_or_the_text_agrees()
    {
        Assert.Null(Apply(Event(), Event()).ChineseText);
        Assert.Null(Apply(Event(), Event(display: "另一条译文")).ChineseText);
        Assert.Null(Apply(Event(), "{\"tweet_id\":\"123\"}").ChineseText);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"events\":null}")]
    [InlineData("{\"events\":{}}")]
    public void Malformed_top_level_structure_is_rejected(string json)
    {
        Assert.Throws<InvalidDataException>(() => Parse(json));
    }

    [Fact]
    public void Invalid_json_and_excessive_nesting_are_rejected()
    {
        Assert.ThrowsAny<JsonException>(() => Parse("not json"));
        Assert.ThrowsAny<JsonException>(() => Parse("{\"events\":[" + new string('[', 13) + "0" + new string(']', 13) + "]}"));
    }

    [Theory]
    [InlineData("\\uD800")]
    [InlineData("\\uDC00")]
    public void Unpaired_utf16_escapes_in_individual_fields_are_skipped_without_throwing(string escape)
    {
        foreach (var field in new[] { "tweet_id", "announced_at", "reset_type", "text", "display_text" })
        {
            using var document = JsonDocument.Parse(Event());
            var properties = document.RootElement.EnumerateObject().Select(property =>
                JsonSerializer.Serialize(property.Name) + ":" +
                (property.Name == field ? "\"" + escape + "\"" : property.Value.GetRawText()));
            Assert.Null(Apply("{" + string.Join(',', properties) + "}").ChineseText);
        }

        Assert.Equal("已重置", Apply("{\"tweet_id\":\"" + escape + "\"}", Event()).ChineseText);
    }

    [Fact]
    public void Html_is_returned_as_literal_text_not_parsed_or_decoded()
    {
        const string text = "<script>中文提示</script> &lt;button&gt;";
        Assert.Equal(text, Apply(Event(display: text)).ChineseText);
    }

    [Theory]
    [InlineData("中文 https://t.co/abc 公告 https://x.com/user/status/1", "中文 公告")]
    [InlineData("HTTP://X.COM/user/status/1 已重置", "已重置")]
    [InlineData("已重置 http://example.com/reset", "已重置")]
    [InlineData("前文 https://t.co/abc。后文", "前文 。后文")]
    [InlineData("已重置 https://x.\u200Bcom/user/status/1", "已重置")]
    [InlineData("已重置 https://t.\u0001co/abc", "已重置")]
    public void Removes_http_links_including_x_short_links_and_invisible_controls(string text, string expected)
    {
        Assert.Equal(expected, ResetAnnouncementLocalization.WithoutLinks(text));
    }

    [Fact]
    public void Chinese_preview_normalizes_controls_and_caps_length_without_broken_surrogates()
    {
        Assert.Equal("已 重置 完成", Apply(Event(display: "\u202E已\n\t重置\u0001 完成")).ChineseText);
        var text = new string('中', 238) + "🦊继续";
        var result = Apply(Event(display: text)).ChineseText!;
        Assert.Equal(new string('中', 238) + "…", result);
        Assert.True(result.Length <= 240);
        Assert.DoesNotContain('\uFFFD', result);
    }

    [Fact]
    public void Strips_links_before_truncation_and_recognizes_supplementary_han()
    {
        Assert.Equal("已重置", Apply(Event(display: "https://t.co/" + new string('a', 400) + " 已重置")).ChineseText);
        Assert.Equal("𠀀", Apply(Event(display: "𠀀")).ChineseText);
    }

    [Fact]
    public void Empty_localized_feed_preserves_existing_translation_and_event_order()
    {
        var second = Announcement with { Id = "456", ChineseText = "已缓存译文" };
        var result = ResetAnnouncementLocalization.Apply(Encoding.UTF8.GetBytes("{\"events\":[]}"), [Announcement, second]);
        Assert.Equal([Announcement, second], result);
    }

    private static ResetAnnouncement Apply(params string[] events) =>
        Assert.Single(Parse("{\"events\":[" + string.Join(',', events) + "]}"));

    private static IReadOnlyList<ResetAnnouncement> Parse(string json) =>
        ResetAnnouncementLocalization.Apply(Encoding.UTF8.GetBytes(json), [Announcement]);

    private static string Event(string id = "123", string at = "2026-10-08T03:44:55Z",
        string kind = "banked", string original = Original, string? display = "已重置") =>
        JsonSerializer.Serialize(new { tweet_id = id, announced_at = at, reset_type = kind, text = original, display_text = display });
}
