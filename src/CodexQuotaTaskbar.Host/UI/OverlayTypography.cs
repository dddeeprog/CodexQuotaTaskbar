using WpfFontFamily = System.Windows.Media.FontFamily;
using System.IO.Packaging;

namespace CodexQuotaTaskbar.Host.UI;

public static class OverlayTypography
{
    public const string TextFamilyBaseUri = "pack://application:,,,/CodexQuotaTaskbar;component/Assets/Fonts/";
    public const string TextFamilyName = "./#Noto Sans SC";
    public const string NumberFamilyName = "Segoe UI Variable Display, Segoe UI";

    public static WpfFontFamily Text { get; } = CreateTextFamily();
    public static WpfFontFamily Display => Text;
    public static WpfFontFamily Number { get; } = new(NumberFamilyName);

    private static WpfFontFamily CreateTextFamily()
    {
        _ = PackUriHelper.UriSchemePack;
        return new WpfFontFamily(new Uri(TextFamilyBaseUri, UriKind.Absolute), TextFamilyName);
    }
}
