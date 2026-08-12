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
// So a wildcard rule is the FALLBACK for apps whose title shape nothing can read. The pattern and the
// name are now SEPARATE INPUTS rather than one string with syntax in it: "not the arrow. i want two
// separate boxes - one for the title wildcard, another for the new name." An arrow was tried first and
// he was right to reject it -- parsing a separator out of free text means a title that contains one
// gets misread, and there is no reason to invent a syntax for something a dialog can just ask twice.
//
// So the derivation below stays for what it was written for -- one box, name taken from the pattern --
// and the two-box form needs nothing from this class beyond ToRegex.
public static class RenamePattern
{
    // A plain rename (no wildcard) keeps the existing exact-title behaviour, so nothing about
    // the old flow changes unless the user actually types a '*'.
    public static bool IsWildcard(string input) => input.Contains('*');

    // Glob to anchored regex. Everything except '*' is escaped, so a title full of regex
    // metacharacters -- "Remote Desktop Manager [server-01 - fhd]" is nothing but brackets and
    // dashes -- cannot turn into an accidental pattern or a crash.
    public static string ToRegex(string input) =>
        "^" + string.Join(".*", input.Trim().Split('*').Select(Regex.Escape)) + "$";

    // The literal part of the pattern, which is what the taskbar will show when no name was given
    // separately. Separators left stranded by the removed wildcard go too: "beeper | *" should name the
    // window "beeper", not "beeper |".
    public static string ShortNameOf(string input) =>
        input.Replace("*", "").Trim().Trim('|', '-', '–', ':', '·').Trim();
}
