using DockSense.Core;

namespace DockSense.Core.Tests;

public sealed class UpdatesTests
{
    private const string Digest = "ed327d8919a2a9118ee2869109ec362e143dc98eb8c4ceaf48b26722152e44eb";
    private const string DownloadUrl = "https://github.com/rozx/DockSense/releases/download/v0.3/DockSense-0.3-win-x64.zip";

    // The shape of GitHub's GET /repos/{owner}/{repo}/releases/latest response, cut down to what is read.
    private const string Feed = """
        {
          "html_url": "https://github.com/rozx/DockSense/releases/tag/v0.3",
          "tag_name": "v0.3",
          "draft": false,
          "prerelease": false,
          "assets": [
            {
              "name": "DockSense-0.3-win-arm64.zip",
              "size": 60941368,
              "digest": "sha256:735be7d501e0f1f0f9051d173debc4844c72395b57181cd2e930ec4d05f9df1f",
              "browser_download_url": "https://github.com/rozx/DockSense/releases/download/v0.3/DockSense-0.3-win-arm64.zip"
            },
            {
              "name": "DockSense-0.3-win-x64.zip",
              "size": 65473208,
              "digest": "sha256:ED327D8919A2A9118EE2869109EC362E143DC98EB8C4CEAF48B26722152E44EB",
              "browser_download_url": "https://github.com/rozx/DockSense/releases/download/v0.3/DockSense-0.3-win-x64.zip"
            }
          ]
        }
        """;

    [Theory]
    [InlineData("v0.2", 0, 2, 0)]
    [InlineData("0.2.1", 0, 2, 1)]
    [InlineData("V1.2.3", 1, 2, 3)]
    [InlineData("0.2.1+f2e36fe7728453c5f7e7ff898c18a0f86f4e4317", 0, 2, 1)]
    [InlineData("0.2.1.0", 0, 2, 1)]
    [InlineData(" 0.3 ", 0, 3, 0)]
    public void Release_versions_parse_to_three_parts(string text, int major, int minor, int patch)
    {
        Assert.Equal(new Version(major, minor, patch), ReleaseVersion.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("3")]
    [InlineData("1.0-beta")]
    [InlineData("nightly")]
    public void Other_text_is_not_a_release_version(string? text)
    {
        Assert.Null(ReleaseVersion.Parse(text));
    }

    [Fact]
    public void Two_part_and_three_part_versions_compare_as_expected()
    {
        Assert.Equal(ReleaseVersion.Parse("0.2"), ReleaseVersion.Parse("0.2.0"));
        Assert.True(ReleaseVersion.Parse("0.2.1") > ReleaseVersion.Parse("0.2"));
        Assert.True(ReleaseVersion.Parse("0.10") > ReleaseVersion.Parse("0.9.9"));
    }

    [Fact]
    public void Versions_format_like_the_release_tags()
    {
        Assert.Equal("0.2", ReleaseVersion.Format(new Version(0, 2, 0)));
        Assert.Equal("0.2.1", ReleaseVersion.Format(new Version(0, 2, 1)));
    }

    [Fact]
    public void The_feed_gives_the_version_page_and_assets_with_digests()
    {
        var release = ReleaseFeed.Parse(Feed);

        Assert.NotNull(release);
        Assert.Equal(new Version(0, 3, 0), release.Version);
        Assert.Equal("https://github.com/rozx/DockSense/releases/tag/v0.3", release.PageUrl);
        Assert.Equal(2, release.Assets.Count);
        Assert.Equal(new ReleaseAsset("DockSense-0.3-win-x64.zip", DownloadUrl, 65473208, Digest), release.Assets[1]);
    }

    [Theory]
    [InlineData("\"draft\": false", "\"draft\": true")]
    [InlineData("\"prerelease\": false", "\"prerelease\": true")]
    [InlineData("\"tag_name\": \"v0.3\"", "\"tag_name\": \"nightly\"")]
    public void Drafts_prereleases_and_other_tags_are_not_releases(string from, string to)
    {
        Assert.Null(ReleaseFeed.Parse(Feed.Replace(from, to)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"message\": \"API rate limit exceeded\"}")]
    public void A_response_that_is_not_a_release_is_ignored(string json)
    {
        Assert.Null(ReleaseFeed.Parse(json));
    }

    [Fact]
    public void A_page_outside_the_repository_is_replaced_by_the_releases_page()
    {
        var release = ReleaseFeed.Parse(Feed.Replace("https://github.com/rozx/DockSense/releases/tag/v0.3", "https://example.com/x"));

        Assert.Equal(UpdatePlanner.ReleasesUrl, release!.PageUrl);
    }

    [Fact]
    public void A_malformed_digest_leaves_the_asset_without_one()
    {
        var release = ReleaseFeed.Parse(Feed.Replace("sha256:ED327D", "md5:ED327D"));

        Assert.Null(release!.Assets[1].Sha256);
    }

    [Fact]
    public void A_newer_release_is_offered_with_the_download_for_this_architecture()
    {
        var release = ReleaseFeed.Parse(Feed);

        var x64 = UpdatePlanner.Offer(release, new Version(0, 2, 1), "x64");
        var arm64 = UpdatePlanner.Offer(release, new Version(0, 2, 1), "arm64");

        Assert.Equal(new Version(0, 3, 0), x64!.Version);
        Assert.Equal("DockSense-0.3-win-x64.zip", x64.Asset!.Name);
        Assert.Equal("DockSense-0.3-win-arm64.zip", arm64!.Asset!.Name);
    }

    [Theory]
    [InlineData("0.3")]
    [InlineData("0.3.1")]
    [InlineData("1.0")]
    public void The_same_or_an_older_release_is_not_offered(string current)
    {
        Assert.Null(UpdatePlanner.Offer(ReleaseFeed.Parse(Feed), ReleaseVersion.Parse(current)!, "x64"));
        Assert.Null(UpdatePlanner.Offer(null, ReleaseVersion.Parse(current)!, "x64"));
    }

    [Fact]
    public void A_release_without_a_verifiable_download_is_offered_without_an_asset()
    {
        var current = new Version(0, 2, 1);
        var asset = new ReleaseAsset("DockSense-0.3-win-x64.zip", DownloadUrl, 1000, Digest);

        ReleaseInfo With(ReleaseAsset changed) => new(new Version(0, 3, 0), UpdatePlanner.ReleasesUrl, [changed]);

        Assert.NotNull(UpdatePlanner.Offer(With(asset), current, "x64")!.Asset);
        Assert.Null(UpdatePlanner.Offer(With(asset with { Sha256 = null }), current, "x64")!.Asset);
        Assert.Null(UpdatePlanner.Offer(With(asset with { Size = 0 }), current, "x64")!.Asset);
        Assert.Null(UpdatePlanner.Offer(With(asset with { Size = UpdatePlanner.MaxDownloadBytes + 1 }), current, "x64")!.Asset);
        Assert.Null(UpdatePlanner.Offer(With(asset with { Name = "DockSense-0.3-linux-x64.zip" }), current, "x64")!.Asset);
        Assert.Null(UpdatePlanner.Offer(With(asset with { DownloadUrl = "https://example.com/DockSense-0.3-win-x64.zip" }), current, "x64")!.Asset);
    }

    [Fact]
    public void The_release_list_gives_every_real_release()
    {
        var older = Feed.Replace("v0.3", "v0.2.1").Replace("\"prerelease\": false", "\"prerelease\": false ");
        var draft = Feed.Replace("v0.3", "v0.4").Replace("\"draft\": false", "\"draft\": true");

        var releases = ReleaseFeed.ParseAll($"[{draft},{Feed},{older}]");

        Assert.Equal([new Version(0, 3, 0), new Version(0, 2, 1)], releases.Select(r => r.Version));
        Assert.Empty(ReleaseFeed.ParseAll("not json"));
        Assert.Empty(ReleaseFeed.ParseAll(Feed)); // one release is not a list
    }

    [Fact]
    public void A_downloaded_zip_is_matched_to_its_release_even_when_a_newer_one_exists()
    {
        const string newerDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        ReleaseInfo Release(string version, string digest) => new(
            ReleaseVersion.Parse(version)!,
            UpdatePlanner.ReleasesUrl,
            [new ReleaseAsset($"DockSense-{version}-win-x64.zip", DownloadUrl, 1000, digest)]);
        ReleaseInfo[] releases = [Release("0.4", newerDigest), Release("0.3", Digest), Release("0.2.1", "bbbb")];

        // The zip of 0.3, downloaded before 0.4 came out, is still a release newer than 0.2.1.
        Assert.Equal(new Version(0, 3, 0), UpdatePlanner.FindRelease(releases, new Version(0, 2, 1), "x64", Digest.ToUpperInvariant())!.Version);
        Assert.Equal(new Version(0, 4, 0), UpdatePlanner.FindRelease(releases, new Version(0, 2, 1), "x64", newerDigest)!.Version);

        // Not newer than what is installed, another architecture, or no release at all.
        Assert.Null(UpdatePlanner.FindRelease(releases, new Version(0, 3, 0), "x64", Digest));
        Assert.Null(UpdatePlanner.FindRelease(releases, new Version(0, 2, 1), "arm64", Digest));
        Assert.Null(UpdatePlanner.FindRelease(releases, new Version(0, 2, 1), "x64", "cccc"));
        Assert.Null(UpdatePlanner.FindRelease([], new Version(0, 2, 1), "x64", Digest));
    }

    [Theory]
    [InlineData("https://github.com/rozx/DockSense/releases/download/v0.3/DockSense-0.3-win-x64.zip", true)]
    [InlineData("HTTPS://GitHub.com/rozx/DockSense/releases/download/v0.3/a.zip", true)]
    [InlineData("http://github.com/rozx/DockSense/releases/download/v0.3/a.zip", false)]
    [InlineData("https://github.com/someone/DockSense/releases/download/v0.3/a.zip", false)]
    [InlineData("https://github.com/rozx/DockSense-evil/releases/download/v0.3/a.zip", false)]
    [InlineData("https://github.com.evil.example/rozx/DockSense/releases/download/v0.3/a.zip", false)]
    [InlineData("https://github.com/rozx/DockSense/archive/main.zip", false)]
    [InlineData("file:///C:/a.zip", false)]
    [InlineData(null, false)]
    public void Only_release_downloads_of_this_repository_are_fetched(string? url, bool expected)
    {
        Assert.Equal(expected, UpdatePlanner.IsDownloadUrl(url));
    }

    [Fact]
    public void A_check_is_due_once_a_day()
    {
        var now = new DateTimeOffset(2026, 11, 13, 19, 0, 0, TimeSpan.Zero);

        Assert.True(UpdatePlanner.IsCheckDue(null, now));
        Assert.False(UpdatePlanner.IsCheckDue(now.AddHours(-23), now));
        Assert.True(UpdatePlanner.IsCheckDue(now.AddHours(-24), now));
        Assert.True(UpdatePlanner.IsCheckDue(now.AddDays(2), now)); // the clock went backwards
    }

    [Fact]
    public void Updates_default_to_notify_and_survive_a_round_trip()
    {
        Assert.Equal(UpdateMode.Notify, new DockSenseConfig().Updates);

        var path = Path.Combine(Path.GetTempPath(), $"docksense-test-{Guid.NewGuid():N}.json");
        try
        {
            var store = new ConfigStore(path);
            store.Save(new DockSenseConfig { Updates = UpdateMode.Automatic });

            Assert.Contains("\"updates\": \"Automatic\"", File.ReadAllText(path));
            Assert.Equal(UpdateMode.Automatic, store.Load().Updates);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
