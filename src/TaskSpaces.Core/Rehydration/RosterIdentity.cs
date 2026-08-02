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
    static readonly IReadOnlySet<string> Browsers =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "chrome", "msedge", "firefox", "brave", "vivaldi", "opera" };

    public static string Of(string processPath, string? commandLine)
    {
        var exe = Path.GetFileNameWithoutExtension(processPath);
        var content = Browsers.Contains(exe)
            ? BrowserProfile.FromCommandLine(commandLine).Map(p => $"profile:{p}").GetValueOrDefault("")
            : CommandLines.ArgumentsOf(commandLine, processPath);
        return $"{processPath.ToLowerInvariant()}|{content.ToLowerInvariant()}";
    }

    public static string Of(InventoryEntry entry) => Of(entry.ProcessPath, entry.CommandLine);

    // A window with no readable process path (elevated) can't be identified or relaunched.
    public static Maybe<string> Of(WindowInfo window) =>
        window.ProcessPath is null ? Maybe<string>.None : Of(window.ProcessPath, window.CommandLine);

    // "Running anywhere counts": Rider-on-X sitting in ANOTHER workspace still means
    // Start must not launch a duplicate of it.
    public static bool IsRunning(InventoryEntry entry, IEnumerable<WindowInfo> windows) =>
        windows.Any(w => Of(w).Map(id => id == Of(entry)).GetValueOrDefault(false));
}
