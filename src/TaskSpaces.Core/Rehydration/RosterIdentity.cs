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

    // Apps whose windows open WHERE THEY WERE OPENED FROM, and are never sent anywhere by memory.
    //
    // Petre: "some of the windows always need to open in the current workspace where they originate,
    // like browsers", and then "when i say current, i mean the one where they're opened from."
    //
    // What these have in common is that the app says nothing about the work. A browser window becomes
    // whatever page is loaded into it, a terminal whatever directory it is in, a file picker belongs to
    // whichever app asked for it. So "which workspace does msedge live in" has no answer, for the same
    // reason "which workspace does explorer live in" has none (see Shell above), and an answer learned
    // from one window was being applied to the next: one Edge window dragged to a row taught the roster
    // that Edge lives there, and the first Edge window opened after that went to that row from
    // wherever he was.
    //
    // Treated exactly like the shell: no identity. That one change does all of it, because every piece
    // of memory hangs off the identity. Memory has nothing to place them by, a drag moves the window
    // without teaching the roster anything, and a hand pin pins that window without becoming a standing
    // order to pin every future one. Stale entries already in state.json become unreachable rather
    // than needing a migration.
    //
    // What still moves them is the launched-by tier (#94), which is the other half of "where they're
    // opened from": a browser started by an editor goes to the editor's workspace. That tier keys on
    // the process chain, not on identity, so it is untouched by this. Workspace rules are untouched as
    // well; a rule is an instruction Petre wrote himself.
    //
    // Every name here was measured in the trace log on his machine as a window that appears often,
    // rather than added because it sounded plausible:
    //
    //   browsers   chrome, msedge, brave, vivaldi, opera, firefox. A Chromium automated browser (its own
    //              --user-data-dir, Playwright's) is included on purpose: it is started by the session
    //              that drives it, so launched-by already sends it to that session's workspace, and
    //              memory pulling it to the workspace of some EARLIER session was the wrong answer.
    //   terminals  WindowsTerminal, plus the classic console hosts for a window started outside it.
    //   pickers    PickerHost (the Open/Save dialog) and CredentialUIBroker (the Windows Security
    //              sign-in prompt). The system starts these, not the app that asked, so not even the
    //              launched-by walk can connect them; staying put keeps them in front of the asker.
    //   Taskmgr    a tool you open to look at the machine from wherever you are.
    //
    // Deliberately NOT included: Settings and other Store apps, because they report through
    // ApplicationFrameHost and nobody has measured what their windows say (see the note on the Shell
    // list above).
    static readonly IReadOnlySet<string> StayWhereOpened = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "brave", "vivaldi", "opera", "firefox",
        "WindowsTerminal", "OpenConsole", "conhost", "cmd", "powershell", "pwsh",
        "PickerHost", "CredentialUIBroker",
        "Taskmgr",
    };

    // An installed web app is the exception, and the reason a command line is needed at all. A PWA
    // window that runs as its own process carries --app-id, and it IS an app with a home: his YouTube
    // Music is msedge on the Default profile, and it belongs somewhere the way any app does. It keeps
    // its identity (see BrowserContent) and memory keeps placing it.
    //
    // A PWA opened while its browser was already running shares the browser's process and command
    // line (see CLAUDE.md), so it has no --app-id to find and stays put like a browser window. That is
    // no loss: memory could never tell it from the browser before this either.
    public static bool StaysWhereOpened(string processPath, string? commandLine)
    {
        var exe = Path.GetFileNameWithoutExtension(processPath);
        return StayWhereOpened.Contains(exe)
               && !(Browsers.Contains(exe) && BrowserProfile.AppFromCommandLine(commandLine).HasValue);
    }

    // No identity, so nothing can be remembered about the window and nothing can be re-applied to it.
    // Three ways to get here: a window with no readable process path (elevated), the shell, and an app
    // whose windows stay where they were opened.
    public static Maybe<string> Of(WindowInfo window) =>
        window.ProcessPath is null || IsShell(window.ProcessPath) || StaysWhereOpened(window.ProcessPath, window.CommandLine)
            ? Maybe<string>.None
            : Of(window.ProcessPath, window.CommandLine);

    // "Running anywhere counts": Rider-on-X sitting in ANOTHER workspace still means
    // Start must not launch a duplicate of it.
    public static bool IsRunning(InventoryEntry entry, IEnumerable<WindowInfo> windows) =>
        windows.Any(w => Of(w).Map(id => id == Of(entry)).GetValueOrDefault(false));
}
