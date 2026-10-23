using CodexQuotaTaskbar.Host.UI;
using System.Windows;
using System.Windows.Media;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class OverlayTypographyTests
{
    [Fact]
    public void Short_headings_balance_without_splitting_words_or_changing_body_copy()
    {
        static double Measure(string value) => value.Length;
        Assert.Equal("额度更新", OverlayTypography.BalanceHeading("额度更新", 8, Measure));
        Assert.Equal("这是一个标\n题换行测试", OverlayTypography.BalanceHeading("这是一个标题换行测试", 6, Measure));
        var latin = OverlayTypography.BalanceHeading("Codex quota reset", 11, Measure);
        Assert.Contains("Codex", latin);
        Assert.DoesNotContain("Co\n", latin);
        Assert.Equal("很长的公告正文应该继续使用自然换行处理", OverlayTypography.BalanceHeading("很长的公告正文应该继续使用自然换行处理", 5, Measure));
    }
    [Fact]
    public void Uses_the_embedded_cjk_font_for_text_and_headings()
    {
        Assert.Contains("Assets/Fonts/", OverlayTypography.TextFamilyBaseUri);
        Assert.Equal("./#Noto Sans SC", OverlayTypography.TextFamilyName);
        Assert.Contains("Noto Sans SC", OverlayTypography.Text.Source);
        Assert.Same(OverlayTypography.Text, OverlayTypography.Display);
    }

    [Fact]
    public void Embedded_text_font_resolves_regular_and_medium_faces_from_application_resources()
    {
        _ = Application.Current ?? new Application();
        var typefaces = OverlayTypography.Text.GetTypefaces().ToArray();
        var glyphs = typefaces
            .Select(typeface => typeface.TryGetGlyphTypeface(out var glyph) ? glyph : null)
            .Where(glyph => glyph is not null)
            .Cast<GlyphTypeface>()
            .ToArray();

        Assert.Contains(glyphs, glyph =>
            glyph.FamilyNames.Values.Contains("Noto Sans SC") &&
            glyph.FaceNames.Values.Contains("Regular") &&
            glyph.FontUri.Scheme == "pack");
        Assert.Contains(glyphs, glyph =>
            glyph.FamilyNames.Values.Contains("Noto Sans SC") &&
            glyph.FaceNames.Values.Contains("Medium") &&
            glyph.Weight == FontWeights.Medium &&
            glyph.FontUri.Scheme == "pack");
    }

    [Fact]
    public void Numeric_readouts_use_the_same_embedded_font_as_text()
    {
        Assert.Same(OverlayTypography.Text, OverlayTypography.Number);
        Assert.Equal("./#Noto Sans SC", OverlayTypography.Number.Source);
    }
}
