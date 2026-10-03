using CSharpFunctionalExtensions;

namespace TaskSpaces.Core.Control;

// The group menu and the group header's menu. See RemoteControl.cs for the shared parts.
public sealed partial class RemoteControl
{
    // A group is born holding the workspace it was made from, exactly as "New group…" on a row makes one.
    ControlReply GroupCreate(string name, string firstWorkspace) =>
        WorkspaceNamed(firstWorkspace).Bind(w => manager.CreateGroup(name, w.Id)).Match(
            group => Success(DescribeGroup(group.Id)),
            ControlReply.Fail);

    // Joins at the bottom of the group, which is where the bar's "Move into group" puts it.
    ControlReply GroupJoin(string workspace, string group) =>
        WorkspaceNamed(workspace).Bind(w => GroupNamed(group).Bind(g => manager.MoveIntoGroup(w.Id, g.Id)).Map(() => w.Id))
            .Match(id => Success(Describe(id)), ControlReply.Fail);

    ControlReply GroupLeave(string workspace) =>
        WorkspaceNamed(workspace).Match(
            w => Reply(manager.LeaveGroup(w.Id), () => Describe(w.Id)),
            ControlReply.Fail);

    ControlReply GroupRename(string group, string name) =>
        GroupNamed(group).Match(
            g => Reply(manager.RenameGroup(g.Id, name), () => DescribeGroup(g.Id)),
            ControlReply.Fail);

    ControlReply GroupColour(string group, string colour) =>
        GroupNamed(group).Bind(g => ParseColour(colour).Bind(c => manager.SetGroupColor(g.Id, c)).Map(() => g.Id))
            .Match(id => Success(DescribeGroup(id)), ControlReply.Fail);

    // The members keep their desktops, windows and places in the list; the reply names them so the
    // caller can see who was freed.
    ControlReply Ungroup(string group) =>
        GroupNamed(group).Match(
            g =>
            {
                var members = manager.State.MembersOf(g.Id).Select(m => m.Name).ToList();
                return Reply(manager.Ungroup(g.Id), () => new { ungrouped = g.Name, members });
            },
            ControlReply.Fail);
}
