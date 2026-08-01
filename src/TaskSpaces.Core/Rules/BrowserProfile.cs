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
}
