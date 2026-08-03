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
}
