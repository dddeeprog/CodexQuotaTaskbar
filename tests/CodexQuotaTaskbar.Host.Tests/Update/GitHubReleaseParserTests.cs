using CodexQuotaTaskbar.Host.Update;

namespace CodexQuotaTaskbar.Host.Tests.Update;

public sealed class GitHubReleaseParserTests
{
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Parses_exact_official_release_asset()
    {
        var release = GitHubReleaseParser.Parse(CreateJson("v0.1.3"));

        Assert.NotNull(release);
        Assert.Equal(new Version(0, 1, 3), release.Version);
        Assert.Equal(Digest.ToUpperInvariant(), release.Sha256);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Rejects_draft_and_prerelease(bool draft, bool prerelease)
    {
        Assert.Null(GitHubReleaseParser.Parse(CreateJson("v0.1.3", draft, prerelease)));
    }

    [Fact]
    public void Rejects_asset_from_another_repository()
    {
        var json = CreateJson("v0.1.3").Replace(
            "dddeeprog/CodexQuotaTaskbar",
            "someone/other",
            StringComparison.Ordinal);

        Assert.Null(GitHubReleaseParser.Parse(json));
    }

    internal static string CreateJson(string tag, bool draft = false, bool prerelease = false, string? digest = null) => $$"""
        {
          "tag_name": "{{tag}}",
          "draft": {{draft.ToString().ToLowerInvariant()}},
          "prerelease": {{prerelease.ToString().ToLowerInvariant()}},
          "assets": [{
            "name": "CodexQuotaTaskbar-win-x64.zip",
            "browser_download_url": "https://github.com/dddeeprog/CodexQuotaTaskbar/releases/download/{{tag}}/CodexQuotaTaskbar-win-x64.zip",
            "digest": "sha256:{{digest ?? Digest}}"
          }]
        }
        """;
}
