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
    public void Host_uses_the_original_quota_island_icon_for_exe_and_tray()
    {
        var project = XDocument.Load(RepositoryPaths.HostProject);
        var applicationIcon = project.Descendants("ApplicationIcon").Single().Value;
        var traySource = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "src", "CodexQuotaTaskbar.Host", "Tray", "TrayController.cs"));

        Assert.Equal("Assets\\QuotaIslandIcon.ico", applicationIcon);
        Assert.Contains("Icon.ExtractAssociatedIcon", traySource, StringComparison.Ordinal);
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
                    Assert.Equal(252, menu.Width);
                    Assert.Null(menu.FindName("RunningText"));
                    Assert.Null(menu.FindName("AllTaskbarsTrack"));
                    foreach (var name in new[] { "AppearancePopup", "PreferencesPopup" })
                    {
                        var popup = Assert.IsType<System.Windows.Controls.Primitives.Popup>(menu.FindName(name));
                        Assert.False(popup.IsOpen);
                        Assert.Equal(System.Windows.Controls.Primitives.PlacementMode.Custom, popup.Placement);
                        Assert.True(popup.AllowsTransparency);
                    }
                    foreach (var content in new[]
                    {
                        (FrameworkElement)menu.Content,
                        Assert.IsType<Grid>(menu.FindName("AppearanceRoot")),
                        Assert.IsType<Grid>(menu.FindName("PreferencesRoot")),
                    })
                    {
                        VisualTreeHelper.SetRootDpi(content, new DpiScale(dpi / 96d, dpi / 96d));
                        content.Measure(new System.Windows.Size(252, double.PositiveInfinity));
                        content.Arrange(new Rect(0, 0, 252, content.DesiredSize.Height));
                        content.UpdateLayout();
                        Assert.InRange(content.ActualHeight, 150, 340);
                        var labels = Descendants<Button>(content).SelectMany(button => Descendants<TextBlock>(button))
                            .Where(label => label.ActualWidth > 0 && label.Name != "StyleSummaryText").ToArray();
                        Assert.NotEmpty(labels);
                        foreach (var label in labels)
                        {
                            Assert.Equal(OverlayTypography.Interface, label.FontFamily);
                            Assert.Equal(FontWeights.Normal, label.FontWeight);
                            Assert.Equal(13, label.FontSize);
                            var fullText = new FormattedText(label.Text, CultureInfo.CurrentUICulture, label.FlowDirection,
                                new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch),
                                label.FontSize, label.Foreground, dpi / 96d);
                            Assert.True(fullText.WidthIncludingTrailingWhitespace <= label.ActualWidth + 0.5, label.Text);
                            Assert.True(fullText.Height <= label.ActualHeight + 1, label.Text);
                            var glyphs = GlyphRuns(VisualTreeHelper.GetDrawing(label)!).ToArray();
                            Assert.NotEmpty(glyphs);
                            Assert.All(glyphs, glyph =>
                            {
                                Assert.DoesNotContain("Noto Sans SC", glyph.GlyphTypeface.FamilyNames.Values);
                                Assert.All(glyph.GlyphIndices, index => Assert.NotEqual((ushort)0, index));
                            });
                        }
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
                menu.ShowAt(new Point(work.Right - 10, work.Bottom - 10));
                void Pump()
                {
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    menu.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                        new Action(() => frame.Continue = false));
                    System.Windows.Threading.Dispatcher.PushFrame(frame);
                }
                Pump();
                var mainHandle = new System.Windows.Interop.WindowInteropHelper(menu).Handle;
                Assert.True(CodexQuotaTaskbar.Host.Platform.NativeMethods.GetWindowRect(mainHandle, out var before));
                foreach (var name in new[] { "Appearance", "Preferences" })
                {
                    Assert.IsType<Button>(menu.FindName(name + "Button")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Pump();
                    var popup = Assert.IsType<System.Windows.Controls.Primitives.Popup>(menu.FindName(name + "Popup"));
                    Assert.True(menu.IsVisible);
                    Assert.True(popup.IsOpen);
                    Assert.False(BlurredBackground.WPF.BlurredBackground.GetEnableBlur(
                        Assert.IsType<Border>(menu.FindName(name + "Backdrop"))));
                    var popupSource = Assert.IsType<System.Windows.Interop.HwndSource>(PresentationSource.FromVisual(popup.Child));
                    Assert.True(CodexQuotaTaskbar.Host.Platform.NativeMethods.GetWindowRect(popupSource.Handle, out var bounds));
                    Assert.InRange(bounds.Left, work.Left, work.Right - 1);
                    Assert.InRange(bounds.Top, work.Top, work.Bottom - 1);
                    Assert.InRange(bounds.Right, bounds.Left + 1, work.Right);
                    Assert.InRange(bounds.Bottom, bounds.Top + 1, work.Bottom);
                    if (name == "Appearance")
                    {
                        var slider = Assert.IsType<Slider>(menu.FindName("MaterialTransparencySlider"));
                        slider.Focus();
                        var key = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                            popupSource, 0, System.Windows.Input.Key.Left) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                        Assert.IsType<Grid>(menu.FindName("AppearanceRoot")).RaiseEvent(key);
                        Assert.False(key.Handled);
                        Assert.True(popup.IsOpen);
                    }
                    Assert.True(CodexQuotaTaskbar.Host.Platform.NativeMethods.GetWindowRect(mainHandle, out var after));
                    Assert.Equal(before.Left, after.Left);
                    Assert.Equal(before.Top, after.Top);
                }
                menu.Hide();
                Assert.False(Assert.IsType<System.Windows.Controls.Primitives.Popup>(menu.FindName("AppearancePopup")).IsOpen);
                Assert.False(Assert.IsType<System.Windows.Controls.Primitives.Popup>(menu.FindName("PreferencesPopup")).IsOpen);
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
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi / 96d),
            (int)Math.Ceiling(content.ActualHeight * dpi / 96d), dpi, dpi, PixelFormats.Pbgra32);
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
