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
}
