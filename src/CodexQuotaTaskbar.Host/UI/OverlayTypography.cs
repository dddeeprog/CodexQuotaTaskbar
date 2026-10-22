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

    private static WpfFontFamily CreateTextFamily()
    {
        _ = PackUriHelper.UriSchemePack;
        return new WpfFontFamily(new Uri(TextFamilyBaseUri, UriKind.Absolute), TextFamilyName);
    }
}
