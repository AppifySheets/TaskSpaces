using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Tests;

// Petre: "i need to be able to move workspaces up or down in the manage window".
//
// Order is not cosmetic: State.Workspaces order drives the floating bar's row order, the
// switcher panel's group order AND which workspace Ctrl+Alt+1..9 lands on. So it has to
// persist, and everything downstream follows from this one list.
public class WorkspaceOrderTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    (WorkspaceManager manager, Guid first, Guid second, Guid third) Started()
    {
        var a = new Workspace(Guid.NewGuid(), "GEPHA", Guid.NewGuid());
        var b = new Workspace(Guid.NewGuid(), "Sparrow", Guid.NewGuid());
        var c = new Workspace(Guid.NewGuid(), "TaskSpace", Guid.NewGuid());
        new List<Workspace> { a, b, c }.ForEach(w => desktops.Desktops.Add(new Abstractions.DesktopInfo(w.DesktopId!.Value, w.Name)));
        store.Stored = AppState.Empty with { Workspaces = [a, b, c] };
        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return (manager, a.Id, b.Id, c.Id);
    }

    static IReadOnlyList<string> NamesOf(WorkspaceManager manager) =>
        manager.State.Workspaces.Select(w => w.Name).ToList();

    [Fact]
    public void Moving_a_workspace_up_swaps_it_with_its_predecessor()
    {
        var (manager, _, second, _) = Started();

        Assert.True(manager.MoveWorkspace(second, -1).IsSuccess);

        Assert.Equal(["Sparrow", "GEPHA", "TaskSpace"], NamesOf(manager));
    }

    [Fact]
    public void Moving_a_workspace_down_swaps_it_with_its_successor()
    {
        var (manager, first, _, _) = Started();

        Assert.True(manager.MoveWorkspace(first, +1).IsSuccess);

        Assert.Equal(["Sparrow", "GEPHA", "TaskSpace"], NamesOf(manager));
    }

    [Fact]
    public void The_new_order_is_persisted()
    {
        var (manager, _, _, third) = Started();

        Assert.True(manager.MoveWorkspace(third, -1).IsSuccess);

        Assert.Equal(["GEPHA", "TaskSpace", "Sparrow"], store.Stored.Workspaces.Select(w => w.Name));
    }

    // Clamping rather than failing: the Manage window's Up button on the first row is a
    // no-op, not an error dialog.
    [Fact]
    public void Moving_the_first_workspace_up_is_a_no_op()
    {
        var (manager, first, _, _) = Started();

        Assert.True(manager.MoveWorkspace(first, -1).IsSuccess);

        Assert.Equal(["GEPHA", "Sparrow", "TaskSpace"], NamesOf(manager));
    }

    [Fact]
    public void Moving_the_last_workspace_down_is_a_no_op()
    {
        var (manager, _, _, third) = Started();

        Assert.True(manager.MoveWorkspace(third, +1).IsSuccess);

        Assert.Equal(["GEPHA", "Sparrow", "TaskSpace"], NamesOf(manager));
    }

    [Fact]
    public void Moving_an_unknown_workspace_fails()
    {
        var (manager, _, _, _) = Started();

        Assert.True(manager.MoveWorkspace(Guid.NewGuid(), -1).IsFailure);
    }

    // Reordering must not disturb anything else about a workspace -- notably its bound
    // desktop, which is what actually holds the windows.
    [Fact]
    public void Reordering_preserves_desktop_bindings()
    {
        var (manager, _, second, _) = Started();
        var boundBefore = manager.State.Workspaces.Single(w => w.Id == second).DesktopId;

        Assert.True(manager.MoveWorkspace(second, -1).IsSuccess);

        Assert.Equal(boundBefore, manager.State.Workspaces.Single(w => w.Id == second).DesktopId);
    }

    // --- minimized rows (#52) ---------------------------------------------------------------
    //
    // Petre: "minimized workspace rows: right-click to shrink a row to a third of its height."
    // How SMALL is the bar's business; that it survives a restart and disturbs nothing else is
    // this layer's.

    [Fact]
    public void A_workspace_starts_un_minimized() =>
        Assert.DoesNotContain(Started().manager.State.Workspaces, w => w.Minimized);

    [Fact]
    public void Minimizing_a_workspace_persists_and_pulses()
    {
        var (manager, _, second, _) = Started();
        var savesBefore = store.SaveCount;
        var pulses = 0;
        using var subscription = manager.StateChanged.Subscribe(_ => pulses++);

        Assert.True(manager.SetWorkspaceMinimized(second, true).IsSuccess);

        Assert.True(manager.State.Workspaces.Single(w => w.Id == second).Minimized);
        Assert.True(store.Stored.Workspaces.Single(w => w.Id == second).Minimized);
        Assert.Equal(savesBefore + 1, store.SaveCount);
        Assert.Equal(1, pulses);
    }

    [Fact]
    public void Restoring_a_workspace_undoes_it()
    {
        var (manager, _, second, _) = Started();
        Assert.True(manager.SetWorkspaceMinimized(second, true).IsSuccess);

        Assert.True(manager.SetWorkspaceMinimized(second, false).IsSuccess);

        Assert.False(manager.State.Workspaces.Single(w => w.Id == second).Minimized);
    }

    // Presentational and nothing more: a minimized workspace keeps its name, its place in the
    // order and -- the one that would actually lose windows -- its bound desktop.
    [Fact]
    public void Minimizing_changes_nothing_but_the_flag()
    {
        var (manager, _, second, _) = Started();
        var before = manager.State.Workspaces.Single(w => w.Id == second);

        Assert.True(manager.SetWorkspaceMinimized(second, true).IsSuccess);

        var after = manager.State.Workspaces.Single(w => w.Id == second);
        Assert.Equal(before with { Minimized = true }, after);
        Assert.Equal(["GEPHA", "Sparrow", "TaskSpace"], NamesOf(manager));
    }

    [Fact]
    public void Minimizing_an_unknown_workspace_fails() =>
        Assert.True(Started().manager.SetWorkspaceMinimized(Guid.NewGuid(), true).IsFailure);

    // --- move to top / move to end (#40) ----------------------------------------------------
    //
    // Petre: "menu options: add move to the end and to the top."

    [Fact]
    public void Moving_to_the_top_puts_it_first_and_shifts_the_rest_down()
    {
        var (manager, _, _, third) = Started();

        Assert.True(manager.MoveWorkspaceTo(third, 0).IsSuccess);

        Assert.Equal(["TaskSpace", "GEPHA", "Sparrow"], NamesOf(manager));
    }

    [Fact]
    public void Moving_to_the_end_puts_it_last_and_closes_the_gap()
    {
        var (manager, first, _, _) = Started();

        Assert.True(manager.MoveWorkspaceTo(first, manager.State.Workspaces.Count - 1).IsSuccess);

        Assert.Equal(["Sparrow", "TaskSpace", "GEPHA"], NamesOf(manager));
    }

    // A reposition, not a run of swaps: bubbling to the top reaches the same order but persists
    // and pulses once per step, so one gesture would rebuild every open surface three times and
    // rewrite state.json three times.
    [Fact]
    public void Moving_to_the_top_persists_once_however_far_it_travels()
    {
        var (manager, _, _, third) = Started();
        var savesBefore = store.SaveCount;
        var pulses = 0;
        using var subscription = manager.StateChanged.Subscribe(_ => pulses++);

        Assert.True(manager.MoveWorkspaceTo(third, 0).IsSuccess);

        Assert.Equal(1, pulses);
        Assert.Equal(savesBefore + 1, store.SaveCount);
    }

    // Already there: still a success, and still exactly one pulse rather than a special case that
    // silently does nothing -- "move to top" on the top row is a reasonable thing to click.
    [Fact]
    public void Moving_to_where_it_already_is_is_harmless()
    {
        var (manager, first, _, _) = Started();

        Assert.True(manager.MoveWorkspaceTo(first, 0).IsSuccess);

        Assert.Equal(["GEPHA", "Sparrow", "TaskSpace"], NamesOf(manager));
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(99)]
    public void A_target_outside_the_list_lands_at_the_nearest_end(int index)
    {
        var (manager, _, second, _) = Started();

        Assert.True(manager.MoveWorkspaceTo(second, index).IsSuccess);

        Assert.Equal("Sparrow", NamesOf(manager)[index < 0 ? 0 : 2]);
    }

    [Fact]
    public void Repositioning_an_unknown_workspace_fails() =>
        Assert.True(Started().manager.MoveWorkspaceTo(Guid.NewGuid(), 0).IsFailure);

    // --- insert before / after (#40) --------------------------------------------------------
    //
    // Petre: "ability to rename existing and add new workspaces from within the floating window,
    // on top or under the current workspace, something like a insert before/after."
    //
    // The bar turns a right-clicked row into an index; this is the half that can be tested
    // without one.

    [Fact]
    public void Inserting_before_a_workspace_puts_it_at_that_position()
    {
        var (manager, _, second, _) = Started();

        Assert.True(manager.InsertWorkspace("Fresh", manager.State.Workspaces.ToList().FindIndex(w => w.Id == second)).IsSuccess);

        Assert.Equal(["GEPHA", "Fresh", "Sparrow", "TaskSpace"], NamesOf(manager));
    }

    [Fact]
    public void Inserting_after_a_workspace_puts_it_one_further_on()
    {
        var (manager, _, second, _) = Started();

        Assert.True(manager.InsertWorkspace("Fresh", manager.State.Workspaces.ToList().FindIndex(w => w.Id == second) + 1).IsSuccess);

        Assert.Equal(["GEPHA", "Sparrow", "Fresh", "TaskSpace"], NamesOf(manager));
    }

    // Adding is inserting at the end, and is now literally implemented as that -- so the oldest
    // behaviour on this list has to keep coming out the same way.
    [Fact]
    public void Adding_still_appends()
    {
        var (manager, _, _, _) = Started();

        Assert.True(manager.AddWorkspace("Fresh").IsSuccess);

        Assert.Equal(["GEPHA", "Sparrow", "TaskSpace", "Fresh"], NamesOf(manager));
    }

    // Every caller derives its index from a row that was on screen when the menu opened, and the
    // bar rebuilds constantly -- so an index describing a workspace that has since gone must land
    // the new one somewhere sensible rather than raise at someone who asked for something
    // reasonable. -1 is what FindIndex returns for a row that no longer exists.
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(99, 3)]
    public void An_index_that_no_longer_makes_sense_is_clamped(int index, int lands)
    {
        var (manager, _, _, _) = Started();

        Assert.True(manager.InsertWorkspace("Fresh", index).IsSuccess);

        Assert.Equal("Fresh", NamesOf(manager)[lands]);
    }

    // The guards that AddWorkspace has always had come along, because it is the same method now.
    [Fact]
    public void A_duplicate_name_is_refused_wherever_it_is_inserted()
    {
        var (manager, _, _, _) = Started();

        Assert.True(manager.InsertWorkspace("sparrow", 0).IsFailure);
        Assert.Equal(3, manager.State.Workspaces.Count);
    }

    [Fact]
    public void An_inserted_workspace_gets_a_desktop_of_its_own()
    {
        var (manager, _, second, _) = Started();

        Assert.True(manager.InsertWorkspace("Fresh", 1).IsSuccess);

        var fresh = manager.State.Workspaces.Single(w => w.Name == "Fresh");
        Assert.NotNull(fresh.DesktopId);
        Assert.NotEqual(manager.State.Workspaces.Single(w => w.Id == second).DesktopId, fresh.DesktopId);
    }
}
