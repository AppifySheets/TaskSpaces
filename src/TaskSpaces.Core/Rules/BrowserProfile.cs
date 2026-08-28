using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;

namespace TaskSpaces.Core.Rules;

// Chromium browsers (Chrome/Edge/Brave/Vivaldi) expose the active profile only via
// the process command line: --profile-directory=Default or --profile-directory="Profile 2".
public static partial class BrowserProfile
{
    [GeneratedRegex("""--profile-directory=(?:"(?<q>[^"]+)"|(?<u>\S+))""")]
    private static partial Regex ProfileDirectory();

    public static Maybe<string> FromCommandLine(string? commandLine) =>
        commandLine is not null && ProfileDirectory().Match(commandLine) is { Success: true } m
            ? m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["u"].Value
            : Maybe<string>.None;

    // A Chromium "app" window -- an installed PWA (--app-id=<id>) or a URL shortcut
    // (--app=<url>). Petre's YouTube Music is one of these: process msedge, class
    // Chrome_WidgetWin_1, indistinguishable from a browser window by profile alone.
    //
    // It matters for IDENTITY. YouTube Music is a different app from the browser in every
    // sense that counts here -- its own icon, its own place in a workspace -- but it shares
    // the profile, so on profile alone the two collapse into one identity and the roster can
    // only remember ONE workspace for both. Including the app id separates them, which is
    // exactly the spec's content-based identity goal ("every app may belong to workspace A or
    // B, depending on what's being shown").
    [GeneratedRegex("""--app(?:-id)?=(?:"(?<q>[^"]+)"|(?<u>\S+))""")]
    private static partial Regex AppId();

    public static Maybe<string> AppFromCommandLine(string? commandLine) =>
        commandLine is not null && AppId().Match(commandLine) is { Success: true } m
            ? m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["u"].Value
            : Maybe<string>.None;

    // Where the browser keeps its profiles, when it has been told to keep them somewhere other than
    // the default. Petre: "opening chrome in a workspace, i think claude opened it, opened up in my
    // current workspace, not its own workspace... but it has worked correctly in the past."
    //
    // It is the same argument the app id above makes, one level out. A Chrome that an automation tool
    // starts runs from its own user-data-dir with no --profile-directory at all -- and measured on
    // Petre's machine, neither does any Chrome HE starts, so all fifteen live processes shared the one
    // identity "chrome.exe|". Placement memory stands down whenever another live window shares an
    // identity (see WorkspaceManager.Remembered), so the roster entry he had taught for the automated
    // Chrome could never be applied while any Chrome window was open, which is always. The trail of
    // successes in the trace log came from the launched-by tier instead, which only fires when he
    // happens to be standing somewhere other than the launcher's workspace.
    //
    // Read AFTER the profile rather than instead of it: a machine can have both, and they are two
    // different questions ("which profile root" and "which profile inside it").
    [GeneratedRegex("""--user-data-dir=(?:"(?<q>[^"]+)"|(?<u>\S+))""")]
    private static partial Regex UserDataDir();

    public static Maybe<string> UserDataDirFromCommandLine(string? commandLine) =>
        commandLine is not null && UserDataDir().Match(commandLine) is { Success: true } m
            ? Stable(m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["u"].Value)
            : Maybe<string>.None;

    // A per-session suffix stripped off the last segment, because the raw path is NOT stable: Playwright
    // MCP makes a fresh directory per session (mcp-chrome-829010e, then mcp-chrome-5adf218), so keying
    // identity on it verbatim would hand every session a brand new identity and remember nothing at all
    // -- the same failure, by the opposite route.
    //
    // Deliberately narrow, because the cost of over-stripping is merging two profiles a person named by
    // hand: a trailing dash, then four or more characters that are alphanumeric and include a digit.
    // That is what a random token looks like; "chrome-beta" keeps its name because it has no digit, and
    // "chrome-2" because two characters is not a token. The rule is a heuristic and says so; what makes
    // it safe is that it only ever runs on a directory the user explicitly passed.
    [GeneratedRegex(@"-(?=[A-Za-z0-9]{4,}$)[A-Za-z0-9]*[0-9][A-Za-z0-9]*$")]
    private static partial Regex SessionSuffix();

    static string Stable(string userDataDir) => SessionSuffix().Replace(userDataDir.TrimEnd('\\', '/'), "");
}
