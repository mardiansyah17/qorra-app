using DbClient.Core.Services;

namespace DbClient.Tests.Core;

public sealed class UpdatesTests
{
    [Fact]
    public void Parse_picks_installer_and_digest()
    {
        var release = Updates.Parse("""
            {
              "tag_name": "v1.2.3",
              "html_url": "https://github.com/x/y/releases/tag/v1.2.3",
              "assets": [
                { "name": "source.zip", "browser_download_url": "https://x/source.zip" },
                { "name": "Qorra-Setup-1.2.3.exe", "browser_download_url": "https://x/Qorra-Setup-1.2.3.exe", "digest": "sha256:ABCDEF" }
              ]
            }
            """);

        Assert.NotNull(release);
        Assert.Equal(new Version(1, 2, 3), release.Version);
        Assert.Equal("https://x/Qorra-Setup-1.2.3.exe", release.InstallerUrl);
        Assert.Equal("ABCDEF", release.Sha256);
        Assert.Equal("https://github.com/x/y/releases/tag/v1.2.3", release.PageUrl);
    }

    [Fact]
    public void Parse_rejects_non_version_tags_and_tolerates_missing_assets()
    {
        Assert.Null(Updates.Parse("""{ "tag_name": "nightly" }"""));
        var release = Updates.Parse("""{ "tag_name": "0.2" }""");
        Assert.NotNull(release);
        Assert.Null(release.InstallerUrl);
        Assert.True(release.Version > Updates.Normalize(new Version(0, 1, 0, 0)));
        Assert.Equal(Updates.Normalize(new Version(0, 2, 0, 0)), release.Version);
    }
}
