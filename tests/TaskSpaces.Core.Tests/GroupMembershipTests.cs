using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Tests;

// #83 (move into a group, move out, ungroup) and the creating half of #84 (a group that is a name
// with no parent workspace). Petre: "all three live on the workspace row's right-click context
// menu", and "i want the bar to drive it" -- so all of it has to work without Manage.
public class GroupMembershipTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    (WorkspaceManager manager, Guid a, Guid b, Guid c) Started()
    {
        var a = new Workspace(Guid.NewGuid(), "Alpha", Guid.NewGuid());
        var b = new Workspace(Guid.NewGuid(), "Beta", Guid.NewGuid());
        var c = new Workspace(Guid.NewGuid(), "Gamma", Guid.NewGuid());
        new[] { a, b, c }.ToList().ForEach(w => desktops.Desktops.Add(new DesktopInfo(w.DesktopId!.Value, w.Name)));
        store.Stored = AppState.Empty with { Workspaces = [a, b, c] };

        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return (manager, a.Id, b.Id, c.Id);
    }

    // --- creating an anchorless group ---------------------------------------------------------

    [Fact]
    public void A_new_group_is_anchorless_and_holds_the_workspace_it_was_made_from()
    {
        var (manager, a, _, _) = Started();

        var group = manager.CreateGroup("Clients", a);

        Assert.True(group.IsSuccess);
        Assert.False(group.Value.IsAnchored);
        Assert.Equal("Clients", group.Value.Name);
        Assert.Equal(group.Value.Id, manager.State.Workspaces.Single(w => w.Id == a).GroupId);
    }

    // Nothing is borrowed, which is the whole point of the construct: there is no parent workspace,
    // so there are no windows to lend.
    [Fact]
    public void An_anchorless_group_lends_no_windows()
    {
        var (manager, a, b, _) = Started();
        var group = manager.CreateGroup("Clients", a).Value;
        Assert.True(manager.MoveIntoGroup(b, group.Id).IsSuccess);

        Assert.Null(manager.State.LendsWindowsTo(a));
        Assert.Null(manager.State.LendsWindowsTo(b));
    }

    [Fact]
    public void A_group_name_is_required_and_must_be_unique()
    {
        var (manager, a, b, _) = Started();
        Assert.True(manager.CreateGroup("Clients", a).IsSuccess);

        Assert.True(manager.CreateGroup("   ", b).IsFailure);
        Assert.True(manager.CreateGroup("clients", b).IsFailure); // case-insensitive
    }

    // Moving it silently would make "new group" dissolve the group it came from as a side effect,
    // which is a second consequence the user did not ask for.
    [Fact]
    public void A_workspace_already_in_a_group_must_leave_before_starting_another()
    {
        var (manager, a, b, _) = Started();
        var group = manager.CreateGroup("Clients", a).Value;
        Assert.True(manager.MoveIntoGroup(b, group.Id).IsSuccess);

        var second = manager.CreateGroup("Others", b);

        Assert.True(second.IsFailure);
        Assert.Contains("Clients", second.Error);
    }

    // --- moving in ----------------------------------------------------------------------------

    [Fact]
    public void Moving_into_an_anchorless_group_is_pure_membership()
    {
        var (manager, a, b, _) = Started();
        var group = manager.CreateGroup("Clients", a).Value;

        Assert.True(manager.MoveIntoGroup(b, group.Id).IsSuccess);

        Assert.Equal(group.Id, manager.State.Workspaces.Single(w => w.Id == b).GroupId);
        Assert.Equal(2, manager.State.MembersOf(group.Id).Count);
    }

    // The payoff of one model: joining an ANCHORED group makes the workspace a child that borrows
    // the anchor's windows, and it takes no extra code to do it.
    [Fact]
    public void Moving_into_an_anchored_group_starts_borrowing_from_its_anchor()
    {
        var (manager, a, b, c) = Started();
        Assert.True(manager.NestWorkspace(b, a).IsSuccess);
        var group = manager.State.GroupOf(a)!.Id;

        Assert.True(manager.MoveIntoGroup(c, group).IsSuccess);

        Assert.Equal(a, manager.State.LendsWindowsTo(c));
    }

    // Leaving the old group is part of moving, so a group of two that loses a member is dissolved
    // exactly as a plain "move out" would dissolve it.
    [Fact]
    public void Moving_between_groups_dissolves_a_group_of_two_left_behind()
    {
        var (manager, a, b, c) = Started();
        var first = manager.CreateGroup("Clients", a).Value;
        Assert.True(manager.MoveIntoGroup(b, first.Id).IsSuccess);
        Assert.True(manager.CreateGroup("Others", c).IsSuccess);
        var second = manager.State.GroupOf(c)!.Id;

        Assert.True(manager.MoveIntoGroup(b, second).IsSuccess);

        Assert.DoesNotContain(manager.State.Groups, g => g.Id == first.Id);
        Assert.Null(manager.State.Workspaces.Single(w => w.Id == a).GroupId);
        Assert.Equal(second, manager.State.Workspaces.Single(w => w.Id == b).GroupId);
    }

    [Fact]
    public void An_anchor_cannot_move_into_another_group()
    {
        var (manager, a, b, c) = Started();
        Assert.True(manager.NestWorkspace(b, a).IsSuccess);
        Assert.True(manager.CreateGroup("Clients", c).IsSuccess);
        var other = manager.State.GroupOf(c)!.Id;

        Assert.True(manager.MoveIntoGroup(a, other).IsFailure);
    }

    [Fact]
    public void Moving_into_a_group_that_does_not_exist_is_refused() =>
        Assert.True(Started().manager.MoveIntoGroup(Started().a, Guid.NewGuid()).IsFailure);

    // --- moving out ---------------------------------------------------------------------------

    [Fact]
    public void Moving_out_of_a_group_of_three_leaves_the_group_standing()
    {
        var (manager, a, b, c) = Started();
        var group = manager.CreateGroup("Clients", a).Value;
        Assert.True(manager.MoveIntoGroup(b, group.Id).IsSuccess);
        Assert.True(manager.MoveIntoGroup(c, group.Id).IsSuccess);

        Assert.True(manager.LeaveGroup(c).IsSuccess);

        Assert.Contains(manager.State.Groups, g => g.Id == group.Id);
        Assert.Null(manager.State.Workspaces.Single(w => w.Id == c).GroupId);
        Assert.Equal(2, manager.State.MembersOf(group.Id).Count);
    }

    // An outline around a single row is decoration that means nothing, so a group cannot be left
    // with one member.
    [Fact]
    public void Moving_out_of_a_group_of_two_dissolves_it()
    {
        var (manager, a, b, _) = Started();
        var group = manager.CreateGroup("Clients", a).Value;
        Assert.True(manager.MoveIntoGroup(b, group.Id).IsSuccess);

        Assert.True(manager.LeaveGroup(b).IsSuccess);

        Assert.Empty(manager.State.Groups);
        Assert.Null(manager.State.Workspaces.Single(w => w.Id == a).GroupId);
    }

    // The anchor leaving is the interesting case: the others keep their grouping and their name, and
    // simply stop borrowing, because the workspace that was lending the windows has gone.
    [Fact]
    public void An_anchor_leaving_a_group_of_three_makes_it_anchorless()
    {
        var (manager, a, b, c) = Started();
        Assert.True(manager.NestWorkspace(b, a).IsSuccess);
        var group = manager.State.GroupOf(a)!.Id;
        Assert.True(manager.MoveIntoGroup(c, group).IsSuccess);

        Assert.True(manager.LeaveGroup(a).IsSuccess);

        var remaining = manager.State.Groups.Single(g => g.Id == group);
        Assert.False(remaining.IsAnchored);
        Assert.Equal("Alpha", remaining.Name); // the name it was given, kept after the anchor left
        Assert.Null(manager.State.LendsWindowsTo(b));
        Assert.Equal(2, manager.State.MembersOf(group).Count);
    }

    // --- ungrouping ---------------------------------------------------------------------------

    [Fact]
    public void Ungrouping_frees_every_member_and_removes_the_group()
    {
        var (manager, a, b, c) = Started();
        var group = manager.CreateGroup("Clients", a).Value;
        Assert.True(manager.MoveIntoGroup(b, group.Id).IsSuccess);
        Assert.True(manager.MoveIntoGroup(c, group.Id).IsSuccess);

        Assert.True(manager.Ungroup(group.Id).IsSuccess);

        Assert.Empty(manager.State.Groups);
        Assert.All(manager.State.Workspaces, w => Assert.Null(w.GroupId));
    }

    // Members keep everything except being drawn together, which is what makes ungrouping safe to
    // offer on a menu one mis-click away.
    [Fact]
    public void Ungrouping_keeps_desktops_names_and_positions()
    {
        var (manager, a, b, _) = Started();
        Assert.True(manager.NestWorkspace(b, a).IsSuccess);
        var before = manager.State.Workspaces.Select(w => (w.Id, w.Name, w.DesktopId)).ToList();

        Assert.True(manager.Ungroup(manager.State.GroupOf(a)!.Id).IsSuccess);

        Assert.Equal(before, manager.State.Workspaces.Select(w => (w.Id, w.Name, w.DesktopId)).ToList());
    }

    [Fact]
    public void Ungrouping_an_anchored_group_stops_the_borrowing()
    {
        var (manager, a, b, _) = Started();
        Assert.True(manager.NestWorkspace(b, a).IsSuccess);
        Assert.Equal(a, manager.State.LendsWindowsTo(b));

        Assert.True(manager.Ungroup(manager.State.GroupOf(a)!.Id).IsSuccess);

        Assert.Null(manager.State.LendsWindowsTo(b));
    }

    [Fact]
    public void Ungrouping_something_that_is_not_a_group_is_refused() =>
        Assert.True(Started().manager.Ungroup(Guid.NewGuid()).IsFailure);

    // --- renaming -----------------------------------------------------------------------------

    [Fact]
    public void A_group_can_be_renamed()
    {
        var (manager, a, _, _) = Started();
        var group = manager.CreateGroup("Clients", a).Value;

        Assert.True(manager.RenameGroup(group.Id, "Customers").IsSuccess);

        Assert.Equal("Customers", manager.State.Groups.Single().Name);
    }

    [Fact]
    public void Renaming_to_a_name_another_group_already_has_is_refused()
    {
        var (manager, a, b, _) = Started();
        var first = manager.CreateGroup("Clients", a).Value;
        Assert.True(manager.CreateGroup("Others", b).IsSuccess);

        Assert.True(manager.RenameGroup(first.Id, "Others").IsFailure);
        Assert.True(manager.RenameGroup(first.Id, "  ").IsFailure);
    }

    // A group named after its anchor is the commonest case, so a group and a workspace sharing a
    // name has to stay legal.
    [Fact]
    public void A_group_may_share_a_name_with_a_workspace()
    {
        var (manager, a, _, _) = Started();

        Assert.True(manager.CreateGroup("Alpha", a).IsSuccess);
    }
}
