using System.IO;
using System.Windows;

namespace CodexQuotaTaskbar.Host.UI;

internal static class WebShortcutIconPicker
{
    internal static string? Choose(Window owner)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择网站图标", Filter = "图标图片 (*.png;*.jpg;*.jpeg;*.ico)|*.png;*.jpg;*.jpeg;*.ico",
            Multiselect = false, CheckFileExists = true, CheckPathExists = true, ValidateNames = true,
            AddToRecent = false,
        };
        return dialog.ShowDialog(owner) == true && TryNormalizeSelection(dialog.FileName, out var path) ? path : null;
    }

    // This checks a user's selection only. The service separately bounds and decodes the bytes.
    internal static bool TryNormalizeSelection(string? selected, out string? path)
    {
        path = null;
        if (string.IsNullOrWhiteSpace(selected) || selected.Any(char.IsControl) || !Path.IsPathFullyQualified(selected)) return false;
        try
        {
            var extension = Path.GetExtension(selected);
            if (!new[] { ".png", ".jpg", ".jpeg", ".ico" }.Contains(extension, StringComparer.OrdinalIgnoreCase)) return false;
            path = Path.GetFullPath(selected);
            return true;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
