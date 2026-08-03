using WpfFontFamily = System.Windows.Media.FontFamily;

namespace CodexQuotaTaskbar.Host.UI;

public static class OverlayTypography
{
    public const string TextFamilyName = "Noto Sans SC, Microsoft YaHei UI";
    public const string NumberFamilyName = "Segoe UI Variable Display, Segoe UI";

    public static WpfFontFamily Text { get; } = new(TextFamilyName);
    public static WpfFontFamily Display { get; } = new(TextFamilyName);
    public static WpfFontFamily Number { get; } = new(NumberFamilyName);
}
