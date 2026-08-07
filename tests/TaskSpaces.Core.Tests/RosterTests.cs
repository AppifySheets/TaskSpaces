using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Tests;

public class RosterTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    (WorkspaceManager Manager, Workspace Work, Workspace Personal) Started()
    {
        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        var work = manager.AddWorkspace("Work").Value;
        var personal = manager.AddWorkspace("Personal").Value;
        return (manager, work, personal);
    }

    static WindowInfo Rider(nint hwnd, string sln, string title) =>
        new(new WindowHandle(hwnd), 100, "rider64", @"C:\rider\rider64.exe", title, $"\"C:\\rider\\rider64.exe\" {sln}");

    [Fact]
    public void Same_identity_moves_between_workspaces_never_duplicates()
    {
        var (manager, work, personal) = Started();
        var window = Rider(0x10, "X.sln", "X");

        manager.AssignWindow(NextAppeared(window), work.Id);
        Assert.Single(store.Stored.Inventory[work.Id]);

        manager.AssignWindow(window.Handle, personal.Id);
        Assert.Empty(store.Stored.Inventory[work.Id]);           // moved, not copied
        Assert.Single(store.Stored.Inventory[personal.Id]);
    }

    [Fact]
    public void Different_content_same_app_rosters_in_different_workspaces()
    {
        var (manager, work, personal) = Started();
        manager.AssignWindow(NextAppeared(Rider(0x10, "X.sln", "X")), work.Id);
        manager.AssignWindow(NextAppeared(Rider(0x11, "Y.sln", "Y")), personal.Id);
        Assert.Single(store.Stored.Inventory[work.Id]);
        Assert.Single(store.Stored.Inventory[personal.Id]);
    }

    [Fact]
    public void Manual_add_and_remove_roster_entry()
    {
        var (manager, work, _) = Started();
        var added = manager.AddRosterEntry(work.Id, @"C:\Tools\gitextensions.exe", "browse C:\\repos\\X");
        Assert.True(added.IsSuccess);
        Assert.Single(store.Stored.Inventory[work.Id]);

        Assert.True(manager.RemoveRosterEntry(work.Id, added.Value).IsSuccess);
        Assert.Empty(store.Stored.Inventory[work.Id]);
    }

    [Fact]
    public void Late_placement_moves_an_unplaced_window_when_its_title_changes()
    {
        var (manager, work, _) = Started();
        manager.SetRules([new WorkspaceRule(work.Id, RuleMatchKind.TitleRegex, "TaskSpaces")], []);

        var bare = Rider(0x10, "", "JetBrains Rider");            // opened bare: no rule matches
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, bare));
        Assert.Empty(desktops.WindowPlacements);

        // Petre loads the solution -> Rider rewrites its title -> NOW the rule matches.
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.TitleChanged, bare with { Title = "TaskSpaces – rider" }));
        Assert.Equal(work.DesktopId, desktops.WindowPlacements[bare.Handle]);
    }

    // Fix round 1 (reviewer, Important): ApplyRename's titles.Set produces a genuine
    // NAMECHANGE, which re-enters OnTitleChanged as an "echo" carrying OUR OWN short
    // name as window.Title. If the window is still unplaced, late placement must NOT
    // run workspace rules against that synthetic title -- only titles the APP itself
    // wrote are legitimate placement signals.
    [Fact]
    public void Late_placement_does_not_fire_on_the_echo_of_our_own_rename()
    {
        var (manager, work, _) = Started();
        manager.SetRules(
            [new WorkspaceRule(work.Id, RuleMatchKind.TitleRegex, "Amy")],
            [new RenameRule(RuleMatchKind.ProcessName, "rider64", "Amy related")]);

        var bare = Rider(0x10, "", "JetBrains Rider");   // natural title doesn't match "Amy"
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, bare));
        Assert.Empty(desktops.WindowPlacements);         // rename applied, not placed

        // WM_SETTEXT echo: the OS reports our own rename back as a NAMECHANGE, with
        // Title now "Amy related" -- which WOULD match the workspace's TitleRegex "Amy"
        // if late placement blindly re-ran rules against it.
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.TitleChanged, bare with { Title = "Amy related" }));

        Assert.Empty(desktops.WindowPlacements); // must stay unplaced -- that title is ours, not the app's
    }

    [Fact]
    public void Placed_windows_are_never_re_placed_by_title_changes()
    {
        var (manager, work, personal) = Started();
        manager.SetRules([new WorkspaceRule(personal.Id, RuleMatchKind.TitleRegex, "Sparrow")], []);

        var window = Rider(0x10, "X.sln", "TaskSpaces – rider");
        manager.AssignWindow(NextAppeared(window), work.Id);      // Petre put it in Work by hand

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.TitleChanged, window with { Title = "Sparrow – rider" }));
        Assert.Equal(work.DesktopId, desktops.WindowPlacements[window.Handle]); // stayed put
    }

    [Fact]
    public void RemoveWorkspace_prunes_memberships_no_phantom_roster_resurrection()
    {
        var (manager, work, _) = Started();
        var window = Rider(0x10, "X.sln", "X");
        manager.AssignWindow(NextAppeared(window), work.Id);

        Assert.True(manager.RemoveWorkspace(work.Id).IsSuccess);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Disappeared, window));

        Assert.False(store.Stored.Inventory.ContainsKey(work.Id)); // deleted stays deleted
    }

    WindowHandle NextAppeared(WindowInfo window)
    {
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, window));
        return window.Handle;
    }
}
