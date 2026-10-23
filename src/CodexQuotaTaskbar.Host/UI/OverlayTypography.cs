using WpfFontFamily = System.Windows.Media.FontFamily;
using System.IO.Packaging;

namespace CodexQuotaTaskbar.Host.UI;

public static class OverlayTypography
{
    public const string TextFamilyBaseUri = "pack://application:,,,/CodexQuotaTaskbar;component/Assets/Fonts/";
    public const string TextFamilyName = "./#Noto Sans SC";

    public static WpfFontFamily Text { get; } = CreateTextFamily();
    public static WpfFontFamily Display => Text;
    public static WpfFontFamily Number => Text;
    // The approved lighter menu/detail design uses Windows UI faces; the island stays unchanged.
    public static WpfFontFamily Interface { get; } = new("Segoe UI, Microsoft YaHei UI");

    // Only short, two-line headings are balanced. Body copy and one-line task names
    // keep their normal wrapping/trimming, and Latin words are never split.
    internal static string BalanceHeading(string text, double availableWidth, Func<string, double> measure)
    {
        if (availableWidth <= 0 || text.Length > 120 || text.Contains('\n')
            || measure(text) <= availableWidth || measure(text) > availableWidth * 2) return text;
        var best = text;
        var score = double.PositiveInfinity;
        foreach (var index in System.Globalization.StringInfo.ParseCombiningCharacters(text).Skip(1))
        {
            var left = text[..index].TrimEnd();
            var right = text[index..].TrimStart();
            if (left.Length == 0 || right.Length == 0 || "，。！？、：；）】》,.!?:;)]".Contains(right[0])
                || "（【《([".Contains(left[^1])) continue;
            if (char.IsAsciiLetterOrDigit(text[index - 1]) && char.IsAsciiLetterOrDigit(text[index])) continue;
            var a = measure(left);
            var b = measure(right);
            if (a > availableWidth || b > availableWidth || Math.Abs(a - b) >= score) continue;
            score = Math.Abs(a - b);
            best = left + "\n" + right;
        }
        return best;
    }

    private static WpfFontFamily CreateTextFamily()
    {
        _ = PackUriHelper.UriSchemePack;
        return new WpfFontFamily(new Uri(TextFamilyBaseUri, UriKind.Absolute), TextFamilyName);
    }
}
