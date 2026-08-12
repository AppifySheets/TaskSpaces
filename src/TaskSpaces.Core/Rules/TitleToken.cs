using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;

namespace TaskSpaces.Core.Rules;

// Petre: "if a vscode window has a title 'filename - TaskSpaces' you should pay attention to
// the TaskSpaces when assigning", and "so i open vscode, then i load a folder in it, that
// should take it to the correct workspace".
//
// WHY this exists at all: placement is otherwise keyed on RosterIdentity (exe path + args),
// and an app launched WITHOUT arguments has one identity no matter what it has loaded. Three
// VS Code windows started from the Start menu are indistinguishable that way -- which is
// exactly what collapsed all three of Petre's into one workspace. The container name in the
// title is the only per-window signal available, so this extracts it.
//
// ALLOWLIST, never a blocklist. The failure modes are asymmetric: an app we do not recognise
// simply keeps today's behaviour, whereas an app we wrongly recognise silently misplaces
// windows. Same reasoning (and same shape) as RosterIdentity's browser set.
//
// Deliberately EXCLUDED, per Petre:
//   - Browsers. "browser tabs are a bad way to identify which tab to assign it to" -- a tab
//     title is the page, not a container, and it changes on every navigation. The honest fix
//     for browsers is UIA reading the address bar (already parked in the spec as a spike),
//     not a cleverer title heuristic.
//   - Single-window apps (Beeper, WhatsApp, Slack, Teams, Spotify). One window means one
//     identity means one workspace, which placement memory already handles perfectly. Title
//     learning would add risk and no value.
public static class TitleToken
{
    // How an app arranges its title. Not cosmetic -- the container is at OPPOSITE ends for
    // VS Code and for JetBrains IDEs, so one "take the last segment" rule cannot serve both.
    enum Shape
    {
        TrailingContainer, // "file - Container - App Name"      (VS Code, Visual Studio)
        LeadingContainer,  // "Container – file – branch"        (JetBrains, note the EN DASH)
        Bracketed,         // "App Name [Container]"             (Remote Desktop Manager)
    }

    static readonly IReadOnlyDictionary<string, Shape> Apps =
        new Dictionary<string, Shape>(StringComparer.OrdinalIgnoreCase)
        {
            // VS Code and its forks: "index.ts - Corne-Config - Visual Studio Code"
            ["code"] = Shape.TrailingContainer,
            ["cursor"] = Shape.TrailingContainer,
            ["vscodium"] = Shape.TrailingContainer,
            ["windsurf"] = Shape.TrailingContainer,
            // "Program.cs - TaskSpaces - Microsoft Visual Studio"
            ["devenv"] = Shape.TrailingContainer,

            // JetBrains put the PROJECT FIRST and separate with an en dash:
            // "TaskSpaces – Program.cs"
            ["rider64"] = Shape.LeadingContainer,
            ["idea64"] = Shape.LeadingContainer,
            ["pycharm64"] = Shape.LeadingContainer,
            ["webstorm64"] = Shape.LeadingContainer,
            ["goland64"] = Shape.LeadingContainer,
            ["phpstorm64"] = Shape.LeadingContainer,
            ["clion64"] = Shape.LeadingContainer,
            ["datagrip64"] = Shape.LeadingContainer,
            ["studio64"] = Shape.LeadingContainer,

            // Petre's own: "Remote Desktop Manager [server-01 - fhd]" -- the session is
            // bracketed, and note it contains a " - " of its own, which is precisely why the
            // bracket rule has to run BEFORE any splitting.
            ["remotedesktopmanager"] = Shape.Bracketed,
        };

    // Trailing app names to discard before taking the container. Without this, VS Code's
    // container would always come out as "Visual Studio Code".
    static readonly IReadOnlyList<string> AppNameTails =
        ["Visual Studio Code", "Microsoft Visual Studio", "Visual Studio", "Cursor", "VSCodium", "Windsurf"];

    // Split on the delimiter WITH its surrounding spaces, never on the bare character.
    // "Corne-Config" is one word and splitting on a lone '-' would shred it into
    // "Corne" and "Config" -- the exact folder name Petre needs to match on.
    static readonly Regex Delimiters = new(@" - | – | \| ", RegexOptions.Compiled);

    static readonly Regex Bracketed = new(@"\[(?<container>[^\]]+)\]", RegexOptions.Compiled);

    // The container a window currently has loaded, or None when there is nothing to learn:
    // an app we do not track, or a window with no container yet. The second case matters --
    // a freshly opened VS Code is titled just "Visual Studio Code", and returning None for
    // it is what lets the folder-load a moment later be the thing that places the window.
    public static Maybe<string> For(string processName, string title)
    {
        if (string.IsNullOrWhiteSpace(title) || !Apps.TryGetValue(processName, out var shape))
            return Maybe<string>.None;

        if (shape == Shape.Bracketed)
            return Bracketed.Match(title) is { Success: true } match
                ? Clean(match.Groups["container"].Value)
                : Maybe<string>.None;

        var segments = Delimiters.Split(title)
            .Select(segment => segment.Trim())
            .Where(segment => segment.Length > 0)
            .Where(segment => !AppNameTails.Contains(segment, StringComparer.OrdinalIgnoreCase))
            .ToList();

        // Fewer than two segments means the title carries a name but no container: a bare
        // "Visual Studio Code", or "Untitled-1 - Visual Studio Code" with no folder open.
        // Guessing there would learn a FILE name as though it were a project.
        return segments.Count < 2
            ? Maybe<string>.None
            : Clean(shape == Shape.TrailingContainer ? segments[^1] : segments[0]);
    }

    // A one-character container is noise, not a name worth matching windows on.
    static Maybe<string> Clean(string container) =>
        container.Trim() is { Length: > 1 } cleaned ? cleaned : Maybe<string>.None;

    // Whether this app's title shape is known at all, which is what decides where "Name by folder"
    // is offered (#134). An app not on the list has no container to name a window after, so offering
    // it would be a menu item that silently does nothing.
    public static bool Knows(string processName) => Apps.ContainsKey(processName);

    // The container as a NAME, for a taskbar button. Petre: "folder name, but only the last part."
    //
    // A no-op for the ordinary case, which is why it is safe: VS Code shows the folder name alone, so
    // "TaskSpaces" is already the last part of itself. It earns its keep when the title carries a PATH
    // instead -- a window.title setting that includes the folder path, a JetBrains project shown with
    // its location -- where "C:\repos\bitcoin\dice-to-seed" should name the window "dice-to-seed"
    // rather than filling the taskbar with a path.
    //
    // Both separators, because a WSL or SSH remote in the title uses forward slashes. A trailing
    // separator is dropped first, or a path written "C:\repos\dice\" would come out as nothing.
    public static string LastPart(string container) =>
        container.TrimEnd('\\', '/').Split('\\', '/') is { Length: > 0 } parts && parts[^1].Length > 0
            ? parts[^1]
            : container;

    // Does this window's current content match a token we learned earlier? Case-insensitive
    // containment rather than equality: VS Code shows the folder name verbatim, but other
    // apps decorate it (a trailing "[Administrator]", a branch suffix), and a learned token
    // should survive that.
    public static bool Matches(string processName, string title, string token) =>
        For(processName, title).Map(container => container.Contains(token, StringComparison.OrdinalIgnoreCase)).GetValueOrDefault(false);
}
