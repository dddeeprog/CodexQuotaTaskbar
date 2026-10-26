using System.Drawing;
using System.Xml.Linq;
using System.Globalization;
using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Host.Tests.TestSupport;
using CodexQuotaTaskbar.Host.UI;
using CodexQuotaTaskbar.Host.Settings;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class TrayMenuWindowTests
{
    [Theory]
    [InlineData(272, 300)]
    [InlineData(306, 700)]
    [InlineData(408, 900)]
    public void Island_menu_remains_centered_as_its_height_changes(int width, int height)
    {
        var work = new Rectangle(-1920, 0, 1920, 1040);
        var center = new Point(-850, 960);
        var result = TrayMenuWindow.CalculateAnchoredBounds(work, center, new Size(width, height), true, 8, true);
        Assert.InRange(Math.Abs(result.Left + result.Width / 2d - center.X), 0, 0.5);
        Assert.InRange(result.Top, work.Top + 8, center.Y);
    }

    [Fact]
    public void Menu_is_kept_inside_the_current_screen_work_area()
    {
        var workArea = new Rectangle(-1920, 0, 1920, 1040);
        var cursor = new Point(-1918, 2);

        var result = TrayMenuWindow.CalculateBounds(workArea, cursor, new Size(276, 430));

        Assert.InRange(result.Left, workArea.Left + 8, workArea.Right - result.Width - 8);
        Assert.InRange(result.Top, workArea.Top + 8, workArea.Bottom - result.Height - 8);
    }

    [Fact]
    public void Host_uses_the_diandao_icon_for_exe_and_sized_tray_frames()
    {
        var project = XDocument.Load(RepositoryPaths.HostProject);
        var applicationIcon = project.Descendants("ApplicationIcon").Single().Value;
        var traySource = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "src", "CodexQuotaTaskbar.Host", "Tray", "TrayController.cs"));

        Assert.Equal("Assets\\QuotaIslandIcon.ico", applicationIcon);
        Assert.Contains("SystemInformation.SmallIconSize", traySource, StringComparison.Ordinal);
        Assert.Contains(project.Descendants("EmbeddedResource"), element => (string?)element.Attribute("Include") == applicationIcon);
        foreach (var size in new[] { 16, 20, 24, 32 })
        {
            using var stream = typeof(TrayMenuWindow).Assembly.GetManifestResourceStream("CodexQuotaTaskbar.Host.Assets.QuotaIslandIcon.ico");
            Assert.NotNull(stream);
            using var icon = new System.Drawing.Icon(stream, new Size(size, size));
            Assert.Equal(new Size(size, size), icon.Size);
            using var image = icon.ToBitmap();
            Assert.Equal(0, image.GetPixel(0, 0).A);
            Assert.Equal(0, image.GetPixel(size - 1, size - 1).A);
        }
        Assert.DoesNotContain("SystemIcons.Information", traySource, StringComparison.Ordinal);
        Assert.DoesNotContain("ContextMenuStrip", traySource, StringComparison.Ordinal);
    }

    [Fact]
    public void Capsule_does_not_show_the_default_white_system_tooltip()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "src", "CodexQuotaTaskbar.Host", "UI", "QuotaCapsuleWindow.xaml.cs"));

        Assert.Contains("ToolTip = null", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildToolTip", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Menu_uses_the_same_ideal_text_spacing_as_the_other_overlay_surfaces()
    {
        var xaml = XDocument.Load(Path.Combine(RepositoryPaths.Root, "src", "CodexQuotaTaskbar.Host", "UI", "TrayMenuWindow.xaml"));
        var window = xaml.Root!;
        Assert.Equal("True", (string?)window.Attribute("UseLayoutRounding"));
        Assert.Equal("Ideal", (string?)window.Attribute("TextOptions.TextFormattingMode"));
        Assert.Equal("Grayscale", (string?)window.Attribute("TextOptions.TextRenderingMode"));
        Assert.Contains("OverlayTypography.Interface", (string?)window.Attribute("FontFamily"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MenuAction", "Chrome")]
    [InlineData("ChoiceAction", "ChoiceChrome")]
    [InlineData("BinaryAction", "Chrome")]
    public void Menu_button_templates_keep_neutral_focus_hover_selection_and_geometry(string key, string chromeName)
    {
        var xaml = XDocument.Load(Path.Combine(RepositoryPaths.Root, "src", "CodexQuotaTaskbar.Host", "UI", "TrayMenuWindow.xaml"));
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Style(string name) => Assert.Single(xaml.Descendants(p + "Style"), node => (string?)node.Attribute(x + "Key") == name);
        string? Setter(XElement node, string property) => (string?)Assert.Single(node.Elements(p + "Setter"),
            item => (string?)item.Attribute("Property") == property).Attribute("Value");
        var basic = Style("MenuAction");
        var style = Style(key);
        var template = Assert.Single(style.Descendants(p + "ControlTemplate"));
        var focus = Assert.Single(template.Descendants(p + "Trigger"), node => (string?)node.Attribute("Property") == "IsKeyboardFocused");
        Assert.Equal("True", (string?)focus.Attribute("Value"));
        var focusBorder = Assert.Single(focus.Elements(p + "Setter"), node => (string?)node.Attribute("Property") == "BorderBrush");
        Assert.Equal(chromeName, (string?)focusBorder.Attribute("TargetName"));
        Assert.Equal("{DynamicResource MenuChoiceBorderBrush}", (string?)focusBorder.Attribute("Value"));
        var hover = Assert.Single(template.Descendants(p + "Trigger"), node => (string?)node.Attribute("Property") == "IsMouseOver");
        Assert.Equal("True", (string?)hover.Attribute("Value"));
        Assert.Equal("{DynamicResource MenuHoverBrush}", Setter(hover, "Background"));
        var chrome = Assert.Single(template.Descendants(p + "Border"), node => (string?)node.Attribute(x + "Name") == chromeName);
        Assert.Equal("1", key == "MenuAction" ? Setter(focus, "BorderThickness") : (string?)chrome.Attribute("BorderThickness"));
        if (key != "MenuAction") Assert.Equal("{StaticResource MenuAction}", (string?)style.Attribute("BasedOn"));
        if (key == "ChoiceAction")
        {
            var selected = Assert.Single(template.Descendants(p + "Trigger"), node => (string?)node.Attribute("Property") == "Tag"
                && (string?)node.Attribute("Value") == "Selected");
            Assert.Equal("{DynamicResource MenuSelectedBrush}", Setter(selected, "Background"));
            Assert.Equal("{DynamicResource MenuChoiceBorderBrush}", Setter(selected, "BorderBrush"));
        }
        foreach (var (resource, color) in new[] { ("MenuHoverBrush", "#0EFFFFFF"), ("MenuSelectedBrush", "#22FFFFFF"), ("MenuChoiceBorderBrush", "#18FFFFFF") })
            Assert.Equal(color, (string?)Assert.Single(xaml.Descendants(p + "SolidColorBrush"), node => (string?)node.Attribute(x + "Key") == resource).Attribute("Color"));
        Assert.Equal("Transparent", Setter(basic, "Background"));
        Assert.Equal("32", Setter(basic, "Height"));
        Assert.Equal("10,0", Setter(basic, "Padding"));
        Assert.Equal("13", Setter(basic, "FontSize"));
        Assert.Equal("SemiBold", Setter(basic, "FontWeight"));
        Assert.Equal("272", (string?)xaml.Root!.Attribute("Width"));
        Assert.Equal("460", (string?)xaml.Root.Attribute("MaxHeight"));
    }

    [Fact]
    public void Menu_uses_the_shared_opaque_surface_and_blurs_only_the_backdrop()
    {
        var uiDirectory = Path.Combine(RepositoryPaths.Root, "src", "CodexQuotaTaskbar.Host", "UI");
        var xaml = XDocument.Load(Path.Combine(uiDirectory, "TrayMenuWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var glass = Assert.Single(xaml.Descendants(), element => (string?)element.Attribute(x + "Name") == "GlassBorder");
        var backdrop = Assert.Single(xaml.Descendants(), element => (string?)element.Attribute(x + "Name") == "BackdropLayer");
        Assert.Equal("{x:Static ui:OverlayGlassMaterial.Surface}", (string?)glass.Attribute("Background"));
        Assert.Equal("{x:Static ui:OverlayGlassMaterial.Border}", (string?)glass.Attribute("BorderBrush"));
        Assert.Equal("#011D1D1F", (string?)backdrop.Attribute("Background"));
        var source = File.ReadAllText(Path.Combine(uiDirectory, "TrayMenuWindow.xaml.cs"));
        Assert.Contains("FrostedBlur.SetBlurRadius(BackdropLayer, OverlayGlassMaterial.BlurRadius)", source, StringComparison.Ordinal);
        Assert.Contains("FrostedBlur.SetMerging(BackdropLayer, OverlayGlassMaterial.Merging)", source, StringComparison.Ordinal);
        Assert.Contains("FrostedBlur.SetEnableBlur(BackdropLayer, enabled)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FrostedBlur.SetEnableBlur(GlassBorder,", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(108)]
    [InlineData(144)]
    public void Lightweight_menu_and_submenus_use_system_ui_font_without_clipping(int dpi)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var menu = new TrayMenuWindow(AppSettings.Default with { MaterialTransparencyPercent = 37, LowQuotaNotifications = false });
                try
                {
                    Assert.Equal(272, menu.Width); // 252 DIP content plus safe shadow margins
                    Assert.Null(menu.FindName("RunningText"));
                    Assert.Null(menu.FindName("AllTaskbarsTrack"));
                    var content = (FrameworkElement)menu.Content;
                    VisualTreeHelper.SetRootDpi(content, new DpiScale(dpi / 96d, dpi / 96d));
                    foreach (var section in new[] { "", "Appearance", "Preferences" })
                    {
                        if (section.Length > 0)
                            Assert.IsType<Button>(menu.FindName(section + "Button")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        content.Measure(new System.Windows.Size(menu.Width, double.PositiveInfinity));
                        content.Arrange(new Rect(0, 0, menu.Width, content.DesiredSize.Height));
                        content.UpdateLayout();
                        Assert.InRange(Assert.IsType<Border>(menu.FindName("GlassBorder")).ActualWidth, 251.5, 252.5); // subpixel DPI rounding
                        Assert.Null(menu.FindName("AppearancePopup"));
                        Assert.Null(menu.FindName("PreferencesPopup"));
                        var open = section.Length == 0 ? null : Assert.IsType<Grid>(menu.FindName(section + "Root"));
                        if (open is not null)
                        {
                            Assert.Equal(Visibility.Visible, open.Visibility);
                            Assert.True(open.ActualHeight > 100);
                            Assert.Equal(Enum.Parse<MenuSection>(section), menu.CurrentSection);
                        }
                        foreach (var label in Descendants<Button>(content).SelectMany(button => Descendants<TextBlock>(button))
                            .Where(label => label.ActualWidth > 0 && label.Name != "StyleSummaryText"
                                && !(section.Length > 0 && Assert.IsType<StackPanel>(menu.FindName("MenuItems")).IsAncestorOf(label))
                                && !(section != "Appearance" && Assert.IsType<Grid>(menu.FindName("AppearanceRoot")).IsAncestorOf(label))
                                && !(section != "Preferences" && Assert.IsType<Grid>(menu.FindName("PreferencesRoot")).IsAncestorOf(label))))
                        {
                            Assert.Equal(OverlayTypography.Interface, label.FontFamily);
                            Assert.Equal(FontWeights.SemiBold, label.FontWeight);
                            Assert.InRange(label.FontSize, 12, 13);
                            var fullText = new FormattedText(label.Text, CultureInfo.CurrentUICulture, label.FlowDirection,
                                new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch),
                                label.FontSize, label.Foreground, dpi / 96d);
                            Assert.True(fullText.WidthIncludingTrailingWhitespace <= label.ActualWidth + 0.5, label.Text);
                            Assert.True(fullText.Height <= label.ActualHeight + 1, label.Text);
                            var glyphs = GlyphRuns(VisualTreeHelper.GetDrawing(label)!).ToArray();
                            Assert.NotEmpty(glyphs);
                            Assert.All(glyphs, glyph => Assert.All(glyph.GlyphIndices, index => Assert.NotEqual((ushort)0, index)));
                        }
                        content.Name = "menu" + section;
                        SaveProof(content, dpi);
                    }
                    var requested = new List<AppSettings>();
                    menu.SettingsChangeRequested += (_, value) => requested.Add(value);
                    var toggle = Assert.IsType<Button>(menu.FindName("NotificationsButton"));
                    toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.True(Assert.Single(requested).LowQuotaNotifications);
                    Assert.Equal(37, requested[0].EffectiveMaterialTransparencyPercent);
                    Assert.Equal(Visibility.Visible, Assert.IsType<System.Windows.Shapes.Path>(menu.FindName("NotificationsCheck")).Visibility);
                    Assert.Equal("已开启", System.Windows.Automation.AutomationProperties.GetItemStatus(toggle));
                    var slider = Assert.IsType<Slider>(menu.FindName("MaterialTransparencySlider"));
                    slider.Value = 42;
                    menu.FlushMaterialTransparencyChange();
                    Assert.Equal(42, requested.Last().EffectiveMaterialTransparencyPercent);
                    Assert.True(requested.Last().LowQuotaNotifications);
                    var trayToggle = Assert.IsType<Button>(menu.FindName("TrayIconButton"));
                    trayToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.False(requested.Last().TrayIconEnabled);
                    Assert.Equal("已关闭", System.Windows.Automation.AutomationProperties.GetItemStatus(trayToggle));
                    trayToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.True(requested.Last().TrayIconEnabled);
                    Assert.Equal(Visibility.Visible, Assert.IsType<System.Windows.Shapes.Path>(menu.FindName("TrayIconCheck")).Visibility);
                }
                finally { menu.Close(); }
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void Native_submenu_stays_on_screen_and_closes_with_its_owner()
    {
        // Opt-in native-window smoke check; normal CI uses offscreen layout tests above.
        if (Environment.GetEnvironmentVariable("CODEX_QUOTA_UI_PROOFS") != "1") return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            TrayMenuWindow? menu = null;
            try
            {
                menu = new TrayMenuWindow(AppSettings.Default);
                var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
                var island = new Rectangle(work.Left + work.Width / 2 - 140, work.Bottom - 100, 280, 80);
                // Deliberately unrelated cursor: island invocation must not align to the pointer.
                menu.ShowAt(new Point(work.Right - 10, work.Bottom - 10), 8, island);
                void Pump()
                {
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(240) };
                    timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                    timer.Start();
                    System.Windows.Threading.Dispatcher.PushFrame(frame);
                }
                Pump();
                var mainHandle = new System.Windows.Interop.WindowInteropHelper(menu).Handle;
                Assert.True(CodexQuotaTaskbar.Host.Platform.NativeMethods.GetWindowRect(mainHandle, out var before));
                Assert.False(new Rectangle(before.Left, before.Top, before.Right - before.Left, before.Bottom - before.Top).IntersectsWith(island));
                foreach (var name in new[] { "Appearance", "Preferences" })
                {
                    Assert.IsType<Button>(menu.FindName(name + "Button")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Pump();
                    var page = Assert.IsType<Grid>(menu.FindName(name + "Root"));
                    Assert.True(menu.IsVisible);
                    Assert.Equal(Visibility.Visible, page.Visibility);
                    Assert.True(page.IsHitTestVisible);
                    Assert.True(page.ActualHeight > 100);
                    var other = Assert.IsType<Grid>(menu.FindName((name == "Appearance" ? "Preferences" : "Appearance") + "Root"));
                    Assert.Equal(Visibility.Collapsed, other.Visibility);
                    Assert.Equal(272, menu.Width);
                    Assert.Same(PresentationSource.FromVisual(menu), PresentationSource.FromVisual(page));
                    Assert.True(CodexQuotaTaskbar.Host.Platform.NativeMethods.GetWindowRect(mainHandle, out var bounds));
                    Assert.InRange(bounds.Left, work.Left, work.Right - 1);
                    Assert.InRange(bounds.Top, work.Top, work.Bottom - 1);
                    Assert.InRange(bounds.Right, bounds.Left + 1, work.Right);
                    Assert.InRange(bounds.Bottom, bounds.Top + 1, work.Bottom);
                    if (name == "Appearance")
                    {
                        var slider = Assert.IsType<Slider>(menu.FindName("MaterialTransparencySlider"));
                        slider.Focus();
                        var key = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                            PresentationSource.FromVisual(menu), 0, System.Windows.Input.Key.Left) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                        Assert.IsType<Grid>(menu.FindName("AppearanceRoot")).RaiseEvent(key);
                        Assert.False(key.Handled);
                        Assert.Equal(Visibility.Visible, page.Visibility);
                        Assert.True(page.IsHitTestVisible);
                    }
                    Assert.False(new Rectangle(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top).IntersectsWith(island));
                }
                menu.Hide();
                Assert.Equal(Visibility.Collapsed, Assert.IsType<Grid>(menu.FindName("AppearanceRoot")).Visibility);
                Assert.Equal(Visibility.Collapsed, Assert.IsType<Grid>(menu.FindName("PreferencesRoot")).Visibility);
                Assert.Equal(MenuSection.None, menu.CurrentSection);
                using var controller = new CodexQuotaTaskbar.Host.Tray.TrayController(AppSettings.Default with { ShowTrayIcon = false });
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var icon = Assert.IsType<System.Windows.Forms.NotifyIcon>(controller.GetType().GetField("icon", flags)!.GetValue(controller));
                var controllerMenu = Assert.IsType<TrayMenuWindow>(controller.GetType().GetField("menu", flags)!.GetValue(controller));
                Assert.False(icon.Visible);
                var visibilityToggle = Assert.IsType<Button>(controllerMenu.FindName("TrayIconButton"));
                visibilityToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(icon.Visible);
                Assert.True(controller.Settings.TrayIconEnabled);
                visibilityToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(icon.Visible);
                Assert.False(controller.Settings.TrayIconEnabled);
            }
            catch (Exception error) { failure = error; }
            finally { menu?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void SaveProof(FrameworkElement content, int dpi)
    {
        if (Environment.GetEnvironmentVariable("CODEX_QUOTA_UI_PROOFS") != "1") return;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling((content.ActualWidth + content.Margin.Left + content.Margin.Right) * dpi / 96d),
            (int)Math.Ceiling((content.ActualHeight + content.Margin.Top + content.Margin.Bottom) * dpi / 96d), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.Combine(RepositoryPaths.Root, "artifacts", "tray-font-proof");
        Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, $"menu-{(string.IsNullOrEmpty(content.Name) ? "main" : content.Name)}-{dpi}.png"));
        encoder.Save(stream);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }
            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static IEnumerable<GlyphRun> GlyphRuns(Drawing drawing)
    {
        if (drawing is GlyphRunDrawing glyph)
        {
            yield return glyph.GlyphRun;
        }
        else if (drawing is DrawingGroup group)
        {
            foreach (var child in group.Children.SelectMany(GlyphRuns))
            {
                yield return child;
            }
        }
    }

    [Fact]
    public void Selected_menu_font_is_packaged_with_the_host()
    {
        var project = XDocument.Load(RepositoryPaths.HostProject);
        var resources = project.Descendants("Resource").Select(node => (string?)node.Attribute("Include")).ToArray();

        Assert.Contains("Assets\\Fonts\\NotoSansSC-Regular.otf", resources);
        Assert.Contains("Assets\\Fonts\\NotoSansSC-Medium.otf", resources);
        Assert.True(File.Exists(Path.Combine(RepositoryPaths.Root, "src", "CodexQuotaTaskbar.Host", "Assets", "Fonts", "NotoSansSC-Regular.otf")));
        Assert.True(File.Exists(Path.Combine(RepositoryPaths.Root, "src", "CodexQuotaTaskbar.Host", "Assets", "Fonts", "NotoSansSC-Medium.otf")));
    }

    [Fact]
    public void Host_embeds_project_and_font_licenses_without_changing_the_single_file_update_contract()
    {
        var project = XDocument.Load(RepositoryPaths.HostProject);
        var resources = project.Descendants("Resource")
            .Select(node => new
            {
                Include = (string?)node.Attribute("Include"),
                Link = node.Element("Link")?.Value,
            })
            .ToArray();

        Assert.Contains(resources, resource =>
            resource.Include == "Assets\\Fonts\\OFL.txt" &&
            resource.Link == "Assets\\Licenses\\Noto-Sans-SC-OFL.txt");
        Assert.Contains(resources, resource =>
            resource.Include == "..\\..\\LICENSE" &&
            resource.Link == "Assets\\Licenses\\GPL-3.0.txt");
        Assert.Contains(resources, resource =>
            resource.Include == "..\\..\\THIRD_PARTY_NOTICES.md" &&
            resource.Link == "Assets\\Licenses\\THIRD_PARTY_NOTICES.md");
    }
}
