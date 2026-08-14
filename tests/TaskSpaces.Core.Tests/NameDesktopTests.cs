using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Tests;

// #149. Petre: "right-clicking on the default desktop (desktop1) doesn't do anything, no context menu
// for it."
//
// Naming a desktop that ALREADY EXISTS is the operation the app never had. InsertWorkspace creates a
// desktop and names it, which can never adopt Desktop 1 -- the desktop everybody starts on, and the one
// most likely to be sitting there unnamed with real windows in it.
public class NameDesktopTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    // One unnamed desktop the shell already has, and one workspace, so "already bound" has something
    // to be tested against.
    (WorkspaceManager manager, Guid unbound, Guid bound) Started()
    {
        var unbound = Guid.NewGuid();
        var bound = Guid.NewGuid();
        desktops.Desktops.Add(new DesktopInfo(unbound, "Desktop 1"));
        desktops.Desktops.Add(new DesktopInfo(bound, "Work"));
        store.Stored = AppState.Empty with { Workspaces = [new Workspace(Guid.NewGuid(), "Work", bound)] };

        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return (manager, unbound, bound);
    }

    [Fact]
    public void Naming_an_unbound_desktop_creates_a_workspace_on_it()
    {
        var (manager, unbound, _) = Started();

        var named = manager.NameDesktop(unbound, "Reading");

        Assert.True(named.IsSuccess);
        Assert.Equal(unbound, named.Value.DesktopId);
        Assert.Equal("Reading", named.Value.Name);
    }

    // No new desktop, which is the difference from InsertWorkspace and the reason this exists: the
    // windows already on that desktop have to stay where they are.
    [Fact]
    public void Naming_a_desktop_does_not_create_another_one()
    {
        var (manager, unbound, _) = Started();

        manager.NameDesktop(unbound, "Reading");

        Assert.Equal(2, desktops.Desktops.Count);
    }

    // Task View has to agree with the bar, so the shell's own name is set too.
    [Fact]
    public void The_shell_desktop_is_renamed_as_well()
    {
        var (manager, unbound, _) = Started();

        manager.NameDesktop(unbound, "Reading");

        Assert.Equal("Reading", desktops.Desktops.Single(d => d.Id == unbound).Name);
    }

    [Fact]
    public void The_new_workspace_is_appended_last()
    {
        var (manager, unbound, _) = Started();

        manager.NameDesktop(unbound, "Reading");

        Assert.Equal(["Work", "Reading"], manager.State.Workspaces.Select(w => w.Name));
    }

    [Fact]
    public void It_is_persisted()
    {
        var (manager, unbound, _) = Started();

        manager.NameDesktop(unbound, "Reading");

        Assert.Contains(store.Stored.Workspaces, w => w.DesktopId == unbound && w.Name == "Reading");
    }

    // A bound desktop's row already carries the full menu with Rename on it, and quietly renaming that
    // workspace from here would be a different intention answered by the same click.
    [Fact]
    public void A_desktop_that_already_has_a_workspace_is_refused()
    {
        var (manager, _, bound) = Started();

        var named = manager.NameDesktop(bound, "Reading");

        Assert.True(named.IsFailure);
        Assert.Contains("already belongs", named.Error);
    }

    [Fact]
    public void A_refused_name_leaves_the_workspace_list_alone()
    {
        var (manager, _, bound) = Started();

        manager.NameDesktop(bound, "Reading");

        Assert.Single(manager.State.Workspaces);
    }

    // Same rule as every other naming path, and it has teeth: ManageWindow keys a dictionary by name.
    [Fact]
    public void A_name_already_in_use_is_refused()
    {
        var (manager, unbound, _) = Started();

        var named = manager.NameDesktop(unbound, "work");

        Assert.True(named.IsFailure);
        Assert.Contains("already exists", named.Error);
    }

    [Fact]
    public void An_empty_name_is_refused() =>
        Assert.True(Started() is var (manager, unbound, _) && manager.NameDesktop(unbound, "   ").IsFailure);

    // The menu was opened on a row built from an overview that is milliseconds old, and a desktop can
    // be closed in that time. Checked against the shell rather than trusted from the caller.
    [Fact]
    public void A_desktop_that_no_longer_exists_is_refused()
    {
        var (manager, _, _) = Started();

        var named = manager.NameDesktop(Guid.NewGuid(), "Reading");

        Assert.True(named.IsFailure);
        Assert.Contains("no longer exists", named.Error);
    }

    [Fact]
    public void The_name_is_trimmed()
    {
        var (manager, unbound, _) = Started();

        Assert.Equal("Reading", manager.NameDesktop(unbound, "  Reading  ").Value.Name);
    }
}
