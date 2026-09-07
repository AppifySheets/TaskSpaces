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
    // switches...) that vary run to run -- only --profile-directory identifies content.
    // Firefox is deliberately NOT here: it has no --profile-directory (that's Chromium
    // syntax) -- its profile is -P/-profile, which BrowserProfile doesn't parse. Routing
    // Firefox through this profile-only path would collapse EVERY Firefox window to the
    // same identity regardless of profile. Leaving it out of this set means it falls
    // through to the generic path+args identity below, where -P work vs -P home differ
    // naturally -- exactly the content-based-identity goal from the spec.
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
    // ...and the profile ROOT when the browser was told to use one of its own, which is Petre's Chrome
    // report: "opening chrome in a workspace, i think claude opened it, opened up in my current
    // workspace, not its own workspace." An automated Chrome runs out of its own --user-data-dir and
    // passes no --profile-directory -- and on his machine neither does the Chrome he starts himself, so
    // every Chrome window was the identity "chrome.exe|" and memory, which stands down when another
    // live window shares an identity, could never place any of them. The directory separates the
    // automated browser from his own; BrowserProfile normalises away its per-session suffix so the
    // separation survives from one session to the next.
    static string BrowserContent(string? commandLine) =>
        BrowserProfile.FromCommandLine(commandLine).Map(profile => $"profile:{profile}").GetValueOrDefault("")
        + BrowserProfile.AppFromCommandLine(commandLine).Map(app => $"|app:{app}").GetValueOrDefault("")
        + BrowserProfile.UserDataDirFromCommandLine(commandLine).Map(dir => $"|dir:{dir}").GetValueOrDefault("");

    public static string Of(InventoryEntry entry) => Of(entry.ProcessPath, entry.CommandLine);

    // The SHELL, which is the one identity here that LIES.
    //
    // Petre: "run window opens in llc workspace" / "should open in current". Measured before anything
    // was changed, because the app looked innocent -- no move appears in the trace, since placement
    // memory is the one tier that does not write a line. His state.json held
    //
    //   Inventory[LLC] = { ProcessPath: "C:\WINDOWS\Explorer.EXE",
    //                      CommandLine: "C:\WINDOWS\Explorer.EXE", Title: "Run" }
    //
    // and the live Win+R dialog sat on LLC's desktop (bdb0b172-...) while he was on GEPHA. Explorer
    // builds a FRESH dialog per Win+R -- consecutive ones were 0x5C1174 then 0x41124A -- so each was
    // appearing on the current desktop and being moved off it within milliseconds. Hence "it just
    // opens in that workspace": there is no visible move, only a window that was never where it was
    // made.
    //
    // One path with no arguments runs the desktop, the taskbar, every File Explorer folder window and
    // that dialog, so "which workspace does C:\WINDOWS\explorer.exe live in" has no answer -- and the
    // answer it was given got applied to whichever of those windows appeared next.
    //
    // LaunchedBy refuses the shell for the same reason and says so in its own words: it "owns File
    // Explorer windows, so treating it as a launcher would place a newly started app wherever a folder
    // window happened to be". That was the launcher half. This is the identity half, which was missing.
    //
    // Nothing usable is lost. Every explorer window shares this single identity, so memory could never
    // tell a Downloads folder from a Run box, and the rule that memory stands down when another live
    // window shares an identity already switched it off whenever two explorer windows were open. What
    // remains is only the case where exactly one was -- a coin toss that moved whatever that happened
    // to be.
    //
    // Kept to explorer, deliberately. The rest of LaunchedBy's NotLaunchers list is there to end a
    // process walk early; those processes own no window this app tracks, so adding them here would be
    // a claim about windows nobody has measured. ApplicationFrameHost is the one worth naming: it
    // frames Store app windows, and on this machine WhatsApp and Teams are rostered under their own
    // paths, so the frame is not what the window reports and it does not belong here either.
    static readonly IReadOnlySet<string> Shell =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "explorer" };

    public static bool IsShell(string processPath) =>
        Shell.Contains(Path.GetFileNameWithoutExtension(processPath));

    // No identity, so nothing can be remembered about the window and nothing can be re-applied to it.
    // Two ways to get here: a window with no readable process path (elevated), and the shell.
    public static Maybe<string> Of(WindowInfo window) =>
        window.ProcessPath is null || IsShell(window.ProcessPath)
            ? Maybe<string>.None
            : Of(window.ProcessPath, window.CommandLine);

    // "Running anywhere counts": Rider-on-X sitting in ANOTHER workspace still means
    // Start must not launch a duplicate of it.
    public static bool IsRunning(InventoryEntry entry, IEnumerable<WindowInfo> windows) =>
        windows.Any(w => Of(w).Map(id => id == Of(entry)).GetValueOrDefault(false));
}
