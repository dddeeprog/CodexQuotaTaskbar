using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CodexQuotaTaskbar.Host.Shortcuts;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Image = System.Windows.Controls.Image;
using MenuItem = System.Windows.Controls.MenuItem;

namespace CodexQuotaTaskbar.Host.UI;

public partial class QuotaPopoverWindow
{
    private readonly Dictionary<string, (WebShortcut Shortcut, Button Button, Border Icon)> shortcutViews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WebShortcutIcon> shortcutImages = new(StringComparer.Ordinal);
    private bool shortcutEditorOpen;
    private int openShortcutMenus;

    internal event Action<WebShortcutAddRequest>? AddWebShortcutRequested;
    internal event Action<string, string>? ChangeWebShortcutIconRequested;
    internal event Action<string>? RestoreAutomaticWebShortcutIconRequested;
    internal event Action<string>? DeleteWebShortcutRequested;
    internal event Action<Uri>? OpenWebShortcutRequested;
    private bool HasOpenShortcutInteraction => shortcutEditorOpen || openShortcutMenus > 0;

    internal void ApplyShortcuts(WebShortcutsSnapshot snapshot)
    {
        ShortcutsStatusText.Text = snapshot.IsLoading ? "正在读取网站…"
            : string.IsNullOrEmpty(snapshot.ErrorMessage) ? string.Empty
            : snapshot.Items.Count > 0 ? "网站列表未能更新" : "网站列表暂不可用";
        ShortcutsStatusText.ToolTip = snapshot.ErrorMessage;
        ShortcutsStatusText.Visibility = string.IsNullOrEmpty(ShortcutsStatusText.Text) ? Visibility.Collapsed : Visibility.Visible;
        foreach (var previous in shortcutViews)
        {
            var replacement = snapshot.Items.FirstOrDefault(item => item.Id == previous.Key);
            if (replacement is null || replacement.TargetUri is not { IsAbsoluteUri: true }
                || replacement.TargetUri.AbsoluteUri != previous.Value.Shortcut.TargetUri.AbsoluteUri
                || replacement.ManualIconKey != previous.Value.Shortcut.ManualIconKey) shortcutImages.Remove(previous.Key);
        }
        ShortcutsGrid.Children.Clear();
        shortcutViews.Clear();
        foreach (var shortcut in snapshot.Items.Take(48))
        {
            if (string.IsNullOrEmpty(shortcut.Id) || shortcutViews.ContainsKey(shortcut.Id)
                || shortcut.TargetUri is not { IsAbsoluteUri: true }
                || !WebShortcut.TryNormalizeUrl(shortcut.TargetUri.AbsoluteUri, out var normalized, out _)) continue;
            var icon = new Border { Width = 32, Height = 32, IsHitTestVisible = false };
            var button = new Button
            {
                Style = (Style)FindResource("ShortcutButton"), Content = icon, Tag = shortcut.Id,
                ToolTip = ShortcutTooltip(shortcut, null),
            };
            AutomationProperties.SetName(button, $"打开 {CleanShortcutName(shortcut.Name, normalized!)}");
            button.Click += (_, _) => OpenWebShortcutRequested?.Invoke(normalized!);
            var remove = new MenuItem { FontWeight = FontWeights.SemiBold, Header = "删除", Tag = shortcut.Id };
            remove.Click += (_, _) => DeleteWebShortcutRequested?.Invoke(shortcut.Id);
            var changeIcon = new MenuItem { FontWeight = FontWeights.SemiBold, Header = "更换图标…", Tag = shortcut.Id };
            changeIcon.Click += (_, _) => ChooseShortcutIcon(shortcut.Id);
            var restore = new MenuItem
            {
                FontWeight = FontWeights.SemiBold, Header = "恢复自动图标", Tag = shortcut.Id, IsEnabled = !string.IsNullOrEmpty(shortcut.ManualIconKey),
            };
            restore.Click += (_, _) => RestoreAutomaticIcon(shortcut.Id);
            var menu = new ContextMenu { FontFamily = OverlayTypography.Interface, FontSize = 12, FontWeight = FontWeights.SemiBold };
            menu.Items.Add(changeIcon);
            menu.Items.Add(restore);
            menu.Items.Add(new Separator());
            menu.Items.Add(remove);
            menu.Opened += (_, _) => openShortcutMenus++;
            menu.Closed += (_, _) => openShortcutMenus = Math.Max(0, openShortcutMenus - 1);
            button.ContextMenu = menu;
            shortcutViews.Add(shortcut.Id, (shortcut, button, icon));
            ShortcutsGrid.Children.Add(button);
            SetShortcutImage(shortcut.Id, shortcutImages.GetValueOrDefault(shortcut.Id));
        }
        foreach (var stale in shortcutImages.Keys.Where(id => !shortcutViews.ContainsKey(id)).ToArray()) shortcutImages.Remove(stale);
        AddShortcutButton.IsEnabled = shortcutViews.Count < 48 && !snapshot.IsLoading;
        AddShortcutButton.ToolTip = shortcutViews.Count >= 48 ? "最多可添加 48 个网站" : "添加网站";
        ShortcutsGrid.Children.Add(AddShortcutButton);
        ApplySystemAppearance();
    }

    internal void ApplyShortcutIcon(string id, WebShortcutIcon icon)
    {
        if (!shortcutViews.ContainsKey(id)) return;
        shortcutImages[id] = icon;
        SetShortcutImage(id, icon);
    }

    private void SetShortcutImage(string id, WebShortcutIcon? icon)
    {
        var view = shortcutViews[id];
        var bitmap = icon?.Image is { IsFrozen: true } frozen ? frozen : null;
        var manualImage = !string.IsNullOrEmpty(view.Shortcut.ManualIconKey) && bitmap is not null;
        var size = manualImage ? 52d : 32d;
        view.Icon.Width = size;
        view.Icon.Height = size;
        view.Icon.CornerRadius = manualImage ? new CornerRadius(13) : default;
        view.Icon.Clip = manualImage ? new RectangleGeometry(new Rect(0, 0, size, size), 13, 13) : null;
        view.Icon.Background = manualImage ? new ImageBrush(bitmap)
        {
            Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center,
        } : null;
        view.Icon.Child = manualImage ? null : bitmap is { } image
            ? new Image
            {
                Source = image, Stretch = Stretch.Uniform, Width = size, Height = size,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center,
            }
            : new TextBlock
            {
                Text = PlaceholderLetter(view.Shortcut.TargetUri), FontFamily = OverlayTypography.Interface,
                FontSize = 22, FontWeight = FontWeights.Medium,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            };
        view.Button.ToolTip = ShortcutTooltip(view.Shortcut, icon?.Name);
    }

    private static string ShortcutTooltip(WebShortcut shortcut, string? fetchedName) =>
        $"{CleanShortcutName(fetchedName ?? shortcut.Name, shortcut.TargetUri)}\n{shortcut.TargetUri.IdnHost}"
        + (string.IsNullOrEmpty(shortcut.ManualIconKey) ? string.Empty : "\n本地图标");

    private static string CleanShortcutName(string? name, Uri uri)
    {
        var text = string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        text = new string(text.Where(c => !char.IsControl(c)).ToArray());
        if (text.Length == 0) return uri.IdnHost;
        return text.Length > 80 ? text[..80] + "…" : text;
    }

    internal static string PlaceholderLetter(Uri uri)
    {
        var host = uri.IdnHost;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];
        return host.Length > 0 ? host[..1].ToUpperInvariant() : "•";
    }

    private void ApplyShortcutAppearance(bool opaque)
    {
        foreach (var button in ShortcutsGrid.Children.OfType<Button>())
        {
            button.Foreground = opaque ? System.Windows.SystemColors.WindowTextBrush : Brushes.White;
            button.Background = opaque ? System.Windows.SystemColors.ControlBrush
                : ReferenceEquals(button, AddShortcutButton) ? Brushes.Transparent
                : new SolidColorBrush(System.Windows.Media.Color.FromArgb(20, 255, 255, 255));
            button.BorderBrush = opaque ? System.Windows.SystemColors.ActiveBorderBrush
                : new SolidColorBrush(System.Windows.Media.Color.FromArgb(ReferenceEquals(button, AddShortcutButton) ? (byte)56 : (byte)32, 255, 255, 255));
        }
    }

    private void OpenAddShortcutDialog()
    {
        if (shortcutEditorOpen) return;
        shortcutEditorOpen = true;
        try
        {
            var dialog = new WebShortcutAddWindow { Owner = this };
            if (dialog.ShowDialog() == true && dialog.AcceptedRequest is { } request)
                AddWebShortcutRequested?.Invoke(request);
        }
        finally { shortcutEditorOpen = false; }
    }

    private void ChooseShortcutIcon(string id)
    {
        var wasEditing = shortcutEditorOpen;
        shortcutEditorOpen = true;
        try { ApplySelectedShortcutIcon(id, WebShortcutIconPicker.Choose(this)); }
        finally { shortcutEditorOpen = wasEditing; }
    }

    internal bool ApplySelectedShortcutIcon(string id, string? selectedPath)
    {
        if (!shortcutViews.ContainsKey(id) || !WebShortcutIconPicker.TryNormalizeSelection(selectedPath, out var path)) return false;
        ChangeWebShortcutIconRequested?.Invoke(id, path!);
        return true;
    }

    internal bool RestoreAutomaticIcon(string id)
    {
        if (!shortcutViews.TryGetValue(id, out var view) || string.IsNullOrEmpty(view.Shortcut.ManualIconKey)) return false;
        RestoreAutomaticWebShortcutIconRequested?.Invoke(id);
        return true;
    }
}
