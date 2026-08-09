using CSharpFunctionalExtensions;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Overview;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Tests;

// Petre: "a workspace can be nested under a main (parent) workspace... everything from the main
// workspace is pinned to the nested ones... a nested workspace is easily identifiable as nested."
//
// Nesting is app-level metadata over flat OS desktops -- the same move this app already makes by
// naming them -- so all of it is decidable here, without a desktop shell.
public class NestedWorkspaceTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    (WorkspaceManager manager, Guid parent, Guid child, Guid other) Started()
    {
        var parent = new Workspace(Guid.NewGuid(), "Project", Guid.NewGuid());
        var child = new Workspace(Guid.NewGuid(), "Project docs", Guid.NewGuid());
        var other = new Workspace(Guid.NewGuid(), "Personal", Guid.NewGuid());
        new[] { parent, child, other }.ToList()
            .ForEach(w => desktops.Desktops.Add(new DesktopInfo(w.DesktopId!.Value, w.Name)));
        store.Stored = AppState.Empty with { Workspaces = [parent, child, other] };

        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return (manager, parent.Id, child.Id, other.Id);
    }

    static Guid? ParentOf(WorkspaceManager manager, Guid id) =>
        manager.State.Workspaces.Single(w => w.Id == id).ParentId;

    [Fact]
    public void A_workspace_starts_at_the_top_level() =>
        Assert.All(Started().manager.State.Workspaces, w => Assert.Null(w.ParentId));

    [Fact]
    public void Nesting_records_the_parent()
    {
        var (manager, parent, child, _) = Started();

        Assert.True(manager.NestWorkspace(child, parent).IsSuccess);

        Assert.Equal(parent, ParentOf(manager, child));
        Assert.Equal(parent, store.Stored.Workspaces.Single(w => w.Id == child).ParentId);
    }

    [Fact]
    public void Un_nesting_returns_it_to_the_top_level()
    {
        var (manager, parent, child, _) = Started();
        Assert.True(manager.NestWorkspace(child, parent).IsSuccess);

        Assert.True(manager.UnnestWorkspace(child).IsSuccess);

        Assert.Null(ParentOf(manager, child));
    }

    // A cycle of length one, and every walk over the tree would run forever.
    [Fact]
    public void A_workspace_cannot_be_nested_under_itself()
    {
        var (manager, parent, _, _) = Started();

        Assert.True(manager.NestWorkspace(parent, parent).IsFailure);
    }

    // One level deep, both ways round. Not a limitation of the model -- a Guid? expresses any tree
    // -- but of what can be read at a glance on a bar ten rows tall.
    [Fact]
    public void Nesting_under_an_already_nested_workspace_is_refused()
    {
        var (manager, parent, child, other) = Started();
        Assert.True(manager.NestWorkspace(child, parent).IsSuccess);

        Assert.True(manager.NestWorkspace(other, child).IsFailure);
        Assert.Null(ParentOf(manager, other));
    }

    [Fact]
    public void Nesting_a_workspace_that_has_children_is_refused()
    {
        var (manager, parent, child, other) = Started();
        Assert.True(manager.NestWorkspace(child, parent).IsSuccess);

        Assert.True(manager.NestWorkspace(parent, other).IsFailure);
        Assert.Null(ParentOf(manager, parent));
    }

    [Fact]
    public void Nesting_an_unknown_workspace_fails()
    {
        var (manager, parent, _, _) = Started();

        Assert.True(manager.NestWorkspace(Guid.NewGuid(), parent).IsFailure);
        Assert.True(manager.NestWorkspace(parent, Guid.NewGuid()).IsFailure);
    }

    // Deleting a workspace deletes ONE workspace. Taking its children with it would destroy
    // desktops full of windows on the strength of a grouping decision, and leaving the ParentId
    // dangling would draw rows nested under nothing.
    [Fact]
    public void Removing_a_parent_promotes_its_children()
    {
        var (manager, parent, child, _) = Started();
        Assert.True(manager.NestWorkspace(child, parent).IsSuccess);

        Assert.True(manager.RemoveWorkspace(parent).IsSuccess);

        Assert.Contains(manager.State.Workspaces, w => w.Id == child);
        Assert.Null(ParentOf(manager, child));
    }

    // --- what a nested workspace SHOWS ------------------------------------------------------

    static WindowInfo Window(nint handle, string process) =>
        new(new WindowHandle(handle), (int)handle, process, $@"C:\{process}.exe", $"{process} window", $@"""C:\{process}.exe""");

    [Fact]
    public void A_nested_workspace_shows_its_parents_windows_separately()
    {
        var (manager, parent, child, _) = Started();
        Assert.True(manager.NestWorkspace(child, parent).IsSuccess);

        var parentDesktop = manager.State.Workspaces.Single(w => w.Id == parent).DesktopId!.Value;
        var childDesktop = manager.State.Workspaces.Single(w => w.Id == child).DesktopId!.Value;
        var onParent = Window(0xA, "slack");
        var onChild = Window(0xB, "code");
        desktops.WindowPlacements[onParent.Handle] = parentDesktop;
        desktops.WindowPlacements[onChild.Handle] = childDesktop;

        var overview = OverviewBuilder.Build(
            manager.State,
            [onParent, onChild],
            _ => Maybe<string>.None,
            new HashSet<WindowHandle>(),
            new Dictionary<WindowHandle, Guid> { [onParent.Handle] = parentDesktop, [onChild.Handle] = childDesktop },
            [new DesktopInfo(parentDesktop, "Project"), new DesktopInfo(childDesktop, "Project docs")],
            childDesktop);

        var nested = overview.Workspaces.Single(g => g.Workspace.Id == child);
        // Its own windows stay its own -- the inherited ones are NOT merged into Running, because
        // they are not on this desktop and everything downstream (drag targets, counts, placement
        // memory) would inherit that lie.
        Assert.Equal(onChild.Handle, Assert.Single(nested.Running).Window.Handle);
        Assert.Equal(onParent.Handle, Assert.Single(nested.Inherited).Window.Handle);
    }

    [Fact]
    public void A_top_level_workspace_inherits_nothing()
    {
        var (manager, parent, _, _) = Started();
        var desktop = manager.State.Workspaces.Single(w => w.Id == parent).DesktopId!.Value;

        var overview = OverviewBuilder.Build(
            manager.State, [], _ => Maybe<string>.None, new HashSet<WindowHandle>(),
            new Dictionary<WindowHandle, Guid>(), [new DesktopInfo(desktop, "Project")], desktop);

        Assert.All(overview.Workspaces, g => Assert.Empty(g.Inherited));
    }
}
