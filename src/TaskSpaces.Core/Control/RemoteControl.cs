using System.Text.Json;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Control;

// Commands from OUTSIDE the app: another process asking the running TaskSpaces to do what its menus do.
//
// Petre: "i want you to add ability to create workspaces and move windows into them programatically, so
// that when i have a claude session start working in a new worktree, i can tell it to move the windows
// to a new workspace which it creates." Then: "expose all the commands that are available via context
// menu", "moving existing workspaces, renaming, etc."
//
// Every command calls the SAME WorkspaceManager method its menu item calls (ControlUsage names the menu
// item beside each one), so a remote rename or reorder obeys exactly the rules a click does, and the bar
// redraws from the same StateChanged. Nothing here keeps state of its own.
//
// Split by menu into partial files: workspaces (RemoteControl.Workspaces.cs), groups
// (RemoteControl.Groups.cs) and windows (RemoteControl.Windows.cs). This file holds the dispatch and the
// lookups they share.
//
// Runs on the app's UI thread, always: the manager is not thread-safe and its COM calls are STA. The
// pipe server is responsible for getting here through the dispatcher (see ControlPipeServer).
//
// Output is JSON for every success, because the expected caller is an agent that reads it back, and a
// plain sentence for every failure, because the expected reader of a failure is whoever is debugging.
public sealed partial class RemoteControl(WorkspaceManager manager, Func<bool, Result>? setBarVisible = null)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ControlReply Execute(IReadOnlyList<string> args) =>
        args.Count == 0
            ? ControlReply.Fail(ControlUsage.Text)
            : Dispatch(args[0].ToLowerInvariant(), args.Skip(1).ToArray());

    // One arm per command, matched on the verb AND the shape of its arguments, so a command given the
    // wrong number of them falls through to the arm that prints that command's own usage line.
    ControlReply Dispatch(string verb, string[] rest) => (verb, rest) switch
    {
        ("workspaces", []) => Workspaces(),
        ("windows", []) => Windows(),
        ("desktops", []) => Desktops(),

        ("create", _) => ControlArguments.ParseCreate(rest).Match(Create, ControlReply.Fail),
        ("move", _) => ControlArguments.ParseMove(rest).Match(Move, ControlReply.Fail),

        ("rename", [var workspace, var name]) => Rename(workspace, name),
        ("add-child", [var parent, var name]) => AddChild(parent, name),
        ("reorder", [var workspace, var where]) => Reorder(workspace, where),
        ("minimize-row", [var workspace]) => SetRowMinimized(workspace, true),
        ("restore-row", [var workspace]) => SetRowMinimized(workspace, false),
        ("color", [var workspace, var colour]) => Colour(workspace, colour),
        ("delete", [var workspace]) => Delete(workspace, closeWindows: false),
        ("delete", [var workspace, "--close-windows"]) => Delete(workspace, closeWindows: true),
        ("switch", [var workspace]) => Switch(workspace),

        ("group-create", [var name, var first]) => GroupCreate(name, first),
        ("group-join", [var workspace, var group]) => GroupJoin(workspace, group),
        ("group-leave", [var workspace]) => GroupLeave(workspace),
        ("group-rename", [var group, var name]) => GroupRename(group, name),
        ("group-color", [var group, var colour]) => GroupColour(group, colour),
        ("ungroup", [var group]) => Ungroup(group),

        ("name-desktop", [var desktop, var name]) => NameDesktop(desktop, name),

        ("rename-window", [var hwnd, var name]) => RenameWindow(hwnd, name),
        ("rename-app", [var hwnd, var name]) => RenameApp(hwnd, name),
        ("rename-pattern", [var pattern, var name]) => RenamePattern(pattern, name),
        ("name-by-folder", [var hwnd, var onOff]) => NameByFolder(hwnd, onOff),
        ("restore-title", [var hwnd]) => RestoreTitle(hwnd),
        ("pin", [var hwnd]) => Pin(hwnd, pin: true),
        ("unpin", [var hwnd]) => Pin(hwnd, pin: false),

        ("bar", [var state]) => Bar(state),

        ("help" or "--help" or "-h" or "/?", _) => new ControlReply(ControlReply.Ok, ControlUsage.Text),
        _ when ControlUsage.IsVerb(verb) => ControlReply.Fail($"Wrong arguments for {verb}.\n{ControlUsage.LineFor(verb)}"),
        _ => ControlReply.Fail($"unknown command '{verb}'.\n\n{ControlUsage.Text}"),
    };

    // --- lookups shared by every command ------------------------------------------------------

    // Names are compared the way the manager compares them for collisions (trimmed, ignoring case), so
    // "find" and "already exists" can never disagree. An id works too, for a caller holding one from a
    // listing, since two workspaces can be renamed into each other's old names between calls.
    Maybe<Workspace> FindWorkspace(string nameOrId) =>
        manager.State.Workspaces.TryFirst(w => Matches(w.Name, w.Id, nameOrId));

    Result<Workspace> WorkspaceNamed(string nameOrId) =>
        FindWorkspace(nameOrId).ToResult($"There is no workspace named '{nameOrId}'. `workspaces` lists them.");

    Result<Group> GroupNamed(string nameOrId) =>
        manager.State.Groups.TryFirst(g => Matches(g.Name, g.Id, nameOrId)).ToResult(
            manager.State.Groups.Count == 0
                ? $"There is no group named '{nameOrId}'; there are no groups yet. group-create makes one."
                : $"There is no group named '{nameOrId}'. The groups are: {string.Join(", ", manager.State.Groups.Select(g => g.Name))}.");

    // Only windows the app knows: a handle it has never seen is either closed or not a taskbar window,
    // and nothing the menus do applies to either.
    Result<WindowInfo> WindowAt(string raw) =>
        ControlArguments.Handle(raw).Bind(handle => manager.KnownWindows.TryFirst(w => w.Handle == handle)
            .ToResult($"No open window has handle {raw}. `windows` lists them."));

    static bool Matches(string name, Guid id, string nameOrId) =>
        name.Trim().Equals(nameOrId.Trim(), StringComparison.OrdinalIgnoreCase)
        || (Guid.TryParse(nameOrId, out var asId) && asId == id);

    // --- what a reply says about things --------------------------------------------------------

    // A workspace as every command reports it, read back from the state AFTER the change, so the reply
    // is where it actually ended up rather than where it was asked to go.
    object Describe(Guid workspaceId)
    {
        var workspace = manager.State.Workspaces.First(w => w.Id == workspaceId);
        var group = manager.State.GroupOf(workspaceId);
        return new
        {
            name = workspace.Name,
            id = workspace.Id,
            group = group?.Name,
            row = manager.RowOf(workspaceId).GetValueOrDefault(),
            // Position inside the group's box, from 1, for a grouped workspace: the number `reorder`
            // takes for it.
            groupPosition = group is null ? (int?)null : manager.State.MembersOf(group.Id).ToList().FindIndex(m => m.Id == workspaceId) + 1,
            color = workspace.Color,
            minimized = workspace.Minimized,
            current = manager.CurrentWorkspaceId == workspaceId,
        };
    }

    object DescribeGroup(Guid groupId)
    {
        var group = manager.State.Groups.First(g => g.Id == groupId);
        return new
        {
            name = group.Name,
            id = group.Id,
            color = group.Color,
            members = manager.State.MembersOf(groupId).Select(m => m.Name),
            row = manager.State.MembersOf(groupId).Select(m => manager.RowOf(m.Id).GetValueOrDefault()).FirstOrDefault(),
        };
    }

    static string Hex(WindowHandle handle) => $"0x{(long)handle.Value:X}";

    static ControlReply Success(object value) => new(ControlReply.Ok, Serialize(value));

    static ControlReply Reply(Result result, Func<object> describe) =>
        result.IsSuccess ? Success(describe()) : ControlReply.Fail(result.Error);

    static string Serialize(object value) => JsonSerializer.Serialize(value, Json);
}
