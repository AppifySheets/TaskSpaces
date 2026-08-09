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

    // --- the parent's windows are actually THERE (#42) ---------------------------------------
    //
    // Petre: "i want parent's windows to be present in the child workspace, not in the workspace
    // row... but on the desktop." An earlier version drew them as extra icons and he rejected it
    // on sight -- so what is tested now is the OS-level borrow, not a rendering.

    static WindowInfo Window(nint handle, string process) =>
        new(new WindowHandle(handle), (int)handle, process, $@"C:\{process}.exe", $"{process} window", $@"""C:\{process}.exe""");

    (WorkspaceManager manager, Guid parent, Guid child, WindowInfo onParent) NestedWithAParentWindow()
    {
        var (manager, parent, child, _) = Started();
        Assert.True(manager.NestWorkspace(child, parent).IsSuccess);

        var parentDesktop = manager.State.Workspaces.Single(w => w.Id == parent).DesktopId!.Value;
        var onParent = Window(0xA, "slack");
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, onParent));
        desktops.WindowPlacements[onParent.Handle] = parentDesktop;
        return (manager, parent, child, onParent);
    }

    [Fact]
    public void Arriving_in_a_nested_workspace_borrows_the_parents_windows()
    {
        var (manager, _, child, onParent) = NestedWithAParentWindow();
        var childDesktop = manager.State.Workspaces.Single(w => w.Id == child).DesktopId!.Value;

        desktops.CurrentChangedSubject.OnNext(childDesktop);

        Assert.True(desktops.IsPinned(onParent.Handle).Value);
    }

    // Unpinning does NOT send a window home -- it leaves it on whatever desktop is current. So a
    // release that only unpinned would quietly move the parent's windows into the child, which is
    // the one outcome worse than not having the feature.
    [Fact]
    public void Leaving_gives_them_back_to_the_desktop_they_came_from()
    {
        var (manager, parent, child, onParent) = NestedWithAParentWindow();
        var parentDesktop = manager.State.Workspaces.Single(w => w.Id == parent).DesktopId!.Value;
        var childDesktop = manager.State.Workspaces.Single(w => w.Id == child).DesktopId!.Value;
        var elsewhere = manager.State.Workspaces.Single(w => w.Name == "Personal").DesktopId!.Value;

        desktops.CurrentChangedSubject.OnNext(childDesktop);
        desktops.CurrentChangedSubject.OnNext(elsewhere);

        Assert.False(desktops.IsPinned(onParent.Handle).Value);
        Assert.Equal(parentDesktop, desktops.WindowPlacements[onParent.Handle]);
    }

    // A crash while standing in a nested workspace leaves the parent's windows pinned to every
    // desktop. The borrow is written to state.json precisely so the next start can undo it.
    [Fact]
    public void A_borrow_left_by_a_crash_is_repaired_at_startup()
    {
        var (manager, parent, child, onParent) = NestedWithAParentWindow();
        var parentDesktop = manager.State.Workspaces.Single(w => w.Id == parent).DesktopId!.Value;
        var childDesktop = manager.State.Workspaces.Single(w => w.Id == child).DesktopId!.Value;
        desktops.CurrentChangedSubject.OnNext(childDesktop);
        Assert.NotEmpty(manager.State.InheritedPins);

        // A fresh manager over the same state and the same machine: exactly what the next start
        // sees after a kill.
        var restarted = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(restarted.Start().IsSuccess);

        Assert.False(desktops.IsPinned(onParent.Handle).Value);
        Assert.Equal(parentDesktop, desktops.WindowPlacements[onParent.Handle]);
        Assert.Empty(restarted.State.InheritedPins);
    }

    // Where a borrowed window is DRAWN, which took two corrections from Petre to get right.
    //
    // It must not appear in the 📌 row: it is pinned as a mechanism, not as a statement, and that
    // row means "you asked for this on every desktop".
    //
    // It must not appear in the CHILD's row either -- "i don't want to see parent's windows in the
    // children" -- and it must not vanish from its parent's, which is what the first attempt did:
    // "sparrow loses all windows and they're moved to the child, don't do that."
    //
    // So it stays exactly where it lives. Its presence on the child's desktop is a fact about the
    // SCREEN and Windows' own taskbar, not about this bar -- "only show them in the taskbar, as are
    // pinned windows, only for the children".
    [Fact]
    public void A_borrowed_window_stays_in_its_parents_row()
    {
        var (manager, parent, child, onParent) = NestedWithAParentWindow();
        var childDesktop = manager.State.Workspaces.Single(w => w.Id == child).DesktopId!.Value;
        desktops.CurrentDesktopId = childDesktop;
        desktops.CurrentChangedSubject.OnNext(childDesktop);

        var overview = manager.WindowsByWorkspace().Value;

        Assert.DoesNotContain(overview.Pinned, r => r.Window.Handle == onParent.Handle);
        Assert.DoesNotContain(overview.Workspaces.Single(g => g.Workspace.Id == child).Running,
            r => r.Window.Handle == onParent.Handle);
        Assert.Contains(overview.Workspaces.Single(g => g.Workspace.Id == parent).Running,
            r => r.Window.Handle == onParent.Handle);
    }

    // --- creating one from the bar ------------------------------------------------------------

    [Fact]
    public void A_child_can_be_created_directly_under_its_parent()
    {
        var (manager, parent, child, _) = Started();
        Assert.True(manager.NestWorkspace(child, parent).IsSuccess);

        Assert.True(manager.AddChildWorkspace(parent, "Project notes").IsSuccess);

        var created = manager.State.Workspaces.Single(w => w.Name == "Project notes");
        Assert.Equal(parent, created.ParentId);
        // Directly under the parent's existing children rather than at the end of the list, so a
        // child created from a row appears under that row.
        Assert.Equal(2, manager.State.Workspaces.ToList().FindIndex(w => w.Id == created.Id));
    }

    [Fact]
    public void A_child_cannot_be_given_children_of_its_own()
    {
        var (manager, parent, child, _) = Started();
        Assert.True(manager.NestWorkspace(child, parent).IsSuccess);

        Assert.True(manager.AddChildWorkspace(child, "Too deep").IsFailure);
    }
}
