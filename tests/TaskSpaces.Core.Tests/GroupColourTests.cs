using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Tests;

// #90 (a group has one colour, settable from the parent or any member) and #92 (a workspace moving
// into a group must not recolour the group).
//
// Petre: "the parent can change the group's colour, and so can any child... a group has one colour:
// if any child changes it, the entire group takes that colour."
public class GroupColourTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    (WorkspaceManager manager, Guid a, Guid b, Guid c) Started(string? colourOfA = null)
    {
        var a = new Workspace(Guid.NewGuid(), "Alpha", Guid.NewGuid()) { Color = colourOfA };
        var b = new Workspace(Guid.NewGuid(), "Beta", Guid.NewGuid());
        var c = new Workspace(Guid.NewGuid(), "Gamma", Guid.NewGuid());
        new[] { a, b, c }.ToList().ForEach(w => desktops.Desktops.Add(new DesktopInfo(w.DesktopId!.Value, w.Name)));
        store.Stored = AppState.Empty with { Workspaces = [a, b, c] };

        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return (manager, a.Id, b.Id, c.Id);
    }

    // --- where the colour is set from ---------------------------------------------------------

    // The picker on a MEMBER's row is the group's picker. The workspace's own Color is deliberately
    // untouched, which is what lets it get its own colour back when it leaves.
    [Fact]
    public void A_member_setting_a_colour_sets_the_groups()
    {
        var (manager, a, b, _) = Started();
        var group = manager.CreateGroup("Clients", a).Value.Id;
        Assert.True(manager.MoveIntoGroup(b, group).IsSuccess);

        Assert.True(manager.SetWorkspaceColor(b, "#123456").IsSuccess);

        Assert.Equal("#123456", manager.State.Groups.Single().Color);
        Assert.Null(manager.State.Workspaces.Single(w => w.Id == b).Color);
    }

    [Fact]
    public void The_anchor_setting_a_colour_sets_the_groups_too()
    {
        var (manager, a, b, _) = Started();
        Assert.True(manager.NestWorkspace(b, a).IsSuccess);

        Assert.True(manager.SetWorkspaceColor(a, "#123456").IsSuccess);

        Assert.Equal("#123456", manager.State.GroupOf(a)!.Color);
        Assert.Null(manager.State.Workspaces.Single(w => w.Id == a).Color);
    }

    // An anchorless group's header is the only row of its own it has, and #84 gave it its own menu.
    [Fact]
    public void The_header_sets_the_groups_colour_directly()
    {
        var (manager, a, _, _) = Started();
        var group = manager.CreateGroup("Clients", a).Value.Id;

        Assert.True(manager.SetGroupColor(group, WorkspacePalette.None).IsSuccess);

        Assert.Equal(WorkspacePalette.None, manager.State.Groups.Single().Color);
    }

    [Fact]
    public void Setting_the_colour_of_a_group_that_is_gone_is_refused() =>
        Assert.True(Started().manager.SetGroupColor(Guid.NewGuid(), "#123456").IsFailure);

    // An ungrouped workspace still keeps its own colour, which is the #68 behaviour and must not be
    // swallowed by the redirect above.
    [Fact]
    public void An_ungrouped_workspace_keeps_its_own_colour()
    {
        var (manager, a, _, _) = Started();

        Assert.True(manager.SetWorkspaceColor(a, "#123456").IsSuccess);

        Assert.Equal("#123456", manager.State.Workspaces.Single(w => w.Id == a).Color);
    }

    // --- what grouping does to colours ---------------------------------------------------------

    // Making a group out of a coloured workspace must not recolour the row you made it from, so the
    // group starts in the colour that row was already wearing.
    [Fact]
    public void A_new_group_starts_in_the_colour_of_the_workspace_it_was_made_from()
    {
        var (manager, a, _, _) = Started(colourOfA: "#ABCDEF");

        Assert.Equal("#ABCDEF", manager.CreateGroup("Clients", a).Value.Color);
    }

    [Fact]
    public void Nesting_gives_the_new_group_the_parents_colour()
    {
        var (manager, a, b, _) = Started(colourOfA: "#ABCDEF");

        Assert.True(manager.NestWorkspace(b, a).IsSuccess);

        Assert.Equal("#ABCDEF", manager.State.GroupOf(a)!.Color);
    }

    // #92, the reported bug: the group's colour is the group's, and a newcomer adopts it rather than
    // imposing its own.
    [Fact]
    public void Joining_a_group_does_not_recolour_it()
    {
        var (manager, a, b, _) = Started();
        var group = manager.CreateGroup("Clients", a).Value.Id;
        Assert.True(manager.SetGroupColor(group, "#111111").IsSuccess);
        Assert.True(manager.SetWorkspaceColor(b, "#222222").IsSuccess); // b is ungrouped, so its own

        Assert.True(manager.MoveIntoGroup(b, group).IsSuccess);

        Assert.Equal("#111111", manager.State.Groups.Single().Color);
    }

    // And the other direction: what a member chose for itself before joining is still there when it
    // leaves, because the group's colour was never copied onto it.
    [Fact]
    public void Leaving_a_group_gives_a_workspace_its_own_colour_back()
    {
        var (manager, a, b, c) = Started();
        Assert.True(manager.SetWorkspaceColor(b, "#222222").IsSuccess);
        var group = manager.CreateGroup("Clients", a).Value.Id;
        Assert.True(manager.MoveIntoGroup(b, group).IsSuccess);
        Assert.True(manager.MoveIntoGroup(c, group).IsSuccess);
        Assert.True(manager.SetGroupColor(group, "#111111").IsSuccess);

        Assert.True(manager.LeaveGroup(b).IsSuccess);

        Assert.Equal("#222222", manager.State.Workspaces.Single(w => w.Id == b).Color);
    }

    // --- the position a group's colour falls back to -------------------------------------------

    // With no colour of its own a group follows list position like any row, and the position is the
    // anchor's: the group and its parent row must not be two different colours.
    [Fact]
    public void An_anchored_group_falls_back_to_its_anchors_position()
    {
        var (manager, a, b, _) = Started();
        Assert.True(manager.NestWorkspace(b, a).IsSuccess);

        Assert.Equal(0, manager.State.ColourSlotOf(manager.State.GroupOf(a)!));
    }

    // An anchorless group has no parent to inherit from, so the first member is the only stable
    // answer, which keeps "colour follows list position" true for groups as well as single rows.
    [Fact]
    public void An_anchorless_group_falls_back_to_its_first_members_position()
    {
        var (manager, _, b, c) = Started();
        var group = manager.CreateGroup("Clients", b).Value.Id;
        Assert.True(manager.MoveIntoGroup(c, group).IsSuccess);

        Assert.Equal(1, manager.State.ColourSlotOf(manager.State.Groups.Single()));
    }

    // The palette resolves a group's colour by exactly the same rule as a workspace's, which is why
    // both go through one method.
    [Fact]
    public void The_palette_prefers_an_override_and_falls_back_to_the_position()
    {
        Assert.Equal("#123456", WorkspacePalette.For("#123456", 3));
        Assert.Equal(WorkspacePalette.For((string?)null, 3), WorkspacePalette.For("   ", 3));
    }
}
