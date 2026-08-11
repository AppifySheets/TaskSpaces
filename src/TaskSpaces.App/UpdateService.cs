using System.IO;
using System.Net.Http;
using System.Reflection;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Updates;

namespace TaskSpaces.App;

// The network half of "check for updates" (#71). Everything that DECIDES anything is in
// TaskSpaces.Core.UpdateCheck, which is why this file has almost no branching in it: fetch text,
// hand it over, compare. That split is what makes the awkward cases testable -- a malformed tag, a
// release with no exe, a hostile URL -- without a network anywhere near a test.
//
// Petre's ground rules, and they shape every line below: fail silently offline, never block the UI,
// and never self-replace. This class can therefore only ever return an answer or nothing; it has no
// way to report a problem, on purpose.
public static class UpdateService
{
    // The releases/latest endpoint of this repo, which is also the RepositoryUrl in
    // Directory.Build.props. Hard-coded rather than read from the assembly: this is the one place
    // the app talks to the internet, and where it talks to should be visible in the source rather
    // than assembled at runtime from metadata a build could change.
    const string LatestRelease = "https://api.github.com/repos/AppifySheets/TaskSpaces/releases/latest";

    // Where to send someone when the API cannot be reached at all (#110's manual check): the same
    // repo, its human-readable half. A separate constant beside the endpoint rather than derived from
    // it, because deriving one URL from another by string surgery is how a browser ends up opening
    // api.github.com.
    public const string ReleasesPage = "https://github.com/AppifySheets/TaskSpaces/releases/latest";

    // One client for the process. HttpClient is designed to be shared -- a new one per call leaks
    // sockets in TIME_WAIT -- and this one is used at most once a day.
    //
    // The User-Agent is REQUIRED, not politeness: GitHub's API answers 403 to a request without
    // one, so its absence would look exactly like "no update available, for ever".
    //
    // The timeout is short because nothing waits on this. A check that takes ten seconds and one
    // that fails are the same event to a user who is not looking.
    static readonly HttpClient Client = Build();

    static HttpClient Build()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Add("User-Agent", $"TaskSpaces/{RunningVersion}");
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        return client;
    }

    // What this build calls itself. InformationalVersion rather than AssemblyVersion, because
    // AssemblyVersion is deliberately pinned at 1.0.0.0 so servicing releases never break a
    // reference (see Directory.Build.props) -- reading it would compare every release against
    // 1.0.0.0 and announce an update for ever.
    //
    // It can carry a "+commit" suffix when built from source; UpdateCheck.Parse drops that.
    public static string RunningVersion =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? "0.0.0";

    // The same answer, fit to show someone (#82). Reading it from the assembly rather than from a
    // constant is the point of the issue: with portable versioned exes sitting side by side, the
    // window has to report the build it is actually running in, not the one the source last named.
    public static string DisplayVersion => UpdateCheck.ForDisplay(RunningVersion);

    // The newer release, or nothing at all.
    //
    // Result<Maybe<T>> rather than Result<T?> or an exception: "the check failed" and "the check
    // succeeded and you are up to date" are genuinely different outcomes, and only the second one
    // should stop anything retrying tomorrow. Both are silent to the user either way.
    public static async Task<Result<Maybe<ReleaseInfo>>> NewerThanRunningAsync(CancellationToken cancel = default)
    {
        try
        {
            var response = await Client.GetAsync(LatestRelease, cancel).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Result.Failure<Maybe<ReleaseInfo>>($"release check returned {(int)response.StatusCode}");

            var payload = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);

            return UpdateCheck.ReadRelease(payload)
                .Map(release => UpdateCheck.IsNewer(RunningVersion, release.Version)
                    ? Maybe<ReleaseInfo>.From(release)
                    : Maybe<ReleaseInfo>.None);
        }
        // Every one of these is "no answer today", which is not a problem worth telling anyone
        // about: no network, a proxy in the way, DNS down, the machine asleep mid-request, or the
        // app shutting down while the call was in flight. Petre: "fail silently offline."
        catch (HttpRequestException e)
        {
            return Result.Failure<Maybe<ReleaseInfo>>(e.Message);
        }
        catch (TaskCanceledException e)
        {
            return Result.Failure<Maybe<ReleaseInfo>>(e.Message);
        }
    }

    // Petre: "it needs to download the new release next to the current executable... if i do, then
    // it should download the new one and restart to the new one."
    //
    // NEXT TO the running exe, never over it. The app is portable and updating it means gaining a
    // second file, not losing the first: the running exe cannot be replaced while it is running
    // anyway, and a user who dislikes the new version still has the old one sitting beside it.
    //
    // Returns the path of the downloaded file.
    public static async Task<Result<string>> DownloadAsync(ReleaseInfo release, CancellationToken cancel = default)
    {
        if (release.AssetName is null || !UpdateCheck.IsDownloadable(release.AssetUrl))
            return Result.Failure<string>("this release has no downloadable executable");

        // Re-checked here rather than trusted because it came from ReadRelease. The value has
        // travelled through a UI layer to get here, and the cost of being wrong is running a
        // binary from wherever the payload said.
        if (Path.GetDirectoryName(Environment.ProcessPath) is not { } folder)
            return Result.Failure<string>("cannot work out where this executable lives");

        var target = Path.Combine(folder, release.AssetName);

        // Downloaded to a PART FILE and moved into place only once it is complete. A half-written
        // exe left by a dropped connection would otherwise sit there looking exactly like a
        // finished one, and the next thing this flow does is execute it.
        var partial = target + ".part";

        try
        {
            using (var response = await Client.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                    return Result.Failure<string>($"download returned {(int)response.StatusCode}");

                // Streamed rather than buffered: the asset is ~75 MB, and ReadAsByteArrayAsync
                // would hold all of it in memory before a byte reaches disk.
                await using var source = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
                await using var file = File.Create(partial);
                await source.CopyToAsync(file, cancel).ConfigureAwait(false);
            }

            // Overwrites a previous download of the same version -- someone who declined the
            // restart last time should not accumulate a folder of identical exes.
            File.Move(partial, target, overwrite: true);
            return target;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            // The portable case is a user folder and writable; the read-only case is an exe in
            // Program Files, where this is the expected outcome rather than a fault. Either way the
            // caller falls back to the release page.
            Cleanup(partial);
            return Result.Failure<string>(e.Message);
        }
    }

    static void Cleanup(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* a leftover .part is untidy, not harmful, and never executed */ }
        catch (UnauthorizedAccessException) { }
    }
}
