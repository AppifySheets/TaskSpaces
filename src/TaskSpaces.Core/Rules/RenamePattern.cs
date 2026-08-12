using System.Text.RegularExpressions;

namespace TaskSpaces.Core.Rules;

// Petre: "when renaming, i want to have the ability to specify a wildcard instead of the full
// window name, so 'beeper | work chat' I'd change this to say 'beeper *' which would
// match all beepers and still rename to beeper".
//
// WHY it matters: a manual rename is stored as a PersistedRename keyed on the EXACT title the
// window had when it was renamed, so it stops applying the moment the app rewrites its own
// title. Beeper puts the current chat in its title and Remote Desktop Manager puts the
// session, so both of Petre's renames were guaranteed to lapse. A wildcard turns one rename
// into a rule that keeps working.
//
// The single input does two jobs, which is the neat part of Petre's design:
//   "beeper *"                -> matches any title starting "beeper", names it "beeper"
//   "* - Visual Studio Code"  -> matches any title ending that way, names it "Visual Studio Code"
// The name is simply the pattern with the wildcard removed, so there is no second field to
// fill in and nothing to keep in sync.
// Later, once folder naming existed (#136), Petre drew the line between the two: "when it comes to
// window renames, i think it would be better to rename windows by wildcard, so i'd say *taskspace*
// => TaskSpace, only when I don't have a way to automatically assign the correct name to the app."
//
// So a wildcard rule is the FALLBACK for apps whose title shape nothing can read, and the arrow in
// that sentence is the syntax it asked for: deriving the name from the pattern cannot express
// "match loosely, name precisely". "*taskspace*" would name a window "taskspace", and what he wants
// on the taskbar is "TaskSpace".
public static class RenamePattern
{
    // What separates the pattern from the name it should produce. Spelled the way he wrote it.
    const string Arrow = "=>";

    // Whether this input is a RULE rather than a one-off rename of one window's exact title. Either
    // form makes it one: a wildcard has to be a rule to mean anything, and naming a match explicitly
    // is a statement about every window that matches, not about the one in front of you.
    public static bool IsRule(string input) => IsWildcard(input) || input.Contains(Arrow, StringComparison.Ordinal);

    // A plain rename (no wildcard) keeps the existing exact-title behaviour, so nothing about
    // the old flow changes unless the user actually types a '*'.
    public static bool IsWildcard(string input) => input.Contains('*');

    // Glob to anchored regex. Everything except '*' is escaped, so a title full of regex
    // metacharacters -- "Remote Desktop Manager [server-01 - fhd]" is nothing but brackets and
    // dashes -- cannot turn into an accidental pattern or a crash.
    public static string ToRegex(string input) =>
        "^" + string.Join(".*", PatternOf(input).Split('*').Select(Regex.Escape)) + "$";

    // The match side of the arrow, or the whole input when there is no arrow.
    public static string PatternOf(string input) =>
        input.IndexOf(Arrow, StringComparison.Ordinal) is var at && at >= 0 ? input[..at].Trim() : input.Trim();

    // What the taskbar will show: the name after the arrow when there is one, and otherwise the
    // literal part of the pattern, which is the original single-field design. Separators left
    // stranded by the removed wildcard go too: "beeper | *" should name the window "beeper", not
    // "beeper |".
    public static string ShortNameOf(string input) =>
        input.IndexOf(Arrow, StringComparison.Ordinal) is var at && at >= 0
            ? input[(at + Arrow.Length)..].Trim()
            : input.Replace("*", "").Trim().Trim('|', '-', '–', ':', '·').Trim();
}
