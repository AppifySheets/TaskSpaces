using TaskSpaces.Core.Updates;

namespace TaskSpaces.Core.Tests;

// #71: "tell the user a new version exists and offer a link to the new file."
//
// Everything that DECIDES lives here rather than in the HTTP call, so the interesting cases --
// a malformed tag, a release with no exe, a downgrade, the version we are already running --
// are settled without a network or a running app anywhere near them. The service on top only
// fetches text and hands it to this.
//
// The governing rule for every ambiguous case: an update check that cannot understand what it
// got says NO UPDATE. It is a background nicety, and a wrong "yes" sends someone to download
// something; a wrong "no" costs a day until the next check.
public class UpdateCheckTests
{
    [Theory]
    [InlineData("1.6.0", "1.7.0")]
    [InlineData("1.6.0", "1.6.1")]
    [InlineData("1.6.0", "2.0.0")]
    // The tag on a GitHub release conventionally carries a leading v; the assembly version never
    // does. Comparing them as strings would make "v1.7.0" and "1.6.0" incomparable, so both sides
    // are normalised before anything is decided.
    [InlineData("1.6.0", "v1.7.0")]
    [InlineData("v1.6.0", "1.7.0")]
    // FileVersion is four-part and Version is three; the same release must not look newer than
    // itself because one side carries a trailing zero.
    [InlineData("1.6.0.0", "1.6.1")]
    [InlineData("1.6", "1.6.1")]
    public void A_higher_version_is_newer(string running, string latest) =>
        Assert.True(UpdateCheck.IsNewer(running, latest));

    [Theory]
    [InlineData("1.6.0", "1.6.0")]
    [InlineData("1.6.0", "v1.6.0")]
    [InlineData("1.6.0.0", "1.6.0")]
    [InlineData("1.6.0", "1.6.0.0")]
    public void The_version_we_are_running_is_not_newer(string running, string latest) =>
        Assert.False(UpdateCheck.IsNewer(running, latest));

    // Someone republishing an older release, or a tag that sorts oddly. Never offered.
    [Theory]
    [InlineData("1.7.0", "1.6.0")]
    [InlineData("2.0.0", "1.9.9")]
    public void An_older_version_is_not_newer(string running, string latest) =>
        Assert.False(UpdateCheck.IsNewer(running, latest));

    // A tag neither side can parse is not an argument for bothering anybody.
    [Theory]
    [InlineData("1.6.0", "nightly")]
    [InlineData("1.6.0", "")]
    [InlineData("1.6.0", "   ")]
    [InlineData("", "1.7.0")]
    [InlineData("1.6.0", "release-candidate")]
    public void An_unparseable_version_is_not_newer(string running, string latest) =>
        Assert.False(UpdateCheck.IsNewer(running, latest));

    // Pre-release suffixes are not ranked -- there is no ordering here that would survive
    // someone's naming scheme -- but they must not make the numeric part unreadable either.
    [Fact]
    public void A_suffix_is_ignored_rather_than_ranked()
    {
        Assert.True(UpdateCheck.IsNewer("1.6.0", "1.7.0-beta"));
        Assert.False(UpdateCheck.IsNewer("1.6.0", "1.6.0-beta"));
    }

    const string Payload = """
        {
          "tag_name": "v1.7.0",
          "html_url": "https://github.com/AppifySheets/TaskSpaces/releases/tag/v1.7.0",
          "assets": [
            { "name": "TaskSpaces-1.7.0.exe", "browser_download_url": "https://github.com/AppifySheets/TaskSpaces/releases/download/v1.7.0/TaskSpaces-1.7.0.exe" }
          ]
        }
        """;

    // The asset url is held to a higher standard than the page url, because they are used
    // differently: the page goes to a browser, the asset is downloaded and then EXECUTED. Anything
    // that is not GitHub over https is dropped -- otherwise a release payload could point this app
    // at any binary on the internet and have it fetched and run.
    [Theory]
    [InlineData("https://example.invalid/TaskSpaces.exe")]
    [InlineData("http://github.com/AppifySheets/TaskSpaces/x.exe")]   // http, not https
    [InlineData("https://github.com.evil.invalid/x.exe")]             // suffix trick
    [InlineData("https://notgithub.com/x.exe")]
    public void An_asset_hosted_anywhere_but_github_is_dropped(string url)
    {
        var release = UpdateCheck.ReadRelease($$"""
            {
              "tag_name": "v1.7.0",
              "html_url": "https://github.com/AppifySheets/TaskSpaces/releases/tag/v1.7.0",
              "assets": [ { "name": "TaskSpaces.exe", "browser_download_url": "{{url}}" } ]
            }
            """);

        Assert.True(release.IsSuccess);
        Assert.Null(release.Value.AssetUrl);
        Assert.False(UpdateCheck.IsDownloadable(url));
    }

    // github.com is where browser_download_url points; *.githubusercontent.com is where it
    // REDIRECTS. A downloader that follows redirects has to accept both or it would refuse the
    // very file it was sent to.
    [Theory]
    [InlineData("https://github.com/AppifySheets/TaskSpaces/releases/download/v1.7.0/x.exe")]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/x.exe")]
    public void Github_and_its_redirect_target_are_both_downloadable(string url) =>
        Assert.True(UpdateCheck.IsDownloadable(url));

    [Fact]
    public void A_release_yields_its_version_page_and_exe()
    {
        var release = UpdateCheck.ReadRelease(Payload);

        Assert.True(release.IsSuccess);
        Assert.Equal("v1.7.0", release.Value.Version);
        Assert.Equal("https://github.com/AppifySheets/TaskSpaces/releases/tag/v1.7.0", release.Value.PageUrl);
        Assert.Equal("TaskSpaces-1.7.0.exe", release.Value.AssetName);
        Assert.Equal("https://github.com/AppifySheets/TaskSpaces/releases/download/v1.7.0/TaskSpaces-1.7.0.exe", release.Value.AssetUrl);
    }

    // The app is portable -- one exe -- so the exe IS the download. A release carrying only
    // source archives is still worth announcing, and the link still works; it just cannot offer
    // to fetch and start anything, which is what a null asset means downstream.
    [Fact]
    public void A_release_with_no_exe_still_announces_itself()
    {
        var release = UpdateCheck.ReadRelease("""
            {
              "tag_name": "v1.7.0",
              "html_url": "https://example.invalid/tag/v1.7.0",
              "assets": [ { "name": "Source code.zip", "browser_download_url": "https://example.invalid/src.zip" } ]
            }
            """);

        Assert.True(release.IsSuccess);
        Assert.Null(release.Value.AssetName);
        Assert.Null(release.Value.AssetUrl);
    }

    // Several exes attached (an x64 and an arm64, say): take the first rather than guessing, and
    // leave choosing to the human on the release page. Being wrong about WHICH binary to run is
    // worse than not offering to run one.
    [Fact]
    public void The_first_exe_wins_when_a_release_carries_several()
    {
        var release = UpdateCheck.ReadRelease("""
            {
              "tag_name": "v1.7.0",
              "html_url": "https://example.invalid/tag/v1.7.0",
              "assets": [
                { "name": "TaskSpaces-x64.exe", "browser_download_url": "https://github.com/AppifySheets/TaskSpaces/releases/download/v1.7.0/x64.exe" },
                { "name": "TaskSpaces-arm64.exe", "browser_download_url": "https://github.com/AppifySheets/TaskSpaces/releases/download/v1.7.0/arm64.exe" }
              ]
            }
            """);

        Assert.Equal("TaskSpaces-x64.exe", release.Value.AssetName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{ }")]                                        // no tag, nothing to compare
    [InlineData("""{ "tag_name": "v1.7.0" }""")]               // no page to send anyone to
    [InlineData("""{ "html_url": "https://example.invalid" }""")]
    [InlineData("[]")]                                          // the LIST endpoint, not /latest
    public void Anything_unreadable_is_a_failure_rather_than_a_guess(string payload) =>
        Assert.True(UpdateCheck.ReadRelease(payload).IsFailure);

    // A payload is untrusted input from the network, and PageUrl/AssetUrl are handed to
    // Process.Start and to a downloader. A javascript: or file: tag would be a release note
    // choosing what this machine executes, so the scheme is checked HERE, where it is testable,
    // rather than at the call site where it would be easy to forget.
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("ftp://example.invalid/x")]
    [InlineData("not a url")]
    public void A_page_url_that_is_not_http_is_refused(string url) =>
        Assert.True(UpdateCheck.ReadRelease($$"""{ "tag_name": "v1.7.0", "html_url": "{{url}}" }""").IsFailure);

    [Fact]
    public void An_asset_url_that_is_not_http_is_dropped_without_losing_the_release()
    {
        var release = UpdateCheck.ReadRelease("""
            {
              "tag_name": "v1.7.0",
              "html_url": "https://example.invalid/tag/v1.7.0",
              "assets": [ { "name": "evil.exe", "browser_download_url": "file:///C:/Windows/System32/cmd.exe" } ]
            }
            """);

        Assert.True(release.IsSuccess);
        Assert.Null(release.Value.AssetUrl);
        Assert.Null(release.Value.AssetName);
    }

    // The downloaded file lands NEXT TO the running exe, so its name comes from the network and
    // is used to build a path. Anything that could climb out of that directory is refused.
    [Theory]
    [InlineData("../../evil.exe")]
    [InlineData(@"..\..\evil.exe")]
    [InlineData("C:/Windows/System32/evil.exe")]
    [InlineData("sub/dir/evil.exe")]
    public void An_asset_name_that_is_a_path_is_dropped(string name)
    {
        // Escaped, because a lone backslash is an escape character in JSON: pasting a Windows path
        // in raw makes the PAYLOAD malformed, and the test then passes for the wrong reason -- the
        // parse fails before the name is ever examined.
        var encoded = name.Replace("\\", "\\\\");

        var release = UpdateCheck.ReadRelease($$"""
            {
              "tag_name": "v1.7.0",
              "html_url": "https://example.invalid/tag/v1.7.0",
              "assets": [ { "name": "{{encoded}}", "browser_download_url": "https://github.com/AppifySheets/TaskSpaces/releases/download/v1.7.0/x.exe" } ]
            }
            """);

        Assert.True(release.IsSuccess);
        Assert.Null(release.Value.AssetName);
        Assert.Null(release.Value.AssetUrl);
    }
}
