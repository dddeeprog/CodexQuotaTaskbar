using System.Xml.Linq;
using CodexQuotaTaskbar.Host.Tests.TestSupport;

namespace CodexQuotaTaskbar.Host.Tests;

public sealed class ProjectBoundaryTests
{
    [Fact]
    public void Product_version_matches_release_version_file()
    {
        var version = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "VERSION")).Trim();

        Assert.Equal(ProductVersion.Text, version);
        Assert.Equal(ProductVersion.Value, Version.Parse(version));
    }

    [Fact]
    public void Host_has_no_bridge_or_probe_project_reference()
    {
        var project = XDocument.Load(RepositoryPaths.HostProject);
        var references = project.Descendants("ProjectReference")
            .Select(element => Path.GetFileNameWithoutExtension((string)element.Attribute("Include")!))
            .ToArray();

        Assert.Equal(["CodexQuotaTaskbar.Core"], references);
        Assert.DoesNotContain(references, value => value.Contains("Bridge", StringComparison.Ordinal));
        Assert.DoesNotContain(references, value => value.Contains("Probe", StringComparison.Ordinal));
    }

    [Fact]
    public void Host_uses_native_wpf_blur_and_separate_subscription_login_webview()
    {
        var project = XDocument.Load(RepositoryPaths.HostProject);
        var packages = project.Descendants("PackageReference")
            .Select(element => (string)element.Attribute("Include")!)
            .ToArray();

        Assert.Equal(["BlurredBackground.WPF", "Microsoft.Web.WebView2"], packages);
        foreach (var file in new[] { "QuotaCapsuleWindow.xaml", "QuotaPopoverWindow.xaml", "SessionStackWindow.xaml", "TrayMenuWindow.xaml" })
        {
            var path = Path.Combine(RepositoryPaths.Root, "src", "CodexQuotaTaskbar.Host", "UI", file);
            if (File.Exists(path)) Assert.DoesNotContain("WebView", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        }
    }
}
