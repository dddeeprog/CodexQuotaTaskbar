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
        Assert.Contains("OverlayTypography.Text", (string?)window.Attribute("FontFamily"), StringComparison.Ordinal);
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
    public void Menu_action_labels_match_session_title_glyphs_without_clipping(int dpi)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var menu = new TrayMenuWindow(AppSettings.Default);
                var sessions = new SessionStackWindow("menu-font-test");
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    sessions.Apply(new([new("font-test", "立即刷新", CodexSessionState.Running, now)], now));
                    var sessionPanel = Assert.IsType<StackPanel>(sessions.FindName("SessionsPanel"));
                    var sessionButton = Assert.IsType<Button>(Assert.Single(sessionPanel.Children.Cast<UIElement>()));
                    var sessionContent = Assert.IsType<Grid>(sessionButton.Content);
                    var sessionTitle = Assert.Single(sessionContent.Children.OfType<TextBlock>(), text => text.Text == "立即刷新");
                    // Render the actual generated title, not the unrelated shared session shadow resources.
                    sessionContent.Children.Remove(sessionTitle);
                    var titleHost = new Grid();
                    titleHost.Children.Add(sessionTitle);
                    VisualTreeHelper.SetRootDpi(titleHost, new DpiScale(dpi / 96d, dpi / 96d));
                    titleHost.Measure(new System.Windows.Size(302, double.PositiveInfinity));
                    titleHost.Arrange(new Rect(0, 0, 302, titleHost.DesiredSize.Height));
                    titleHost.UpdateLayout();
                    var sessionDrawing = VisualTreeHelper.GetDrawing(sessionTitle);
                    Assert.NotNull(sessionDrawing);
                    var sessionGlyphs = GlyphRuns(sessionDrawing).ToArray();
                    Assert.NotEmpty(sessionGlyphs);

                    var content = (FrameworkElement)menu.Content;
                    VisualTreeHelper.SetRootDpi(content, new DpiScale(dpi / 96d, dpi / 96d));
                    content.Measure(new System.Windows.Size(menu.Width, double.PositiveInfinity));
                    content.Arrange(new Rect(0, 0, menu.Width, content.DesiredSize.Height));
                    content.UpdateLayout();
                    Assert.Equal(dpi / 96d, VisualTreeHelper.GetDpi(content).PixelsPerDip);

                    var labels = Descendants<Button>(content)
                        .SelectMany(button => Descendants<TextBlock>(button))
                        .ToArray();
                    Assert.Equal(10, labels.Length);
                    foreach (var label in labels)
                    {
                        Assert.Equal(OverlayTypography.Text, label.FontFamily);
                        Assert.Equal(FontWeights.SemiBold, label.FontWeight);
                        Assert.Equal(13.5, label.FontSize);
                        Assert.Equal(sessionTitle.FontFamily, label.FontFamily);
                        Assert.Equal(sessionTitle.FontWeight, label.FontWeight);
                        Assert.Equal(sessionTitle.FontSize, label.FontSize);
                        Assert.Equal(TextFormattingMode.Ideal, TextOptions.GetTextFormattingMode(label));
                        var fullText = new FormattedText(label.Text, CultureInfo.CurrentUICulture,
                            label.FlowDirection, new Typeface(label.FontFamily, label.FontStyle,
                                label.FontWeight, label.FontStretch), label.FontSize, label.Foreground, dpi / 96d);
                        Assert.True(fullText.WidthIncludingTrailingWhitespace <= label.ActualWidth + 0.5,
                            $"Menu label width was clipped: {label.Text}");
                        Assert.True(fullText.Height <= label.ActualHeight + 1,
                            $"Menu label height was clipped: {label.Text}");
                        var drawing = VisualTreeHelper.GetDrawing(label);
                        Assert.NotNull(drawing);
                        var glyphs = GlyphRuns(drawing).ToArray();
                        Assert.NotEmpty(glyphs);
                        foreach (var glyph in glyphs)
                        {
                            Assert.True(glyph.GlyphTypeface.FamilyNames.Values.Contains("Noto Sans SC"),
                                $"{label.Text}: {string.Join(", ", glyph.GlyphTypeface.FamilyNames.Values)}; {glyph.GlyphTypeface.FontUri}");
                            Assert.Equal("pack", glyph.GlyphTypeface.FontUri.Scheme);
                            Assert.Equal(sessionGlyphs[0].GlyphTypeface.FontUri, glyph.GlyphTypeface.FontUri);
                            Assert.Equal(sessionGlyphs[0].GlyphTypeface.StyleSimulations, glyph.GlyphTypeface.StyleSimulations);
                            Assert.Equal(sessionGlyphs[0].FontRenderingEmSize, glyph.FontRenderingEmSize);
                        }
                    }
                    var matchingLabel = Assert.Single(labels, label => label.Text == sessionTitle.Text);
                    var matchingGlyphs = GlyphRuns(VisualTreeHelper.GetDrawing(matchingLabel)!).ToArray();
                    Assert.Equal(sessionGlyphs.SelectMany(glyph => glyph.GlyphIndices), matchingGlyphs.SelectMany(glyph => glyph.GlyphIndices));
                    Assert.Equal(sessionGlyphs.SelectMany(glyph => glyph.AdvanceWidths), matchingGlyphs.SelectMany(glyph => glyph.AdvanceWidths));
                    SaveProof(content, dpi);
                }
                finally
                {
                    menu.Close();
                    sessions.Close();
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Menu typography verification did not finish in time.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
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
        using var stream = File.Create(Path.Combine(directory, $"menu-matched-{dpi}.png"));
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
