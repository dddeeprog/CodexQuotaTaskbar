using System.Drawing;
using System.Xml.Linq;
using CodexQuotaTaskbar.Host.Tests.TestSupport;
using CodexQuotaTaskbar.Host.UI;

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
    public void Menu_uses_explicit_pixel_aligned_typography_for_its_transparent_surface()
    {
        var xaml = XDocument.Load(Path.Combine(RepositoryPaths.Root, "src", "CodexQuotaTaskbar.Host", "UI", "TrayMenuWindow.xaml"));
        var window = xaml.Root!;
        Assert.Equal("True", (string?)window.Attribute("UseLayoutRounding"));
        Assert.Equal("Display", (string?)window.Attribute("TextOptions.TextFormattingMode"));
        Assert.Equal("Grayscale", (string?)window.Attribute("TextOptions.TextRenderingMode"));
        Assert.Contains("OverlayTypography.Text", (string?)window.Attribute("FontFamily"), StringComparison.Ordinal);
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
