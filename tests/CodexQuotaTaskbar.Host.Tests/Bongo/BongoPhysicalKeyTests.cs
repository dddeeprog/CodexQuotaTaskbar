using CodexQuotaTaskbar.Host.Bongo;

namespace CodexQuotaTaskbar.Host.Tests.Bongo;

public sealed class BongoPhysicalKeyTests
{
    [Theory]
    [InlineData(0x1e, 0x04)] [InlineData(0x30, 0x05)] [InlineData(0x2e, 0x06)]
    [InlineData(0x20, 0x07)] [InlineData(0x12, 0x08)] [InlineData(0x21, 0x09)]
    [InlineData(0x22, 0x0a)] [InlineData(0x23, 0x0b)] [InlineData(0x17, 0x0c)]
    [InlineData(0x24, 0x0d)] [InlineData(0x25, 0x0e)] [InlineData(0x26, 0x0f)]
    [InlineData(0x32, 0x10)] [InlineData(0x31, 0x11)] [InlineData(0x18, 0x12)]
    [InlineData(0x19, 0x13)] [InlineData(0x10, 0x14)] [InlineData(0x13, 0x15)]
    [InlineData(0x1f, 0x16)] [InlineData(0x14, 0x17)] [InlineData(0x16, 0x18)]
    [InlineData(0x2f, 0x19)] [InlineData(0x11, 0x1a)] [InlineData(0x2d, 0x1b)]
    [InlineData(0x15, 0x1c)] [InlineData(0x2c, 0x1d)]
    public void Alphabet_positions_map_to_keyboard_page_usages(int scan, int usage)
    {
        Assert.True(BongoPhysicalKey.TryGetHid((ushort)scan, out var hid));
        Assert.Equal((ushort)usage, hid);
    }

    [Fact]
    public void Number_row_and_function_row_keep_physical_order()
    {
        for (ushort offset = 0; offset < 10; offset++)
        {
            Assert.True(BongoPhysicalKey.TryGetHid((ushort)(0x02 + offset), out var number));
            Assert.Equal((ushort)(0x1e + offset), number);
            Assert.True(BongoPhysicalKey.TryGetHid((ushort)(0x3b + offset), out var function));
            Assert.Equal((ushort)(0x3a + offset), function);
        }
        Assert.True(BongoPhysicalKey.TryGetHid(0x57, out var f11));
        Assert.True(BongoPhysicalKey.TryGetHid(0x58, out var f12));
        Assert.Equal((ushort)0x44, f11); Assert.Equal((ushort)0x45, f12);
    }

    [Theory]
    [InlineData(0x39, 0x2c)] [InlineData(0x01, 0x29)] [InlineData(0x0e, 0x2a)]
    [InlineData(0x0f, 0x2b)] [InlineData(0x3a, 0x39)] [InlineData(0x29, 0x35)]
    [InlineData(0x35, 0x38)] [InlineData(0x153, 0x4c)] [InlineData(0x56, 0x64)]
    [InlineData(0x0c, 0x2d)] [InlineData(0x0d, 0x2e)] [InlineData(0x1a, 0x2f)]
    [InlineData(0x1b, 0x30)] [InlineData(0x2b, 0x31)] [InlineData(0x27, 0x33)]
    [InlineData(0x28, 0x34)] [InlineData(0x33, 0x36)] [InlineData(0x34, 0x37)]
    public void Control_and_punctuation_positions_do_not_require_text_translation(int scan, int usage)
    {
        Assert.True(BongoPhysicalKey.TryGetHid((ushort)scan, out var hid));
        Assert.Equal((ushort)usage, hid);
    }

    [Theory]
    [InlineData(0x1d, 0xe0)] [InlineData(0x2a, 0xe1)] [InlineData(0x38, 0xe2)]
    [InlineData(0x15b, 0xe3)] [InlineData(0x11d, 0xe4)] [InlineData(0x36, 0xe5)]
    [InlineData(0x138, 0xe6)] [InlineData(0x15c, 0xe7)] [InlineData(0x15d, 0x65)]
    public void Both_sides_of_each_modifier_keep_their_identity(int scan, int usage)
    {
        Assert.True(BongoPhysicalKey.TryGetHid((ushort)scan, out var hid));
        Assert.Equal((ushort)usage, hid);
    }

    [Theory]
    [InlineData(0x1c, 0x11c, 0x28, 0x58)]
    [InlineData(0x35, 0x135, 0x38, 0x54)]
    [InlineData(0x37, 0x137, 0x55, 0x46)]
    [InlineData(0x47, 0x147, 0x5f, 0x4a)]
    [InlineData(0x48, 0x148, 0x60, 0x52)]
    [InlineData(0x49, 0x149, 0x61, 0x4b)]
    [InlineData(0x4b, 0x14b, 0x5c, 0x50)]
    [InlineData(0x4d, 0x14d, 0x5e, 0x4f)]
    [InlineData(0x4f, 0x14f, 0x59, 0x4d)]
    [InlineData(0x50, 0x150, 0x5a, 0x51)]
    [InlineData(0x51, 0x151, 0x5b, 0x4e)]
    [InlineData(0x52, 0x152, 0x62, 0x49)]
    [InlineData(0x53, 0x153, 0x63, 0x4c)]
    public void E0_keys_remain_distinct_from_their_nonextended_make_code(int plain, int extended, int plainUsage, int extendedUsage)
    {
        Assert.True(BongoPhysicalKey.TryGetHid((ushort)plain, out var plainHid));
        Assert.True(BongoPhysicalKey.TryGetHid((ushort)extended, out var extendedHid));
        Assert.Equal((ushort)plainUsage, plainHid); Assert.Equal((ushort)extendedUsage, extendedHid);
        Assert.NotEqual(plainHid, extendedHid);
    }

    [Fact]
    public void Supported_physical_positions_have_unique_nonzero_usages()
    {
        var usages = new List<ushort>();
        for (var scan = 0; scan <= ushort.MaxValue; scan++)
        {
            if (BongoPhysicalKey.TryGetHid((ushort)scan, out var hid)) usages.Add(hid);
            else Assert.Equal((ushort)0, hid);
        }
        Assert.Equal(104, usages.Count);
        Assert.Equal(usages.Count, usages.Distinct().Count());
        Assert.DoesNotContain((ushort)0x48, usages); // E1/Pause is intentionally unsupported.
    }

    [Theory]
    [InlineData(0)] [InlineData(0x54)] [InlineData(0x55)] [InlineData(0x80)]
    [InlineData(0xff)] [InlineData(0x100)] [InlineData(0x156)] [InlineData(0x180)]
    [InlineData(0x1ff)] [InlineData(0x21d)] [InlineData(0xe11d)] [InlineData(0xffff)]
    public void Unknown_or_nonrepresentable_sequences_are_ignored(int scan)
    {
        Assert.False(BongoPhysicalKey.TryGetHid((ushort)scan, out var hid));
        Assert.Equal((ushort)0, hid);
    }
}
