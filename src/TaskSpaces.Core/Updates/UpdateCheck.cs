using System.Text.Json;
using CSharpFunctionalExtensions;

namespace TaskSpaces.Core.Updates;

// One release of the app, as much of it as this app has any use for (#71).
//
// AssetName and AssetUrl are BOTH null or both set. The app is portable -- a single exe -- so the
// exe is the download, and a release without one can still be announced and linked but cannot be
// fetched. Downstream reads "no asset" as "link only", which is also what an asset that failed a
// safety check becomes.
public sealed record ReleaseInfo(string Version, string PageUrl, string? AssetName, string? AssetUrl);

// Petre: "check for updates -- tell the user a new version exists and offer a link to the new
// file... may also download the new version and offer to start it... never silently self-replace."
//
// Everything that DECIDES lives here: whether a tag is newer, and what a release payload means.
// The HTTP call on top does nothing but fetch text, which is what keeps the awkward cases (a
// malformed tag, a release with no exe, a hostile URL) testable without a network.
//
// The governing rule throughout: anything this cannot understand means NO UPDATE. A check that
// runs in the background and gets it wrong in the "yes" direction sends someone off to download
// something; getting it wrong in the "no" direction costs a day until the next check. The two
// mistakes are not the same size.
public static class UpdateCheck
{
    public static bool IsNewer(string? running, string? latest) =>
        Parse(running) is { } current && Parse(latest) is { } candidate && candidate > current;

    // A version as it should be shown to a person (#82: the app's version in the Manage window).
    //
    // Only the BUILD METADATA is dropped, the "+commit" the SDK appends to InformationalVersion when
    // building from source. A prerelease suffix stays: "1.8.0-beta" is a different thing to be running
    // than "1.8.0" and hiding that in the one place the question gets asked would be a lie of
    // omission. Comparison drops both (see Parse), for a different reason: there it is about ordering,
    // and there is no ordering of "-beta" against "-rc2" worth inventing.
    //
    // Empty rather than null when there is nothing to show, so a caller can bind it straight to a
    // label and get a blank rather than the word "null" in a corner of the window.
    public static string ForDisplay(string? version) =>
        string.IsNullOrWhiteSpace(version) ? "" : version.Trim().Split('+')[0];

    // Tolerant on purpose, because the two sides come from different worlds and neither is under
    // this method's control: the running version is an assembly attribute ("1.6.0", sometimes
    // four-part "1.6.0.0"), and the latest is whatever text someone typed into a git tag
    // ("v1.7.0", "1.7", "1.7.0-beta").
    //
    // NORMALISED TO FOUR PARTS before comparing, which is the whole point. System.Version orders
    // 1.6.0 below 1.6.0.0 -- an unspecified component is -1, not 0 -- so comparing a three-part
    // Version against a four-part one would report the same release as an update on every check,
    // for ever.
    //
    // A suffix after '-' or '+' is DROPPED rather than ranked. There is no ordering for
    // "-beta" against "-rc2" that survives contact with someone's naming scheme, and inventing
    // one would be this file quietly deciding what a pre-release means.
    static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var trimmed = text.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V')) trimmed = trimmed[1..];
        trimmed = trimmed.Split('-', '+')[0];

        return Version.TryParse(trimmed, out var parsed)
            ? new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0))
            : null;
    }

    // GitHub's releases/latest payload, reduced to the four things worth having.
    //
    // Hand-read rather than deserialized into a mirror of GitHub's schema: three fields are wanted
    // out of a response with dozens, and a DTO would have to be kept in step with a shape this app
    // does not own. JsonDocument also refuses malformed input by throwing, which is exactly the
    // answer wanted for "the network returned something that is not a release".
    public static Result<ReleaseInfo> ReadRelease(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            // The LIST endpoint returns an array. Asking for a property on it would throw, but
            // saying so plainly beats a caught exception carrying no explanation.
            if (root.ValueKind != JsonValueKind.Object) return Result.Failure<ReleaseInfo>("not a release object");

            var version = Text(root, "tag_name");
            var page = Text(root, "html_url");

            // Both are load-bearing and neither has a sane default: with no tag there is nothing
            // to compare, and with no page there is nowhere to send anybody.
            if (version is null) return Result.Failure<ReleaseInfo>("release has no tag_name");
            if (page is null) return Result.Failure<ReleaseInfo>("release has no html_url");
            if (!IsHttp(page)) return Result.Failure<ReleaseInfo>($"release url is not http(s): {page}");

            // The first .exe attached, if any. First rather than best-guess: a release carrying an
            // x64 and an arm64 is a choice for a human on the release page, and being wrong about
            // WHICH binary to run is worse than not offering to run one.
            var asset = root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array
                ? assets.EnumerateArray().FirstOrDefault(a =>
                    Text(a, "name") is { } name && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                : default;

            var assetName = asset.ValueKind == JsonValueKind.Object ? Text(asset, "name") : null;
            var assetUrl = asset.ValueKind == JsonValueKind.Object ? Text(asset, "browser_download_url") : null;

            // Both dropped together unless both survive their checks, so "there is an asset" is one
            // question downstream rather than two that can disagree.
            return IsSafeAsset(assetName, assetUrl)
                ? new ReleaseInfo(version, page, assetName, assetUrl)
                : new ReleaseInfo(version, page, null, null);
        }
        catch (JsonException e)
        {
            return Result.Failure<ReleaseInfo>($"unreadable release payload: {e.Message}");
        }
    }

    static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() is { } text && !string.IsNullOrWhiteSpace(text) ? text : null
            : null;

    // These two strings arrive from the network and end up as an argument to Process.Start and as
    // part of a file path next to the running exe. That makes them the app's only untrusted input
    // that can name something to execute, so both are checked HERE, where it is testable, rather
    // than at the call sites where one would eventually be forgotten.
    static bool IsSafeAsset(string? name, string? url) =>
        name is not null && url is not null && IsGitHubDownload(url) && IsBareFileName(name);

    static bool IsHttp(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    // The asset url is held to a higher standard than the page url, because they are used
    // differently: the page is handed to a browser, while THIS one is downloaded and then
    // executed. So it must be https and it must be GitHub.
    //
    // Not paranoia about GitHub itself -- it is where the release came from -- but about the
    // release payload being able to point the download anywhere at all. A tag whose asset url
    // said "https://example.invalid/x.exe" would otherwise have this app fetch and run it.
    //
    // github.com is where browser_download_url points; the *.githubusercontent.com hosts are where
    // it REDIRECTS, so both have to be acceptable or a redirect-following download would refuse
    // the file it was sent to.
    static bool IsGitHubDownload(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    // Public so the downloader can re-check the url it is about to follow rather than trusting that
    // it came from here unmodified.
    public static bool IsDownloadable(string? url) => url is not null && IsGitHubDownload(url);

    // Whether this release can actually be installed, which is not the same question as whether it
    // exists (#144's aftermath). Petre, seconds after a release was published: "when checking for new
    // version, if there's no exe attached to the release, don't say there's a new version and suggest
    // that the user goes and downloads it manually, wait for the exe."
    //
    // The gap is REAL and routine rather than a freak case: publishing a release is what STARTS the
    // build that attaches its executable, so for the two or three minutes that build takes, the release
    // is on GitHub with nothing on it. With a check every five minutes, landing in that window is
    // ordinary. A release still building is not a release you can take, so the honest answer is to say
    // nothing and ask again on the next tick, by which time the exe is there.
    public static bool CanDownload(ReleaseInfo release) =>
        release.AssetName is not null && IsDownloadable(release.AssetUrl);

    // A name and nothing else: no directory part, no drive, no traversal. The download lands
    // BESIDE the running exe, so a name carrying "..\..\" would choose where.
    static bool IsBareFileName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && name == Path.GetFileName(name)
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !name.Contains("..", StringComparison.Ordinal);
}
