using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.IO;
using CodexQuotaTaskbar.Host.Shortcuts;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using TextBox = System.Windows.Controls.TextBox;

namespace CodexQuotaTaskbar.Host.UI;

internal sealed class WebShortcutAddWindow : Window
{
    private readonly TextBox input;
    private readonly TextBlock errorText;
    private readonly TextBlock selectedIconText;
    private readonly Button clearIconButton;
    private bool composing;
    private bool imeEnterDown;
    private int compositionGeneration;

    internal WebShortcutAddRequest? AcceptedRequest { get; private set; }
    internal string? AcceptedAddress => AcceptedRequest?.Address;
    internal string? SelectedIconPath { get; private set; }

    internal WebShortcutAddWindow()
    {
        NameScope.SetNameScope(this, new NameScope());
        Title = "添加网站";
        Width = 360;
        SizeToContent = System.Windows.SizeToContent.Height;
        ResizeMode = System.Windows.ResizeMode.NoResize;
        WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = OverlayTypography.Interface;
        FontWeight = FontWeights.SemiBold;
        Background = SystemParameters.HighContrast ? System.Windows.SystemColors.WindowBrush
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 40, 43));
        Foreground = SystemParameters.HighContrast ? System.Windows.SystemColors.WindowTextBrush : Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "网站地址", FontSize = 13, Margin = new Thickness(0, 0, 0, 8) });
        input = new TextBox
        {
            Name = "UrlInput", MaxLength = 2048, FontSize = 14, FontWeight = FontWeights.SemiBold, Padding = new Thickness(8),
            ToolTip = "例如 github.com，可省略 https://", AcceptsReturn = false, TextWrapping = TextWrapping.NoWrap,
            Background = SystemParameters.HighContrast ? System.Windows.SystemColors.ControlBrush
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 59, 62)),
            Foreground = Foreground,
            BorderBrush = SystemParameters.HighContrast ? System.Windows.SystemColors.ActiveBorderBrush
                : new SolidColorBrush(System.Windows.Media.Color.FromArgb(60, 255, 255, 255)),
            CaretBrush = Foreground,
        };
        AutomationProperties.SetName(input, "网站地址");
        RegisterName(input.Name, input);
        panel.Children.Add(input);
        panel.Children.Add(new TextBlock
        {
            Text = "未填写协议时使用 HTTPS", FontSize = 11,
            Opacity = 0.72, Margin = new Thickness(0, 6, 0, 0),
        });
        panel.Children.Add(new TextBlock { Text = "图标（可选）", FontSize = 12, Margin = new Thickness(0, 16, 0, 8) });
        var iconRow = new Grid();
        iconRow.ColumnDefinitions.Add(new ColumnDefinition());
        iconRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        iconRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        selectedIconText = new TextBlock
        {
            Name = "SelectedIconText", Text = "自动获取网站图标", FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Opacity = 0.8,
        };
        RegisterName(selectedIconText.Name, selectedIconText);
        iconRow.Children.Add(selectedIconText);
        var chooseIcon = MakeButton("选择…", "ChooseIconButton");
        chooseIcon.MinWidth = 62;
        chooseIcon.Click += (_, _) => ApplyIconSelection(WebShortcutIconPicker.Choose(this));
        Grid.SetColumn(chooseIcon, 1);
        iconRow.Children.Add(chooseIcon);
        clearIconButton = MakeButton("取消选择", "ClearIconButton");
        clearIconButton.MinWidth = 68;
        clearIconButton.Visibility = Visibility.Collapsed;
        clearIconButton.Click += (_, _) => ClearIconSelection();
        Grid.SetColumn(clearIconButton, 2);
        iconRow.Children.Add(clearIconButton);
        panel.Children.Add(iconRow);
        errorText = new TextBlock
        {
            Name = "ErrorText", FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Foreground = SystemParameters.HighContrast ? System.Windows.SystemColors.WindowTextBrush
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 177, 170)),
            Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0),
        };
        RegisterName(errorText.Name, errorText);
        panel.Children.Add(errorText);
        var actions = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0),
        };
        var cancel = MakeButton("取消", "CancelButton");
        cancel.IsCancel = true;
        cancel.Click += (_, _) => { if (IsVisible) DialogResult = false; };
        var add = MakeButton("添加", "AcceptButton");
        // Enter is handled explicitly so an IME candidate-confirm key cannot invoke a default button.
        add.Click += (_, _) => TryAcceptInput();
        actions.Children.Add(cancel);
        actions.Children.Add(add);
        panel.Children.Add(actions);
        Content = panel;
        input.TextChanged += (_, _) =>
        {
            errorText.Visibility = Visibility.Collapsed;
            AcceptedRequest = null;
        };
        input.AddHandler(TextCompositionManager.PreviewTextInputStartEvent, new TextCompositionEventHandler((_, e) =>
        {
            if (e.TextComposition.AutoComplete == TextCompositionAutoComplete.Off) { composing = true; compositionGeneration++; }
        }), true);
        input.AddHandler(TextCompositionManager.PreviewTextInputUpdateEvent, new TextCompositionEventHandler((_, e) =>
        {
            if (e.TextComposition.AutoComplete == TextCompositionAutoComplete.Off) { composing = true; compositionGeneration++; }
        }), true);
        input.AddHandler(TextCompositionManager.PreviewTextInputEvent, new TextCompositionEventHandler((_, e) =>
        {
            if (e.TextComposition.AutoComplete != TextCompositionAutoComplete.Off || !composing) return;
            var completed = compositionGeneration;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (compositionGeneration == completed) composing = false;
            }));
        }), true);
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.ImeProcessed)
            {
                if (e.ImeProcessedKey == Key.Enter) imeEnterDown = true;
                return;
            }
            if (e.Key != Key.Enter) return;
            if (composing) { imeEnterDown = true; return; }
            e.Handled = true;
            if (ShouldAcceptKey(e.Key, e.ImeProcessedKey, composing, imeEnterDown, e.IsRepeat)
                && Keyboard.Modifiers == ModifierKeys.None) TryAcceptInput();
        };
        input.PreviewKeyUp += (_, e) =>
        {
            if (e.Key == Key.Enter || (e.Key == Key.ImeProcessed && e.ImeProcessedKey == Key.Enter)) imeEnterDown = false;
        };
        input.LostKeyboardFocus += (_, _) => { composing = false; imeEnterDown = false; compositionGeneration++; };
        Loaded += (_, _) => input.Focus();
    }

    private Button MakeButton(string caption, string name)
    {
        var button = new Button
        {
            Name = name, Content = caption, MinWidth = 72, Height = 30,
            Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 0, 12, 0),
            FontFamily = OverlayTypography.Interface, FontSize = 12, FontWeight = FontWeights.SemiBold,
        };
        RegisterName(name, button);
        return button;
    }

    internal bool TryAcceptInput()
    {
        if (composing || imeEnterDown) return false;
        if (!WebShortcut.TryNormalizeUrl(input.Text, out var uri, out var error))
        {
            errorText.Text = error ?? "请输入有效的网站地址";
            errorText.Visibility = Visibility.Visible;
            return false;
        }
        AcceptedRequest = new(uri!.AbsoluteUri, SelectedIconPath);
        if (IsVisible) DialogResult = true;
        return true;
    }

    internal bool ApplyIconSelection(string? selectedPath)
    {
        // Canceling the native picker leaves an earlier selection intact.
        if (!WebShortcutIconPicker.TryNormalizeSelection(selectedPath, out var path)) return false;
        SelectedIconPath = path;
        AcceptedRequest = null;
        selectedIconText.Text = Path.GetFileName(path);
        clearIconButton.Visibility = Visibility.Visible;
        return true;
    }

    internal void ClearIconSelection()
    {
        SelectedIconPath = null;
        AcceptedRequest = null;
        selectedIconText.Text = "自动获取网站图标";
        clearIconButton.Visibility = Visibility.Collapsed;
    }

    internal static bool ShouldAcceptKey(Key key, Key imeProcessedKey, bool composing, bool imeEnterDown, bool repeat) =>
        key == Key.Enter && !composing && !imeEnterDown && !repeat;
}
