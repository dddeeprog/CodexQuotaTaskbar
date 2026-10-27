using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.IO;
using System.Reflection;
using CodexQuotaTaskbar.Host.Bongo;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TabControl = System.Windows.Controls.TabControl;
using TabItem = System.Windows.Controls.TabItem;
using TextBox = System.Windows.Controls.TextBox;

namespace CodexQuotaTaskbar.Host.UI;

internal sealed class CoreConsentWindow : Window
{
    private readonly CheckBox agreement;
    private readonly Button accept;
    private bool completed;
    internal CoreConsentDecision Decision { get; private set; } = CoreConsentDecision.Closed;
    internal bool AgreementChecked => agreement.IsChecked == true;
    internal bool AcceptanceEnabled => accept.IsEnabled;
    internal bool AcceptIsDefault => accept.IsDefault;
    internal event Action<Uri>? OpenOfficialRequested;

    internal CoreConsentWindow(CoreTermsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Title = "BongoCat · Core 组件条款";
        Width = Math.Min(680, Math.Max(320, SystemParameters.WorkArea.Width - 40));
        Height = Math.Min(640, Math.Max(300, SystemParameters.WorkArea.Height - 40));
        MinWidth = 300; MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = OverlayTypography.Interface;
        FontSize = 13; FontWeight = FontWeights.SemiBold;
        Background = SystemParameters.HighContrast ? System.Windows.SystemColors.WindowBrush : new SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 40, 43));
        Foreground = SystemParameters.HighContrast ? System.Windows.SystemColors.WindowTextBrush : System.Windows.Media.Brushes.White;
        var layout = new Grid { Margin = new Thickness(18) };
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var summary = new TextBlock
        {
            Text = "以下只适用于 Live2D Cubism Core。点岛 Host 及其他开源部分仍按各自许可证提供。明确同意后，将从本项目 GitHub 发布页下载并校验约 2 MB 的可选 Bongo/Core 组件（已有有效缓存则复用）。拒绝或关闭不下载、不新增安装、不启用 Core，小白球和其他功能继续可用。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        };
        layout.Children.Add(summary);
        var tabs = new TabControl { Margin = new Thickness(0, 0, 0, 12) };
        tabs.Items.Add(new TabItem { Header = "Core 组件条款", Content = Body(document.ProductText) });
        tabs.Items.Add(new TabItem { Header = "Core 官方权利说明", Content = Body(document.OfficialText) });
        tabs.Items.Add(new TabItem { Header = "开源与第三方许可", Content = OtherLicenses() });
        Grid.SetRow(tabs, 1); layout.Children.Add(tabs);
        accept = MakeButton("同意并使用 BongoCat");
        accept.IsDefault = false; accept.IsEnabled = false;
        agreement = new CheckBox { Content = new TextBlock { Text = "我已阅读并同意以上仅 Core 适用的组件条款", TextWrapping = TextWrapping.Wrap }, IsChecked = false, Margin = new Thickness(0, 2, 0, 12) };
        AutomationProperties.SetName(agreement, "确认阅读并同意仅 Core 适用的组件条款");
        agreement.Checked += (_, _) => accept.IsEnabled = true;
        agreement.Unchecked += (_, _) => accept.IsEnabled = false;
        Grid.SetRow(agreement, 2); layout.Children.Add(agreement);
        var actions = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var official = MakeButton("官方网页 ↗");
        official.Click += (_, _) => OpenOfficialRequested?.Invoke(CoreTermsDocument.OfficialUri);
        var decline = MakeButton("保留小白球");
        decline.Click += (_, _) => Finish(CoreConsentDecision.Declined);
        accept.Click += (_, _) => TryAccept();
        actions.Children.Add(official); actions.Children.Add(decline); actions.Children.Add(accept);
        Grid.SetRow(actions, 3); layout.Children.Add(actions);
        Content = layout;
        Closed += (_, _) => completed = true;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Finish(CoreConsentDecision.Closed); }
        };
    }

    private TextBox Body(string value) => new()
    {
        Text = value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(12), Background = Background, Foreground = Foreground, BorderThickness = new Thickness(0),
        FontWeight = FontWeights.Normal,
    };

    private FrameworkElement OtherLicenses()
    {
        var content = new DockPanel();
        var save = MakeButton("保存完整第三方许可证集合（ZIP）");
        save.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        save.Margin = new Thickness(0, 0, 0, 10);
        save.Click += (_, _) => SaveOtherLicenses();
        DockPanel.SetDock(save, Dock.Top); content.Children.Add(save);
        content.Children.Add(Body(ReadProductNotice("GPL-3.0.txt") + "\n\n" + ReadProductNotice("THIRD_PARTY_NOTICES.md")));
        return content;
    }

    private static string ReadProductNotice(string name)
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri(
                "/CodexQuotaTaskbar;component/Assets/Licenses/" + name, UriKind.Relative));
            if (resource is null) return "许可文本未能读取：" + name;
            using var stream = resource.Stream;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            return "许可文本未能读取：" + name;
        }
    }

    internal static byte[] ReadThirdPartyLicenseArchive()
    {
        using var stream = typeof(CoreConsentWindow).Assembly.GetManifestResourceStream(
            "CodexQuotaTaskbar.Host.Assets.Licenses.ThirdPartyLicenses.zip")
            ?? throw new InvalidDataException("Third-party license collection is unavailable.");
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (data.Length + count > 8 * 1024 * 1024) throw new InvalidDataException("License collection is too large.");
            data.Write(buffer, 0, count);
        }
        return data.ToArray();
    }

    private void SaveOtherLicenses()
    {
        try
        {
            var bytes = ReadThirdPartyLicenseArchive();
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "保存第三方许可证集合", FileName = "点岛-第三方许可证.zip",
                DefaultExt = ".zip", Filter = "ZIP 文件 (*.zip)|*.zip", AddExtension = true,
                CheckPathExists = true, OverwritePrompt = true,
            };
            if (dialog.ShowDialog(this) == true) File.WriteAllBytes(dialog.FileName, bytes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            System.Windows.MessageBox.Show(this, "许可证集合未能保存。请重新选择可写位置。", "点岛",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static Button MakeButton(string text) => new() { Content = text, MinHeight = 32, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(5, 0, 0, 4) };

    internal void SetAgreementChecked(bool value) => agreement.IsChecked = value;

    internal bool TryAccept()
    {
        if (!AgreementChecked || !accept.IsEnabled || completed) return false;
        Finish(CoreConsentDecision.Accepted);
        return true;
    }

    private void Finish(CoreConsentDecision value)
    {
        if (completed) return;
        completed = true; Decision = value;
        if (IsVisible) DialogResult = value == CoreConsentDecision.Accepted;
    }
}
