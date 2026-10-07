using System.Runtime.InteropServices;
using System.Text;
using ClipboardManager.Core.Updates;
using ClipboardManager.Hosting;

namespace ClipboardManager.Tests.Updates;

[Trait("Category", "Update")]
public sealed class UpdateRulesTests
{
    private const string Download = "https://github.com/Secoolioo/clipboard-manager/releases/download/v0.10.0/";

    private static ReleaseVersion V(string text) => ReleaseVersion.TryParse(text, out var version) ? version : throw new FormatException(text);

    private static GitHubRelease Release(string tag = "v0.10.0", string? exeUrl = null, long size = 1000, bool prerelease = false, string? htmlUrl = null) =>
        GitHubRelease.Parse(Encoding.UTF8.GetBytes($$"""
            {
              "tag_name": "{{tag}}",
              "html_url": "{{htmlUrl ?? "https://github.com/Secoolioo/clipboard-manager/releases/tag/" + tag}}",
              "prerelease": {{(prerelease ? "true" : "false")}},
              "draft": false,
              "assets": [
                { "name": "ClipboardManager.exe", "browser_download_url": "{{exeUrl ?? Download + "ClipboardManager.exe"}}", "size": {{size}} },
                { "name": "ClipboardManager-arm64.exe", "browser_download_url": "{{Download}}ClipboardManager-arm64.exe", "size": 900 },
                { "name": "SHA256SUMS.txt", "browser_download_url": "{{Download}}SHA256SUMS.txt", "size": 300 }
              ]
            }
            """))!;

    [Theory]
    [InlineData("v0.10.0", 0, 10, 0, "")]
    [InlineData("V1.2.3", 1, 2, 3, "")]
    [InlineData("0.9.0+4f2a9c1d", 0, 9, 0, "")]
    [InlineData("1.0.0-beta.2", 1, 0, 0, "beta.2")]
    [InlineData("0.0.0-dev", 0, 0, 0, "dev")]
    [InlineData(" 2.0.0-rc.1+build.5 ", 2, 0, 0, "rc.1")]
    public void Versions_parse_from_tags_and_informational_versions(string text, int major, int minor, int patch, string prerelease)
    {
        var version = V(text);
        Assert.Equal((major, minor, patch, prerelease), (version.Major, version.Minor, version.Patch, version.Prerelease));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-beta..1")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3+")]
    [InlineData("1.-2.3")]
    [InlineData("latest")]
    public void Invalid_versions_are_rejected(string? text) => Assert.False(ReleaseVersion.TryParse(text, out _));

    [Fact]
    public void Versions_order_by_semver_precedence()
    {
        // SemVer 2.0 §11 example, plus numbers compared numerically, not as text.
        string[] ordered =
        [
            "0.9.0", "0.10.0-alpha", "0.10.0-alpha.1", "0.10.0-alpha.beta", "0.10.0-beta", "0.10.0-beta.2",
            "0.10.0-beta.11", "0.10.0-rc.1", "0.10.0", "0.10.1", "1.0.0", "1.10.0", "2.0.0",
        ];
        for (var i = 0; i < ordered.Length - 1; i++)
        {
            Assert.True(V(ordered[i]) < V(ordered[i + 1]), $"{ordered[i]} < {ordered[i + 1]}");
            Assert.True(V(ordered[i + 1]) > V(ordered[i]), $"{ordered[i + 1]} > {ordered[i]}");
        }

        Assert.Equal(V("v1.2.3"), V("1.2.3+abc"));
        Assert.Equal(0, V("1.0.0-rc.1").CompareTo(V("1.0.0-rc.1+x")));
        Assert.Equal("0.10.0-beta.2", V("v0.10.0-beta.2+sha").ToString());
    }

    [Fact]
    public void The_exe_asset_matches_the_process_architecture()
    {
        Assert.Equal("ClipboardManager.exe", UpdateRules.ExeAssetName(Architecture.X64));
        Assert.Equal("ClipboardManager-arm64.exe", UpdateRules.ExeAssetName(Architecture.Arm64));
        Assert.Null(UpdateRules.ExeAssetName(Architecture.X86));
    }

    [Fact]
    public void Release_json_from_github_is_read()
    {
        var release = Release();
        Assert.Equal("v0.10.0", release.TagName);
        Assert.False(release.Prerelease);
        Assert.Equal(3, release.Assets!.Count);
        Assert.Equal(1000, release.Assets[0].Size);
        Assert.Equal(Download + "ClipboardManager.exe", release.Assets[0].BrowserDownloadUrl);

        Assert.Null(GitHubRelease.Parse("not json"u8));
        Assert.Null(GitHubRelease.Parse("""{"tag_name": 5}"""u8));
        Assert.NotNull(GitHubRelease.Parse("""{"message": "Not Found", "assets": null}"""u8));
    }

    [Theory]
    [InlineData("v0.9.0", ReleaseCheckResult.UpToDate)]
    [InlineData("v0.8.5", ReleaseCheckResult.UpToDate)]
    [InlineData("v0.9.1", ReleaseCheckResult.UpdateAvailable)]
    [InlineData("v0.10.0", ReleaseCheckResult.UpdateAvailable)]
    [InlineData("nightly", ReleaseCheckResult.Invalid)]
    public void Only_newer_releases_are_offered(string tag, ReleaseCheckResult expected) =>
        Assert.Equal(expected, UpdateRules.Evaluate(Release(tag), V("0.9.0"), Architecture.X64, out _));

    [Fact]
    public void An_offer_names_the_asset_for_this_architecture()
    {
        Assert.Equal(ReleaseCheckResult.UpdateAvailable, UpdateRules.Evaluate(Release(), V("0.9.0+abc"), Architecture.Arm64, out var offer));
        Assert.Equal(V("0.10.0"), offer!.Version);
        Assert.Equal("ClipboardManager-arm64.exe", offer.ExeName);
        Assert.Equal(900, offer.ExeSize);
        Assert.Equal(new Uri(Download + "ClipboardManager-arm64.exe"), offer.ExeUrl);
        Assert.Equal(new Uri(Download + "SHA256SUMS.txt"), offer.ChecksumsUrl);
        Assert.Equal(new Uri("https://github.com/Secoolioo/clipboard-manager/releases/tag/v0.10.0"), offer.ReleasePage);
    }

    [Fact]
    public void Prereleases_drafts_and_releases_without_a_usable_asset_are_not_installed()
    {
        Assert.Equal(ReleaseCheckResult.UpToDate, UpdateRules.Evaluate(Release(prerelease: true), V("0.9.0"), Architecture.X64, out _));
        Assert.Equal(ReleaseCheckResult.MissingAsset, UpdateRules.Evaluate(Release(), V("0.9.0"), Architecture.X86, out _));
        Assert.Equal(ReleaseCheckResult.MissingAsset, UpdateRules.Evaluate(Release(size: 0), V("0.9.0"), Architecture.X64, out _));
        Assert.Equal(ReleaseCheckResult.MissingAsset, UpdateRules.Evaluate(Release(exeUrl: "http://github.com/x/ClipboardManager.exe"), V("0.9.0"), Architecture.X64, out _));
        Assert.Equal(ReleaseCheckResult.MissingAsset, UpdateRules.Evaluate(Release(exeUrl: "https://example.com/ClipboardManager.exe"), V("0.9.0"), Architecture.X64, out _));
    }

    [Fact]
    public void The_release_page_from_the_answer_is_only_opened_when_it_is_on_github()
    {
        UpdateRules.Evaluate(Release(htmlUrl: "file:///C:/Windows/System32/calc.exe"), V("0.9.0"), Architecture.X64, out var offer);
        Assert.Equal(new Uri(UpdateRules.ReleasesPage), offer!.ReleasePage);
    }

    [Theory]
    [InlineData("https://api.github.com/repos/Secoolioo/clipboard-manager/releases/latest", true)]
    [InlineData("https://github.com/Secoolioo/clipboard-manager/releases/download/v1/ClipboardManager.exe", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/1", true)]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1?sig=x", true)]
    [InlineData("https://GITHUB.com/x", true)]
    [InlineData("http://github.com/x", false)]
    [InlineData("https://github.com:8443/x", false)]
    [InlineData("https://user:pass@github.com/x", false)]
    [InlineData("https://github.com.example.org/x", false)]
    [InlineData("https://evilgithub.com/x", false)]
    [InlineData("https://raw.githubusercontent.com/x", false)]
    [InlineData("ftp://github.com/x", false)]
    public void Only_https_github_hosts_are_contacted(string url, bool allowed) =>
        Assert.Equal(allowed, UpdateRules.IsAllowed(new Uri(url)));

    [Fact]
    public void Checksum_files_in_text_and_binary_mode_are_read()
    {
        var a = new string('a', 64);
        var b = "B" + new string('0', 63);
        var sums = Sha256Sums.Parse($"\uFEFF{a}  ClipboardManager.exe\r\n{b} *ClipboardManager-arm64.exe\r\n\r\n# comment\nnot a hash  x.exe\n{a} ClipboardManager-x64.zip\n");

        Assert.Equal(a, sums["ClipboardManager.exe"]);
        Assert.Equal(b, sums["ClipboardManager-arm64.exe"], ignoreCase: true);
        Assert.False(sums.ContainsKey("x.exe"));
        Assert.False(sums.ContainsKey("ClipboardManager-x64.zip"));
        Assert.Equal(2, sums.Count);
    }

    [Fact]
    public void A_file_listed_with_two_different_hashes_is_not_trusted()
    {
        var sums = Sha256Sums.Parse($"{new string('a', 64)}  ClipboardManager.exe\n{new string('b', 64)}  ClipboardManager.exe\n");
        Assert.False(sums.ContainsKey("ClipboardManager.exe"));
    }

    [Fact]
    public void The_updater_handoff_argument_is_parsed()
    {
        Assert.Equal(4242, StartupOptions.ParseUpdatedFrom(["--updated-from", "4242"]));
        Assert.Equal(7, StartupOptions.ParseUpdatedFrom(["--autostart", "--UPDATED-FROM", "7"]));
        Assert.Null(StartupOptions.ParseUpdatedFrom(["--updated-from"]));
        Assert.Null(StartupOptions.ParseUpdatedFrom(["--updated-from", "-5"]));
        Assert.Null(StartupOptions.ParseUpdatedFrom(["--updated-from", "abc"]));
        Assert.Null(StartupOptions.ParseUpdatedFrom([]));
    }
}
