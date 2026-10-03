using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Control;

// The workspace row menu, creation, desktops and the bar. See RemoteControl.cs for the shared parts.
public sealed partial class RemoteControl
{
    ControlReply Workspaces() =>
        Success(manager.State.Workspaces.Select(w => Describe(w.Id)));

    // Named or not, so an agent can reach the unnamed desktops the bar lists separately. `number` is the
    // 1-based position in Windows' own order, which is what name-desktop also accepts.
    ControlReply Desktops() =>
        manager.LiveDesktops().Match(
            live => Success(live.Select((d, at) => new
            {
                number = at + 1,
                id = d.Id,
                windowsName = d.Name,
                workspace = manager.State.Workspaces.FirstOrDefault(w => w.DesktopId == d.Id)?.Name,
                current = manager.State.Workspaces.FirstOrDefault(w => w.DesktopId == d.Id) is { } w && manager.CurrentWorkspaceId == w.Id,
            })),
            ControlReply.Fail);

    // Idempotent on purpose. An agent that runs its setup twice, or a worktree whose name happens to
    // match a workspace Petre already has, both mean "make sure it exists", and failing them would only
    // teach the caller to ignore errors from this command. An existing workspace is left WHERE IT IS
    // whatever placement was asked for, and the reply's group and row say where that is.
    ControlReply Create(CreateRequest request) =>
        Ensure(request.Name, create: true, request.Placement).Match(
            found => Success(new { created = found.Created, workspace = Describe(found.Workspace.Id) }),
            ControlReply.Fail);

    Result<(Workspace Workspace, bool Created)> Ensure(string name, bool create, Placement placement) =>
        FindWorkspace(name) is { HasValue: true } existing
            ? (existing.Value, false)
            : create
                ? Add(name, placement).Map(w => (w, true))
                : Result.Failure<(Workspace, bool)>($"There is no workspace named '{name}'. Pass --create to make it.");

    // Each placement is the manager call the matching bar gesture makes: the bottom of a group is where
    // "Move into group" puts a joiner, and before/after is the row menu's "Insert before/after…", which
    // inserts at the target's list index and at the target's depth.
    Result<Workspace> Add(string name, Placement placement) => placement switch
    {
        Placement.InGroup inGroup => GroupNamed(inGroup.Group).Bind(group => manager.AddWorkspaceToGroup(name, group.Id)),
        Placement.AtRow atRow => manager.AddWorkspaceAtRow(name, atRow.Row),
        Placement.Beside beside => WorkspaceNamed(beside.Workspace).Bind(target => manager.InsertWorkspace(
            name,
            manager.State.Workspaces.ToList().FindIndex(w => w.Id == target.Id) + (beside.After ? 1 : 0),
            manager.State.GroupOf(target.Id)?.Id)),
        _ => manager.AddWorkspace(name),
    };

    ControlReply Rename(string workspace, string name) =>
        WorkspaceNamed(workspace).Match(
            w => Reply(manager.RenameWorkspace(w.Id, name), () => Describe(w.Id)),
            ControlReply.Fail);

    ControlReply AddChild(string parent, string name) =>
        WorkspaceNamed(parent).Bind(p => manager.AddChildWorkspace(p.Id, name)).Match(
            child => Success(new { created = true, workspace = Describe(child.Id) }),
            ControlReply.Fail);

    // The row menu's four moves, plus an exact position. Every one is among PEERS, as the menu's are
    // (#85): a grouped workspace moves inside its group's box, an ungrouped one among the bar's top-level
    // rows, and a whole group by reordering a member is not a thing. "end" passes the list's length, the
    // same number the menu passes, which the manager clamps to the last peer.
    ControlReply Reorder(string workspace, string where) =>
        WorkspaceNamed(workspace).Bind(w => (where.ToLowerInvariant() switch
            {
                "up" => manager.MoveWorkspace(w.Id, -1),
                "down" => manager.MoveWorkspace(w.Id, +1),
                "top" => manager.MoveWorkspaceTo(w.Id, 0),
                "end" or "bottom" => manager.MoveWorkspaceTo(w.Id, manager.State.Workspaces.Count - 1),
                _ => ControlArguments.Position(where)
                    .MapError(_ => $"reorder takes up, down, top, end or a position from 1, not '{where}'.")
                    .Bind(position => manager.MoveWorkspaceTo(w.Id, position - 1)),
            }).Map(() => w.Id))
            .Match(id => Success(Describe(id)), ControlReply.Fail);

    ControlReply SetRowMinimized(string workspace, bool minimized) =>
        WorkspaceNamed(workspace).Match(
            w => Reply(manager.SetWorkspaceMinimized(w.Id, minimized), () => Describe(w.Id)),
            ControlReply.Fail);

    // SetWorkspaceColor already hands a grouped workspace's colour to its group, as the menu does.
    ControlReply Colour(string workspace, string colour) =>
        WorkspaceNamed(workspace).Bind(w => ParseColour(colour).Bind(c => manager.SetWorkspaceColor(w.Id, c)).Map(() => w.Id))
            .Match(id => Success(Describe(id)), ControlReply.Fail);

    // The menu asks first and offers to close what is open; a command cannot ask, so closing has to be
    // SAID. Without --close-windows a workspace with windows is refused with the manager's own reason,
    // which is what an agent tidying up after itself should hit before it closes anybody's editor.
    ControlReply Delete(string workspace, bool closeWindows) =>
        WorkspaceNamed(workspace).Match(
            w => Reply(closeWindows ? manager.DeleteWorkspaceClosingWindows(w.Id) : manager.DeleteWorkspaceIfEmpty(w.Id),
                () => new { deleted = w.Name }),
            ControlReply.Fail);

    ControlReply Switch(string workspace) =>
        WorkspaceNamed(workspace).Match(
            w => Reply(manager.Switch(w.Id), () => Describe(w.Id)),
            ControlReply.Fail);

    // By id, or by the number `desktops` prints. A desktop that already has a workspace is refused by the
    // manager; rename covers that one.
    ControlReply NameDesktop(string desktop, string name) =>
        manager.LiveDesktops()
            .Bind(live => live.Select((d, at) => (d, number: at + 1))
                .TryFirst(x => (Guid.TryParse(desktop, out var id) && id == x.d.Id) || x.number.ToString() == desktop.Trim())
                .ToResult($"There is no desktop '{desktop}'. `desktops` lists them with their numbers."))
            .Bind(x => manager.NameDesktop(x.d.Id, name))
            .Match(w => Success(new { created = true, workspace = Describe(w.Id) }), ControlReply.Fail);

    // Minimize bar is the app's business, not the manager's (the bar swaps for a stand-in), so the app
    // hands in how to do it. Null in tests and anywhere the bar does not exist.
    ControlReply Bar(string state) =>
        setBarVisible is null
            ? ControlReply.Fail("The bar cannot be controlled from here.")
            : state.ToLowerInvariant() switch
            {
                "minimize" => Reply(setBarVisible(false), () => new { bar = "minimized" }),
                "restore" => Reply(setBarVisible(true), () => new { bar = "restored" }),
                _ => ControlReply.Fail(ControlUsage.LineFor("bar")),
            };

    // The palette's names, a #RRGGBB of your own (what the Custom… picker stores), "none" for the
    // Transparent entry, or "default" for By position, which is the absence of a colour.
    static Result<string?> ParseColour(string raw) =>
        raw.Trim() is var colour && colour.Equals("default", StringComparison.OrdinalIgnoreCase)
            ? Result.Success<string?>(null)
            : WorkspacePalette.IsNone(colour)
                ? Result.Success<string?>(WorkspacePalette.None)
                : WorkspacePalette.Swatches.TryFirst(s => s.Name.Equals(colour, StringComparison.OrdinalIgnoreCase)) is { HasValue: true } swatch
                    ? Result.Success<string?>(swatch.Value.Hex)
                    : Regex.IsMatch(colour, "^#[0-9A-Fa-f]{6}$")
                        ? Result.Success<string?>(colour.ToUpperInvariant())
                        : Result.Failure<string?>(
                            $"'{raw}' is not a colour. Use {string.Join(", ", WorkspacePalette.Swatches.Select(s => s.Name.ToLowerInvariant()))}, #RRGGBB, none or default.");
}
