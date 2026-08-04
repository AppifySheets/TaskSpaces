using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Tests;

// Petre: "maybe an alt-tab like shortcut for me to switch through workspaces".
//
// The ordering rules on their own, with no Win32, no timers and no picker: what makes
// Alt+Tab worth having is that one tap goes back to where you just were, and that is
// entirely a property of this list.
public class WorkspaceMruTests
{
    static Workspace Named(string name) => new(Guid.NewGuid(), name, Guid.NewGuid());

    static IReadOnlyList<string> NamesOf(WorkspaceMru mru, IReadOnlyList<Workspace> workspaces) =>
        mru.Order(workspaces).Select(w => w.Name).ToList();

    [Fact]
    public void Never_visited_workspaces_keep_the_users_own_order()
    {
        var workspaces = new[] { Named("Work"), Named("Personal"), Named("YouTube") };

        Assert.Equal(["Work", "Personal", "YouTube"], NamesOf(WorkspaceMru.Empty, workspaces));
    }

    [Fact]
    public void The_last_visited_workspace_comes_first()
    {
        var workspaces = new[] { Named("Work"), Named("Personal"), Named("YouTube") };
        var mru = WorkspaceMru.Empty.Touch(workspaces[2].Id);

        Assert.Equal(["YouTube", "Work", "Personal"], NamesOf(mru, workspaces));
    }

    // The whole point of the feature: from Work, one tap must land on the workspace used
    // before it, whatever the defined order says.
    [Fact]
    public void Visits_stack_most_recent_first()
    {
        var workspaces = new[] { Named("Work"), Named("Personal"), Named("YouTube") };
        var mru = WorkspaceMru.Empty
            .Touch(workspaces[1].Id)  // Personal
            .Touch(workspaces[2].Id)  // then YouTube
            .Touch(workspaces[0].Id); // and now Work

        Assert.Equal(["Work", "YouTube", "Personal"], NamesOf(mru, workspaces));
    }

    // Otherwise the list would grow without bound and the same workspace would appear twice.
    [Fact]
    public void Re_visiting_moves_a_workspace_rather_than_duplicating_it()
    {
        var workspaces = new[] { Named("Work"), Named("Personal") };
        var mru = WorkspaceMru.Empty
            .Touch(workspaces[0].Id)
            .Touch(workspaces[1].Id)
            .Touch(workspaces[0].Id);

        Assert.Equal(2, mru.Recent.Count);
        Assert.Equal(["Work", "Personal"], NamesOf(mru, workspaces));
    }

    // No separate cleanup step exists, on purpose: filtering against the live list IS the
    // cleanup, so a deleted workspace cannot linger as a phantom switch target.
    [Fact]
    public void A_workspace_deleted_since_it_was_visited_simply_drops_out()
    {
        var workspaces = new[] { Named("Work"), Named("Personal") };
        var mru = WorkspaceMru.Empty.Touch(Guid.NewGuid()).Touch(workspaces[1].Id);

        Assert.Equal(["Personal", "Work"], NamesOf(mru, workspaces));
    }

    [Fact]
    public void Touching_returns_a_new_value_and_leaves_the_original_alone()
    {
        var mru = WorkspaceMru.Empty.Touch(Guid.NewGuid());

        Assert.Empty(WorkspaceMru.Empty.Recent);
        Assert.Single(mru.Recent);
    }

    // --- through the manager, where the visits actually come from -----------------------

    [Fact]
    public void Switching_records_a_visit_so_the_next_walk_starts_from_there()
    {
        var first = Named("First");
        var second = Named("Second");
        var desktops = new FakeDesktops();
        desktops.Desktops.Add(new Abstractions.DesktopInfo(first.DesktopId!.Value, "First"));
        desktops.Desktops.Add(new Abstractions.DesktopInfo(second.DesktopId!.Value, "Second"));
        desktops.CurrentDesktopId = first.DesktopId!.Value;
        var store = new FakeStore { Stored = AppState.Empty with { Workspaces = [first, second] } };
        var manager = new WorkspaceManager(desktops, new FakeMonitor(), new FakeTitles(), store);
        Assert.True(manager.Start().IsSuccess);

        Assert.True(manager.Switch(second.Id).IsSuccess);
        desktops.CurrentDesktopId = second.DesktopId!.Value;

        var recent = manager.ByRecentUse();
        Assert.Equal(["Second", "First"], recent.Ordered.Select(w => w.Name));
        Assert.Equal(0, recent.CurrentIndex); // we are ON the head of the list
    }

    // A switch made with Task View or Win+Ctrl+arrows is just as much a visit as one we
    // performed; an Alt+Tab list that ignored those would send the first tap somewhere
    // Petre did not expect.
    [Fact]
    public void A_switch_made_outside_the_app_counts_as_a_visit_too()
    {
        var first = Named("First");
        var second = Named("Second");
        var desktops = new FakeDesktops();
        desktops.Desktops.Add(new Abstractions.DesktopInfo(first.DesktopId!.Value, "First"));
        desktops.Desktops.Add(new Abstractions.DesktopInfo(second.DesktopId!.Value, "Second"));
        desktops.CurrentDesktopId = first.DesktopId!.Value;
        var store = new FakeStore { Stored = AppState.Empty with { Workspaces = [first, second] } };
        var manager = new WorkspaceManager(desktops, new FakeMonitor(), new FakeTitles(), store);
        Assert.True(manager.Start().IsSuccess);

        desktops.CurrentDesktopId = second.DesktopId!.Value;
        desktops.CurrentChangedSubject.OnNext(second.DesktopId!.Value);

        Assert.Equal(["Second", "First"], manager.ByRecentUse().Ordered.Select(w => w.Name));
    }

    // The picker reads -1 as "start before the beginning", so a forward tap from one of
    // Petre's unbound desktops ("Main") lands on the most recent workspace rather than
    // skipping past it.
    [Fact]
    public void Standing_on_a_desktop_that_is_not_a_workspace_reports_no_current_index()
    {
        var workspace = Named("Work");
        var desktops = new FakeDesktops { CurrentDesktopId = Guid.NewGuid() }; // an unbound desktop
        desktops.Desktops.Add(new Abstractions.DesktopInfo(workspace.DesktopId!.Value, "Work"));
        var store = new FakeStore { Stored = AppState.Empty with { Workspaces = [workspace] } };
        var manager = new WorkspaceManager(desktops, new FakeMonitor(), new FakeTitles(), store);
        Assert.True(manager.Start().IsSuccess);

        Assert.Equal(-1, manager.ByRecentUse().CurrentIndex);
    }
}
