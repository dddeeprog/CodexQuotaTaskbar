using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.Platform;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace CodexQuotaTaskbar.Host.UI;

// A short, nonactivating notification only. No browser, composer, draft, timer or read acknowledgement lives here.
internal sealed class DotNotificationWindow : Window
{
    private readonly TextBlock messageText, countText;
    private readonly Border shell;
    internal const double NotificationWidthDip = 240;
    internal const double NotificationHeightDip = 80;
    internal event EventHandler? OpenRequested;
    internal event EventHandler? InteractionChanged;
    internal bool IsInteracting => IsMouseOver || IsKeyboardFocusWithin;
    internal double PreferredWidthDip => NotificationWidthDip;
    internal double PreferredHeightDip => NotificationHeightDip;
    internal ScreenRect Bounds { get; private set; }

    internal DotNotificationWindow()
    {
        Title = "Dot 提醒 · 点岛"; Width = NotificationWidthDip; Height = NotificationHeightDip;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowActivated = false; ShowInTaskbar = false; Topmost = true;
        FontFamily = OverlayTypography.Interface; Foreground = Brushes.WhiteSmoke;
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        shell = new Border
        {
            Margin = new Thickness(4), CornerRadius = new CornerRadius(13),
            Background = OverlayGlassMaterial.Surface, BorderBrush = OverlayGlassMaterial.Border,
            BorderThickness = new Thickness(.5), Padding = new Thickness(11, 7, 11, 7),
        };
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(17) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        var header = new DockPanel();
        countText = new TextBlock { FontSize = 10, Foreground = Brush("#C0C0C8"), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(countText, Dock.Right); header.Children.Add(countText);
        header.Children.Add(new TextBlock { Text = "Dot", FontSize = 11, FontWeight = FontWeights.SemiBold });
        content.Children.Add(header);
        messageText = new TextBlock
        {
            FontSize = 12, LineHeight = 18, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 36,
        };
        Grid.SetRow(messageText, 1); content.Children.Add(messageText);
        shell.Child = content; Content = shell;
        shell.Cursor = System.Windows.Input.Cursors.Hand;
        shell.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenRequested?.Invoke(this, EventArgs.Empty); };
        AutomationProperties.SetName(shell, "Dot 新消息提醒，点击在点岛内回复");
        MouseEnter += (_, _) => InteractionChanged?.Invoke(this, EventArgs.Empty);
        MouseLeave += (_, _) => InteractionChanged?.Invoke(this, EventArgs.Empty);
        IsKeyboardFocusWithinChanged += (_, _) => InteractionChanged?.Invoke(this, EventArgs.Empty);
        SourceInitialized += (_, _) => WindowInteropPolicy.AttachNoActivate(this);
    }

    internal void Display(DotInlineMessage message, int unreadCount)
    {
        VerifyAccess(); ArgumentNullException.ThrowIfNull(message);
        // A two-line preview should not turn paragraph breaks into empty space.
        messageText.Text = message.Text.Replace('\r', ' ').Replace('\n', ' ');
        countText.Text = unreadCount > 1 ? $"{unreadCount} 条未读" : "新消息";
        ToolTip = "点击在点岛内查看和回复；提醒收起不会标记已读";
    }

    internal void SetMaterialTransparency(int transparency)
    {
        VerifyAccess();
        var highContrast = SystemParameters.HighContrast;
        shell.Background = highContrast ? System.Windows.SystemColors.WindowBrush : OverlayGlassMaterial.CreateSurfaceBrush(transparency);
        shell.BorderBrush = highContrast ? System.Windows.SystemColors.WindowTextBrush : OverlayGlassMaterial.Border;
        Foreground = highContrast ? System.Windows.SystemColors.WindowTextBrush : Brushes.WhiteSmoke;
        countText.Foreground = highContrast ? System.Windows.SystemColors.WindowTextBrush : Brush("#C0C0C8");
    }

    internal void SetPosition(ScreenRect bounds)
    {
        VerifyAccess(); Bounds = bounds;
        if (IsVisible) WindowInteropPolicy.Position(this, bounds);
        else
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            Left = bounds.Left / dpi.DpiScaleX; Top = bounds.Top / dpi.DpiScaleY;
        }
    }

    private static SolidColorBrush Brush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); brush.Freeze(); return brush;
    }
}
