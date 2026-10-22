using CodexQuotaTaskbar.Host.Tests.TestSupport;

namespace CodexQuotaTaskbar.Host.Tests.Installer;

public sealed class InstallerContractTests
{
    private static string Script => File.ReadAllText(Path.Combine(RepositoryPaths.Root, "build", "installer", "QuotaIsland.iss"));

    [Fact]
    public void Installs_per_user_with_a_stable_identity_and_application_mutex()
    {
        Assert.Contains("AppId={{ADC74E3C-B465-4C0A-9D0D-3D428A2AE5F7}", Script);
        Assert.Contains("PrivilegesRequired=lowest", Script);
        Assert.Contains("DefaultDirName={localappdata}\\Programs\\CodexQuotaTaskbar", Script);
        Assert.Contains("AppMutex=Local\\CodexQuotaTaskbar.Host", Script);
        Assert.Contains("CloseApplications=no", Script);
        Assert.Contains("ArchitecturesAllowed=x64compatible", Script);
    }

    [Fact]
    public void Payload_is_explicit_and_does_not_include_profiles_or_wildcards()
    {
        var files = Script.Split("[Files]")[1].Split("[Icons]")[0];
        Assert.Equal(3, files.Split('\n').Count(line => line.StartsWith("Source:", StringComparison.Ordinal)));
        Assert.Contains("CodexQuotaTaskbar.exe", files);
        Assert.Contains("LICENSE", files);
        Assert.Contains("THIRD_PARTY_NOTICES.md", files);
        Assert.DoesNotContain("*", files);
        Assert.DoesNotContain("recursesubdirs", files);
        Assert.DoesNotContain("[UninstallDelete]", Script);
        Assert.DoesNotContain("[InstallDelete]", Script);
    }

    [Fact]
    public void Uninstall_only_removes_autostart_when_the_exact_install_path_matches()
    {
        Assert.Contains("CompareText(StartupCommand, InstalledCommand) = 0", Script);
        Assert.Contains("ExpandConstant('{app}\\CodexQuotaTaskbar.exe')", Script);
        Assert.Contains("RegDeleteValue(HKCU", Script);
        Assert.DoesNotContain("RegDeleteKey", Script);
        Assert.DoesNotContain("DelTree", Script);
        Assert.DoesNotContain("[Registry]", Script);
    }

    [Fact]
    public void Offers_shortcuts_without_starting_during_silent_install()
    {
        Assert.Contains("创建桌面快捷方式\"; Flags: unchecked", Script);
        Assert.Contains("{group}\\Codex 额度岛", Script);
        Assert.Contains("{autodesktop}\\Codex 额度岛", Script);
        Assert.Contains("postinstall skipifsilent", Script);
        Assert.Contains("MessagesFile: \"ChineseSimplified.isl\"", Script);
        Assert.Contains("LicenseFile={#RepoRoot}\\LICENSE", Script);
    }

    [Fact]
    public void Build_uses_separate_self_contained_staging_and_version_validation()
    {
        var build = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "build", "package-installer.ps1"));
        Assert.Contains("artifacts\\installer-publish", build);
        Assert.Contains("--self-contained true", build);
        Assert.Contains("-p:PublishSingleFile=true", build);
        Assert.Contains("Published executable version differs from VERSION", build);
        Assert.DoesNotContain("Remove-Item", build);
        Assert.DoesNotContain("Invoke-WebRequest", build);
    }
}
