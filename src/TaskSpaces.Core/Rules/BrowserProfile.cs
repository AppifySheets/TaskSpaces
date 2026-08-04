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
}
