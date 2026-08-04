using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Rehydration;

// THE content-based membership key (spec: "every app may belong to workspace A or B,
// depending on what's being shown"). rider64.exe X.sln and rider64.exe Y.sln are
// different identities; two chrome windows of the same profile are the same identity.
public static class RosterIdentity
{
    // Chromium browsers spray session-specific arguments (--restore-session, flag
    // switches...) that vary run to run — only --profile-directory identifies content.
    // Firefox is deliberately NOT here: it has no --profile-directory (that's Chromium
    // syntax) — its profile is -P/-profile, which BrowserProfile doesn't parse. Routing
    // Firefox through this profile-only path would collapse EVERY Firefox window to the
    // same identity regardless of profile. Leaving it out of this set means it falls
    // through to the generic path+args identity below, where -P work vs -P home differ
    // naturally — exactly the content-based-identity goal from the spec.
    static readonly IReadOnlySet<string> Browsers =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "chrome", "msedge", "brave", "vivaldi", "opera" };

    public static string Of(string processPath, string? commandLine)
    {
        var exe = Path.GetFileNameWithoutExtension(processPath);
        var content = Browsers.Contains(exe)
            ? BrowserContent(commandLine)
            : CommandLines.ArgumentsOf(commandLine, processPath);
        return $"{processPath.ToLowerInvariant()}|{content.ToLowerInvariant()}";
    }

    // Profile, plus the PWA/app id when there is one. The app id is what stops an installed
    // web app collapsing into the plain browser: Petre's YouTube Music runs as msedge on the
    // Default profile, so on profile alone it shared one identity with all four of his
    // ordinary Edge windows -- and since the roster maps identity -> ONE workspace, whichever
    // of the five was placed last owned the lot.
    static string BrowserContent(string? commandLine) =>
        BrowserProfile.FromCommandLine(commandLine).Map(profile => $"profile:{profile}").GetValueOrDefault("")
        + BrowserProfile.AppFromCommandLine(commandLine).Map(app => $"|app:{app}").GetValueOrDefault("");

    public static string Of(InventoryEntry entry) => Of(entry.ProcessPath, entry.CommandLine);

    // A window with no readable process path (elevated) can't be identified or relaunched.
    public static Maybe<string> Of(WindowInfo window) =>
        window.ProcessPath is null ? Maybe<string>.None : Of(window.ProcessPath, window.CommandLine);

    // "Running anywhere counts": Rider-on-X sitting in ANOTHER workspace still means
    // Start must not launch a duplicate of it.
    public static bool IsRunning(InventoryEntry entry, IEnumerable<WindowInfo> windows) =>
        windows.Any(w => Of(w).Map(id => id == Of(entry)).GetValueOrDefault(false));
}
