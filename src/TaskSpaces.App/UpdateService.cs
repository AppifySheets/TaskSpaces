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
}
