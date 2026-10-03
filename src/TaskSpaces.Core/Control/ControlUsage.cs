namespace TaskSpaces.Core.Control;

// The one description of every remote-control command. `help` prints it, a command given the wrong
// arguments prints its own line from it, and the README points here rather than repeating it, so what
// the running app says it accepts and what it actually accepts cannot drift apart.
//
// Grouped the way the bar's menus are, because "expose all the commands that are available via context
// menu" (Petre) is the brief: each section names the menu it mirrors.
public static class ControlUsage
{
    public sealed record Command(string Verb, string Arguments, string Description);

    public sealed record Section(string Title, IReadOnlyList<Command> Commands);

    public static readonly IReadOnlyList<Section> Sections =
    [
        new("Listing", [
            new("workspaces", "", "every workspace as JSON: name, group, row, colour, minimized, current"),
            new("windows", "", "every window as JSON: hwnd, title, originalTitle, process, workspace"),
            new("desktops", "", "every virtual desktop as JSON, including unnamed ones"),
        ]),
        new("Creating, and moving windows", [
            new("create", "<name> [placement]", "create a workspace; already existing is not an error"),
            new("move", "<workspace> [--create [placement]] [--no-follow] (--title <text> | --hwnd <handle>)...",
                "move windows; every selector must match or nothing moves (exit 3). Moving the window you are\n"
                + "      in takes you along, as a drag does; --no-follow leaves you where you are"),
        ]),
        new("Workspace row menu", [
            new("rename", "<workspace> <new name>", "Rename…"),
            new("add-child", "<parent> <name>", "Add child… (nests a new workspace that borrows the parent's windows)"),
            new("reorder", "<workspace> up|down|top|end|<n>", "Move up / down / to top / to end, or to position n among its peers"),
            new("minimize-row", "<workspace>", "Minimize row"),
            new("restore-row", "<workspace>", "Restore row height"),
            new("color", "<workspace> <colour>", "Colour (a grouped workspace sets its group's colour)"),
            new("delete", "<workspace> [--close-windows]", "Delete workspace…; refuses while windows are open unless --close-windows"),
            new("switch", "<workspace>", "go to that workspace, as clicking its row does"),
        ]),
        new("Group menu", [
            new("group-create", "<group name> <first workspace>", "New group…"),
            new("group-join", "<workspace> <group>", "Move into group (joins at the bottom)"),
            new("group-leave", "<workspace>", "Move out of group"),
            new("group-rename", "<group> <new name>", "Rename group…"),
            new("group-color", "<group> <colour>", "the group's colour"),
            new("ungroup", "<group>", "Ungroup (members keep their places)"),
        ]),
        new("Unnamed desktop menu", [
            new("name-desktop", "<desktop id or number from desktops> <name>", "Name this desktop…"),
        ]),
        new("Window icon menu", [
            new("rename-window", "<hwnd> <short name>", "Rename this window…"),
            new("rename-app", "<hwnd> <short name>", "Rename all <app> windows…"),
            new("rename-pattern", "<title pattern> <short name>", "Rename by title pattern… (* is a wildcard)"),
            new("name-by-folder", "<hwnd> on|off", "Name <app> windows by folder"),
            new("restore-title", "<hwnd>", "Restore title"),
            new("pin", "<hwnd>", "show the window on every workspace, as dropping it on the pin row does"),
            new("unpin", "<hwnd>", "undo pin"),
        ]),
        new("Bar", [
            new("bar", "minimize|restore", "Minimize bar, or bring it back"),
        ]),
    ];

    public static IEnumerable<Command> All => Sections.SelectMany(s => s.Commands);

    public static bool IsVerb(string verb) => All.Any(c => c.Verb == verb);

    public static string LineFor(string verb) =>
        All.Where(c => c.Verb == verb).Select(c => $"usage: TaskSpaces ctl {c.Verb} {c.Arguments}".TrimEnd()).FirstOrDefault() ?? "";

    const string Footer =
        """
        placement, for create and move --create (at most one; none means the end of the bar):
          --group <group>         the bottom of that group
          --position <n>          top-level row n of the bar, a whole group counting as one row
          --before <workspace>    just before it, joining its group if it has one
          --after <workspace>     just after it, the same way

        --wait <seconds>          on any command: retry while the answer is exit 3 (a window not open yet)

        Workspaces and groups are named as they appear on the bar (case does not matter). Windows are
        named by the hwnd that `windows` prints. Colours: a palette name (indigo, plum, steel, violet,
        teal, orchid, amber, slate, rust), #RRGGBB, none (transparent) or default (by position).

        exit codes: 0 done, 1 refused, 2 TaskSpaces is not running, 3 no window matched
        """;

    public static string Text { get; } =
        "usage: TaskSpaces ctl <command> [arguments]\n\n"
        + string.Join("\n", Sections.Select(s =>
            $"{s.Title}:\n" + string.Join("\n", s.Commands.Select(c => $"  {(c.Verb + " " + c.Arguments).TrimEnd()}\n      {c.Description}"))))
        + "\n\n" + Footer;
}
