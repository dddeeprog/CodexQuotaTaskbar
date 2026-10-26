using System.IO;
using CodexQuotaTaskbar.Host.Lifetime;

namespace CodexQuotaTaskbar.Host.Tests.Lifetime;

public sealed class DotInlineWireTests
{
    [Fact]
    public async Task Bounded_reader_preserves_multiple_lines_chunks_crlf_and_final_line()
    {
        using var input = new StringReader(new string('x', 8192) + "\r\nsecond\nlast");
        var reader = new DotInlineLineReader(input, 9000);
        Assert.Equal(new string('x', 8192), await reader.ReadLineAsync(default));
        Assert.Equal("second", await reader.ReadLineAsync(default));
        Assert.Equal("last", await reader.ReadLineAsync(default));
        Assert.Null(await reader.ReadLineAsync(default));
    }

    [Fact]
    public async Task Oversized_line_stops_before_unbounded_allocation()
    {
        using var input = new StringReader(new string('x', 100));
        var reader = new DotInlineLineReader(input, 64);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadLineAsync(default));
    }

    [Fact]
    public void Action_validation_does_not_allow_unbounded_text_unknown_commands_or_missing_request_identity()
    {
        Assert.True(DotInlineWire.ValidAction(new("draft", new string('文', 8000), 7)));
        Assert.False(DotInlineWire.ValidAction(new("draft", new string('文', 8001), 7)));
        Assert.False(DotInlineWire.ValidAction(new("send", "synthetic", 7)));
        Assert.True(DotInlineWire.ValidAction(new("send", "synthetic", 7, RequestId: Guid.NewGuid().ToString("N"))));
        Assert.False(DotInlineWire.ValidAction(new("execute", "anything")));
        Assert.False(DotInlineWire.ValidAction(new("draft", "synthetic", -1)));
        Assert.False(DotInlineWire.ValidAction(new("read", MessageId: new string('i', 257))));
        Assert.Null(DotInlineWire.Deserialize<DotInlineAction>("{\"Kind\":\"page\",\"Unexpected\":true}"));
    }

    [Fact]
    public void Utf8_json_record_roundtrip_preserves_only_the_explicit_state_contract()
    {
        var state = new DotInlineState(Guid.NewGuid().ToString("N"), 1, 2, "合成草稿", [], 0, 0, null, 0,
            true, false, false, false, true, false, "ready");
        var json = DotInlineWire.Serialize(state);
        var restored = DotInlineWire.Deserialize<DotInlineState>(json)!;
        Assert.True(DotInlineWire.ValidState(restored)); Assert.Equal("合成草稿", restored.DraftText);
        Assert.DoesNotContain("CanSubmit", json, StringComparison.Ordinal);
    }
}
