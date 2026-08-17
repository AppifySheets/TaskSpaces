using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Tests;

// #155. Petre: "highlight the PREVIOUS workspace I was in, so i know what i'm coming from and where
// i'd go with the shortcut", then "maybe also ring the one before that, and the one before that".
//
// The trail's first entry is what the back button points at, and the two surfaces must never disagree.
// That is the invariant most of these tests are really about: everything below either checks the walk
// or checks that Back and Trail(...) tell the same story.
public class HistoryTrailTests
{
    static readonly Workspace A = new(Guid.NewGuid(), "Work", Guid.NewGuid());
    static readonly Workspace B = new(Guid.NewGuid(), "EC", Guid.NewGuid());
    static readonly Workspace C = new(Guid.NewGuid(), "Personal", Guid.NewGuid());

    // Ordered is most-recently-used first, so standing on the first entry is the ordinary case: you
    // arrived here, and everything behind you in the list is where you were before.
    static RecentWorkspaces Standing(params Workspace[] ordered) => new(ordered, 0);

    static IReadOnlyList<string> Names(IEnumerable<Workspace> trail) => trail.Select(w => w.Name).ToList();

    [Fact]
    public void The_trail_runs_backwards_through_the_mru() =>
        Assert.Equal(["EC", "Personal"], Names(Standing(A, B, C).Trail(2)));

    [Fact]
    public void Its_first_entry_is_where_the_back_button_goes()
    {
        var recent = Standing(A, B, C);

        Assert.Equal(recent.Back.Value.Id, recent.Trail(2)[0].Id);
    }

    [Fact]
    public void The_depth_is_respected() =>
        Assert.Single(Standing(A, B, C).Trail(1));

    [Fact]
    public void A_depth_deeper_than_the_history_stops_at_the_history() =>
        Assert.Equal(2, Standing(A, B, C).Trail(5).Count);

    // The case that would otherwise ring the row you are standing on: two workspaces, two steps
    // forward, and the walk is back where it began.
    [Fact]
    public void The_current_workspace_is_never_in_its_own_trail()
    {
        var recent = Standing(A, B);

        Assert.Equal(["EC"], Names(recent.Trail(2)));
    }

    // One workspace has no history at all, which is also exactly when the back button is disabled.
    [Fact]
    public void One_workspace_has_no_trail()
    {
        var recent = Standing(A);

        Assert.Empty(recent.Trail(2));
        Assert.True(recent.Back.HasNoValue);
    }

    [Fact]
    public void No_workspaces_has_no_trail() =>
        Assert.Empty(new RecentWorkspaces([], -1).Trail(2));

    // Standing on an UNBOUND desktop: CurrentIndex is -1, which the switcher already reads as "before
    // the beginning", so the first step lands on the most recent workspace. The trail has to say the
    // same thing the back button does there, and the back button says "go to the most recent one".
    [Fact]
    public void From_an_unbound_desktop_the_trail_starts_at_the_most_recent_workspace()
    {
        var recent = new RecentWorkspaces([A, B, C], -1);

        Assert.Equal(["Work", "EC"], Names(recent.Trail(2)));
        Assert.Equal(recent.Back.Value.Id, recent.Trail(2)[0].Id);
    }

    // A zero or negative depth is not an error, it is "draw no trail", which is what a future setting
    // turning the feature off would ask for.
    [Fact]
    public void A_depth_of_zero_draws_nothing() =>
        Assert.Empty(Standing(A, B, C).Trail(0));

    // Standing somewhere other than the front of the MRU happens after a restart, when the list is
    // deliberately not remembered and the current workspace can be anywhere in it. The walk wraps, so
    // the trail is still the rest of the list in order rather than nothing at all -- and it still
    // agrees with the back button, which wraps the same way.
    [Fact]
    public void From_the_end_of_the_list_the_walk_wraps_like_the_back_button_does()
    {
        var recent = new RecentWorkspaces([A, B, C], 2);

        Assert.Equal(["Work", "EC"], Names(recent.Trail(2)));
        Assert.Equal(recent.Back.Value.Id, recent.Trail(2)[0].Id);
    }
}
