using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Tests;

// Groups replaced ParentId (#84 needs a group with a name and no parent workspace, which ParentId
// cannot express), so every state.json written while #42 shipped has to keep working.
//
// This is the one piece of the change that can lose a user's data if it is wrong, and the failure
// mode is quiet: nesting would simply be gone on the first launch after an upgrade, which looks
// like a rendering bug rather than like data loss.
public class GroupMigrationTests
{
    static Workspace Workspace(string name) => new(Guid.NewGuid(), name, Guid.NewGuid());

    // A parent with two children and one unrelated workspace, as an older file would have stored it.
    static (AppState state, Workspace parent, Workspace first, Workspace second, Workspace loner) Nested()
    {
        var parent = Workspace("Sparrow");
        var first = Workspace("dice-rolls") with { ParentId = parent.Id };
        var second = Workspace("slip39") with { ParentId = parent.Id };
        var loner = Workspace("Personal");
        return (AppState.Empty with { Workspaces = [parent, first, second, loner] }, parent, first, second, loner);
    }

    [Fact]
    public void A_parent_becomes_one_anchored_group()
    {
        var (state, parent, _, _, _) = Nested();

        var migrated = state.Migrated();

        var group = Assert.Single(migrated.Groups);
        Assert.Equal(parent.Id, group.AnchorWorkspaceId);
        Assert.True(group.IsAnchored);
    }

    // The group takes the parent's name at migration time, and keeps its own copy afterwards.
    [Fact]
    public void The_group_is_named_after_the_parent()
    {
        var (state, _, _, _, _) = Nested();

        Assert.Equal("Sparrow", Assert.Single(state.Migrated().Groups).Name);
    }

    // The parent joins its own group. Under ParentId it sat outside the relationship and was merely
    // pointed at from below; a group is a set, and the anchor is in it.
    [Fact]
    public void The_parent_and_its_children_all_end_up_in_the_group()
    {
        var (state, parent, first, second, _) = Nested();

        var migrated = state.Migrated();
        var group = Assert.Single(migrated.Groups).Id;

        Assert.All(new[] { parent.Id, first.Id, second.Id },
            id => Assert.Equal(group, migrated.Workspaces.Single(w => w.Id == id).GroupId));
    }

    [Fact]
    public void A_workspace_that_was_never_nested_stays_ungrouped()
    {
        var (state, _, _, _, loner) = Nested();

        Assert.Null(state.Migrated().Workspaces.Single(w => w.Id == loner.Id).GroupId);
    }

    [Fact]
    public void Two_parents_become_two_groups()
    {
        var first = Workspace("Sparrow");
        var second = Workspace("GEPHA");
        var state = AppState.Empty with
        {
            Workspaces =
            [
                first, Workspace("dice-rolls") with { ParentId = first.Id },
                second, Workspace("HRIS") with { ParentId = second.Id },
            ],
        };

        var migrated = state.Migrated();

        Assert.Equal(2, migrated.Groups.Count);
        Assert.Equal(["Sparrow", "GEPHA"], migrated.Groups.Select(g => g.Name));
    }

    // Running it twice must not produce a second set of groups for the same parents, because the
    // manager persists after loading and the next load would migrate again.
    [Fact]
    public void Migrating_twice_changes_nothing_the_second_time()
    {
        var once = Nested().state.Migrated();

        var twice = once.Migrated();

        Assert.Equal(once.Groups, twice.Groups);
        Assert.Equal(once.Workspaces, twice.Workspaces);
    }

    [Fact]
    public void A_file_with_no_nesting_is_left_alone()
    {
        var state = AppState.Empty with { Workspaces = [Workspace("Personal"), Workspace("Work")] };

        var migrated = state.Migrated();

        Assert.Empty(migrated.Groups);
        Assert.All(migrated.Workspaces, w => Assert.Null(w.GroupId));
    }

    // A hand-edited state.json can point at a parent that is not there. The children come out
    // ungrouped, which is what the bar already drew for them, rather than joining a group whose
    // anchor cannot be found.
    [Fact]
    public void A_parent_that_does_not_exist_is_dropped_rather_than_anchoring_a_group()
    {
        var orphan = Workspace("dice-rolls") with { ParentId = Guid.NewGuid() };
        var state = AppState.Empty with { Workspaces = [orphan] };

        var migrated = state.Migrated();

        Assert.Empty(migrated.Groups);
        Assert.Null(migrated.Workspaces.Single().GroupId);
    }

    // ParentId survives migration untouched, so downgrading to a build from before groups still
    // finds its nesting where it expects it.
    [Fact]
    public void The_old_parent_id_is_left_in_place_for_a_downgrade()
    {
        var (state, parent, first, _, _) = Nested();

        var migrated = state.Migrated();

        Assert.Equal(parent.Id, migrated.Workspaces.Single(w => w.Id == first.Id).ParentId);
    }
}
