using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Tests;

// #132. Petre, after a reboot: "i restarted the computer and taskspaces with it, now i have all
// vscode windows in one of the workspaces", and the fix he asked for: "for vscode, take TaskSpaces
// for the current one, is that the token? attribute TaskSpaces to the correct workspace".
//
// WHY nothing else could place these windows. Seven VS Code windows are one process started with no
// arguments, so RosterIdentity is identical for all of them, and placement memory deliberately
// stands down when another live window shares the identity (SharedIdentityPlacementTests pins that
// rule, and it is right). The folder in the title is the only per-window signal there is, which is
// what TitleToken extracts and what these tests wire to placement.
//
// TWO RULINGS from Petre, both of which these tests exist to hold in place:
//
//   * A folder is learned ONLY when he moves a window by hand. Learning from wherever a window
//     happens to be sitting was rejected for a concrete reason: at the moment he reported this, all
//     seven windows were sitting in one workspace, so observation would have memorised exactly the
//     mess being fixed.
//   * The folder BEATS the roster for that window. The token names one window; the roster names the
//     app. The more specific answer wins, and the roster still answers for a bare editor with no
//     folder open at all.
public class ContainerPlacementTests
{
    const string CodePath = @"C:\Users\p\AppData\Local\Programs\Microsoft VS Code\Code.exe";

    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    Workspace taskSpaces = null!;
    Workspace framework = null!;

    // A VS Code window: one process, no arguments, so every one of these is the SAME identity. The
    // title is the only thing that differs, which is the whole point.
    static WindowInfo Code(nint hwnd, string title) =>
        new(new WindowHandle(hwnd), 21176, "Code", CodePath, title, $"\"{CodePath}\" ");

    const string LoadedTaskSpaces = "WorkspaceManager.cs - TaskSpaces - Visual Studio Code";
    const string LoadedDice = "seed.ts - dice-to-seed - Visual Studio Code";
    const string Bare = "Visual Studio Code";

    WorkspaceManager Started(params WindowInfo[] alreadyOpen)
    {
        taskSpaces = new Workspace(Guid.NewGuid(), "TaskSpace", Guid.NewGuid());
        framework = new Workspace(Guid.NewGuid(), "framework", Guid.NewGuid());
        new[] { taskSpaces, framework }.ToList()
            .ForEach(w => desktops.Desktops.Add(new Abstractions.DesktopInfo(w.DesktopId!.Value, w.Name)));
        desktops.CurrentDesktopId = framework.DesktopId!.Value; // where the reboot left him
        store.Stored = AppState.Empty with { Workspaces = [taskSpaces, framework] };
        monitor.InitialWindows.AddRange(alreadyOpen);

        var manager = new WorkspaceManager(desktops, monitor, titles, store, ownProcessId: 4242);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    static void Appears(FakeMonitor monitor, WindowInfo window) =>
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, window));

    static void Retitled(FakeMonitor monitor, WindowInfo window) =>
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.TitleChanged, window));

    static void Closed(FakeMonitor monitor, WindowInfo window) =>
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Disappeared, window));

    // --- learning -----------------------------------------------------------------------------

    [Fact]
    public void Moving_a_window_by_hand_teaches_where_its_folder_lives()
    {
        var manager = Started();
        Appears(monitor, Code(0x901, LoadedTaskSpaces));

        Assert.True(manager.AssignWindow(new WindowHandle(0x901), taskSpaces.Id).IsSuccess);

        var home = Assert.Single(store.Stored.ContainerHomes);
        Assert.Equal("Code", home.ProcessName);
        Assert.Equal("TaskSpaces", home.Token);
        Assert.Equal(taskSpaces.Id, home.WorkspaceId);
    }

    // Petre, overruling the by-hand-only rule I argued for: "why not learn their positions from where
    // they are, not when they're moved. they are in the correct places now", then "it should take a
    // snapshot of where windows are, rather than where they're moved."
    //
    // He is right, and the objection I raised is answered by ORDERING rather than abandoned. A snapshot
    // on its own would memorise a reboot pile; a snapshot that needs a position to survive two sweeps
    // cannot, because the container tier corrects a window the moment it appears. The test below
    // (Correcting_a_pile_beats_learning_it) is the one that holds that race in place.
    [Fact]
    public void A_window_that_stays_put_teaches_where_its_folder_lives()
    {
        var manager = Started();

        // Sitting in framework, seen, known, drawn in that row. Nobody dragged it through the bar.
        desktops.WindowPlacements[new WindowHandle(0x902)] = framework.DesktopId!.Value;
        Appears(monitor, Code(0x902, LoadedTaskSpaces));

        manager.SnapshotContainerHomes();
        manager.SnapshotContainerHomes();

        var home = Assert.Single(store.Stored.ContainerHomes);
        Assert.Equal("TaskSpaces", home.Token);
        Assert.Equal(framework.Id, home.WorkspaceId);
    }

    // One sweep is a glance, not an answer: a window mid-drag, or parked somewhere for a minute, would
    // otherwise rewrite where its folder lives.
    [Fact]
    public void One_sweep_is_not_enough_for_a_position_to_be_believed()
    {
        var manager = Started();
        desktops.WindowPlacements[new WindowHandle(0x903)] = framework.DesktopId!.Value;
        Appears(monitor, Code(0x903, LoadedTaskSpaces));

        manager.SnapshotContainerHomes();
        Assert.Empty(store.Stored.ContainerHomes);

        // ...and a position that moved in between starts its two sweeps over.
        desktops.WindowPlacements[new WindowHandle(0x903)] = taskSpaces.DesktopId!.Value;
        manager.SnapshotContainerHomes();
        Assert.Empty(store.Stored.ContainerHomes);

        manager.SnapshotContainerHomes();
        Assert.Equal(taskSpaces.Id, Assert.Single(store.Stored.ContainerHomes).WorkspaceId);
    }

    // Two windows with the same folder open in different workspaces have no single answer, so the
    // snapshot declines rather than picking one. Same reasoning that governs placement memory and the
    // launched-by tier: nothing happens rather than a coin toss.
    [Fact]
    public void A_folder_open_in_two_workspaces_at_once_teaches_nothing()
    {
        var manager = Started();
        desktops.WindowPlacements[new WindowHandle(0x904)] = framework.DesktopId!.Value;
        desktops.WindowPlacements[new WindowHandle(0x905)] = taskSpaces.DesktopId!.Value;
        Appears(monitor, Code(0x904, LoadedTaskSpaces));
        Appears(monitor, Code(0x905, LoadedTaskSpaces));

        manager.SnapshotContainerHomes();
        manager.SnapshotContainerHomes();

        Assert.Empty(store.Stored.ContainerHomes);
    }

    // A window on a desktop no workspace owns is somewhere else, not at home.
    [Fact]
    public void A_window_outside_every_workspace_teaches_nothing()
    {
        var manager = Started();
        desktops.WindowPlacements[new WindowHandle(0x906)] = Guid.NewGuid(); // a plain, unnamed desktop
        Appears(monitor, Code(0x906, LoadedTaskSpaces));

        manager.SnapshotContainerHomes();
        manager.SnapshotContainerHomes();

        Assert.Empty(store.Stored.ContainerHomes);
    }

    // THE RACE, and the reason a snapshot is safe at all. A reboot puts every window in one workspace;
    // the container tier corrects them as they appear; the snapshot only ever sees the corrected state.
    // If this ever fails, the snapshot has started memorising exactly the mess #132 was about.
    [Fact]
    public void Correcting_a_pile_beats_learning_it()
    {
        var manager = Started();

        // Taught, however that happened: this folder lives in TaskSpace.
        desktops.WindowPlacements[new WindowHandle(0x907)] = taskSpaces.DesktopId!.Value;
        Appears(monitor, Code(0x907, LoadedTaskSpaces));
        manager.SnapshotContainerHomes();
        manager.SnapshotContainerHomes();
        Assert.Equal(taskSpaces.Id, Assert.Single(store.Stored.ContainerHomes).WorkspaceId);
        Closed(monitor, Code(0x907, LoadedTaskSpaces));

        // The reboot: a new window, dumped on whatever desktop was current.
        desktops.WindowPlacements[new WindowHandle(0x917)] = framework.DesktopId!.Value;
        Appears(monitor, Code(0x917, LoadedTaskSpaces));

        // Corrected on arrival, so both sweeps agree on TaskSpace and the home survives.
        Assert.Equal(taskSpaces.DesktopId, desktops.WindowPlacements[new WindowHandle(0x917)]);
        manager.SnapshotContainerHomes();
        manager.SnapshotContainerHomes();

        Assert.Equal(taskSpaces.Id, Assert.Single(store.Stored.ContainerHomes).WorkspaceId);
    }

    // The same race in the other order: TaskSpaces restarting into a pile, where the windows never pass
    // through OnAppeared at all. RestorePlacements has to correct them, which is why the container tier
    // is exempt from SafeToRestore -- otherwise a window sitting in a workspace would be left there and
    // the next two sweeps would learn it.
    [Fact]
    public void Restarting_into_a_pile_takes_the_windows_home()
    {
        var manager = Started();
        desktops.WindowPlacements[new WindowHandle(0x908)] = taskSpaces.DesktopId!.Value;
        Appears(monitor, Code(0x908, LoadedTaskSpaces));
        manager.SnapshotContainerHomes();
        manager.SnapshotContainerHomes();
        Closed(monitor, Code(0x908, LoadedTaskSpaces));

        // Restart, with the editor already open and its window piled in framework.
        var reopened = new FakeMonitor();
        reopened.InitialWindows.Add(Code(0x918, LoadedTaskSpaces));
        desktops.WindowPlacements[new WindowHandle(0x918)] = framework.DesktopId!.Value;
        var restarted = new WorkspaceManager(desktops, reopened, new FakeTitles(), store, ownProcessId: 4242);
        Assert.True(restarted.Start().IsSuccess);

        Assert.Equal(taskSpaces.DesktopId, desktops.WindowPlacements[new WindowHandle(0x918)]);
    }

    // Moving the same folder somewhere else replaces the answer rather than adding a second one. A
    // folder lives in one place, and two homes for one token would be a coin toss.
    [Fact]
    public void Teaching_the_same_folder_twice_keeps_only_the_later_answer()
    {
        var manager = Started();
        Appears(monitor, Code(0x903, LoadedTaskSpaces));

        Assert.True(manager.AssignWindow(new WindowHandle(0x903), taskSpaces.Id).IsSuccess);
        Assert.True(manager.AssignWindow(new WindowHandle(0x903), framework.Id).IsSuccess);

        var home = Assert.Single(store.Stored.ContainerHomes);
        Assert.Equal(framework.Id, home.WorkspaceId);
    }

    // Two folders of the SAME app are two independent answers. This is the case the roster cannot
    // express at all, and the reason the whole feature exists.
    [Fact]
    public void Two_folders_of_one_app_live_in_two_workspaces()
    {
        var manager = Started();
        Appears(monitor, Code(0x904, LoadedTaskSpaces));
        Appears(monitor, Code(0x905, LoadedDice));

        Assert.True(manager.AssignWindow(new WindowHandle(0x904), taskSpaces.Id).IsSuccess);
        Assert.True(manager.AssignWindow(new WindowHandle(0x905), framework.Id).IsSuccess);

        Assert.Equal(taskSpaces.Id, store.Stored.ContainerHomes.Single(h => h.Token == "TaskSpaces").WorkspaceId);
        Assert.Equal(framework.Id, store.Stored.ContainerHomes.Single(h => h.Token == "dice-to-seed").WorkspaceId);
    }

    // --- placing ------------------------------------------------------------------------------

    [Fact]
    public void A_window_that_reopens_with_a_known_folder_goes_home()
    {
        var manager = Started();
        Appears(monitor, Code(0x906, LoadedTaskSpaces));
        Assert.True(manager.AssignWindow(new WindowHandle(0x906), taskSpaces.Id).IsSuccess);
        Closed(monitor, Code(0x906, LoadedTaskSpaces));

        // The reboot: a new hwnd, opened on whatever desktop he happened to be standing on.
        Appears(monitor, Code(0x916, LoadedTaskSpaces));

        Assert.Equal(taskSpaces.DesktopId, desktops.WindowPlacements[new WindowHandle(0x916)]);
    }

    // Petre, when TitleToken was first written: "so i open vscode, then i load a folder in it, that
    // should take it to the correct workspace." A window exists before its folder has loaded, so the
    // title change is the only moment the folder can be acted on.
    //
    // The middle assertion is the one that shaped the code. A bare editor window is placed AT ONCE by
    // the roster, so by the time the folder loads the window already has a membership -- and every
    // other tier here refuses to touch a placed window. If the container tier did the same, the answer
    // would always arrive too late to be used. So it is the one tier allowed to correct a placement,
    // once per folder.
    [Fact]
    public void A_folder_loaded_after_the_window_opened_still_takes_it_home()
    {
        var manager = Started();

        // Taught: this folder lives in TaskSpace.
        Appears(monitor, Code(0x907, LoadedTaskSpaces));
        Assert.True(manager.AssignWindow(new WindowHandle(0x907), taskSpaces.Id).IsSuccess);
        Closed(monitor, Code(0x907, LoadedTaskSpaces));

        // Taught separately: the APP lives in framework. A bare window, so no folder is learned.
        Appears(monitor, Code(0x908, Bare));
        Assert.True(manager.AssignWindow(new WindowHandle(0x908), framework.Id).IsSuccess);
        Closed(monitor, Code(0x908, Bare));

        // Opens bare, so the roster answers and puts it in framework. Correct, and not the end of it.
        Appears(monitor, Code(0x917, Bare));
        Assert.Equal(framework.DesktopId, desktops.WindowPlacements[new WindowHandle(0x917)]);

        Retitled(monitor, Code(0x917, LoadedTaskSpaces));

        Assert.Equal(taskSpaces.DesktopId, desktops.WindowPlacements[new WindowHandle(0x917)]);
    }

    // The precedence ruling. The roster says Code.exe lives in framework; the folder says this
    // window's project lives in TaskSpace. The folder is about this window, so it wins.
    [Fact]
    public void The_folder_beats_what_the_roster_says_about_the_app()
    {
        var manager = Started();

        // Taught the folder first...
        Appears(monitor, Code(0x908, LoadedTaskSpaces));
        Assert.True(manager.AssignWindow(new WindowHandle(0x908), taskSpaces.Id).IsSuccess);
        Closed(monitor, Code(0x908, LoadedTaskSpaces));

        // ...then taught the APP, with a window that has no folder open at all. AddEntry strips the
        // identity from every other workspace, so the roster now says framework and only framework.
        Appears(monitor, Code(0x909, Bare));
        Assert.True(manager.AssignWindow(new WindowHandle(0x909), framework.Id).IsSuccess);
        Closed(monitor, Code(0x909, Bare));

        Appears(monitor, Code(0x919, LoadedTaskSpaces));

        Assert.Equal(taskSpaces.DesktopId, desktops.WindowPlacements[new WindowHandle(0x919)]);
    }

    // ...and the other half of that: with no folder to go on, the roster is still the answer. The
    // token tier must not swallow the case placement memory was built for.
    [Fact]
    public void With_no_folder_open_the_roster_still_places_the_window()
    {
        var manager = Started();
        Appears(monitor, Code(0x910, Bare));
        Assert.True(manager.AssignWindow(new WindowHandle(0x910), framework.Id).IsSuccess);
        Closed(monitor, Code(0x910, Bare));

        Appears(monitor, Code(0x920, Bare));

        Assert.Equal(framework.DesktopId, desktops.WindowPlacements[new WindowHandle(0x920)]);
    }

    // A folder placement is about ONE WINDOW, so it must not claim the app for that workspace.
    // RosterAdd strips an identity from every other workspace, and that is precisely the mechanism
    // behind "i'm starting the edge browser and it immediately goes to personal": an inference about
    // one window must never rewrite what Petre taught the app by hand. Same reasoning as #94.
    [Fact]
    public void Going_home_does_not_move_the_app_in_the_roster()
    {
        var manager = Started();
        Appears(monitor, Code(0x911, LoadedTaskSpaces));
        Assert.True(manager.AssignWindow(new WindowHandle(0x911), taskSpaces.Id).IsSuccess);
        Closed(monitor, Code(0x911, LoadedTaskSpaces));

        Appears(monitor, Code(0x912, Bare));
        Assert.True(manager.AssignWindow(new WindowHandle(0x912), framework.Id).IsSuccess);
        Closed(monitor, Code(0x912, Bare));

        Appears(monitor, Code(0x921, LoadedTaskSpaces));

        // Placed in TaskSpace by its folder, and the roster still says the APP lives in framework.
        Assert.Equal(taskSpaces.DesktopId, desktops.WindowPlacements[new WindowHandle(0x921)]);
        Assert.Empty(store.Stored.Inventory.GetValueOrDefault(taskSpaces.Id, []));
        Assert.Single(store.Stored.Inventory.GetValueOrDefault(framework.Id, []));
    }

    // --- the rename trap ----------------------------------------------------------------------

    // Petre's own rename rule is `ProcessName Code -> VSC`, so every VS Code window on his machine is
    // titled exactly "VSC" on screen and the folder is nowhere to be seen. The ledger already shows
    // what that costs: it holds an entry reading OriginalTitle "VSC", a rename recorded against a
    // title the app had itself rewritten.
    //
    // So the folder has to be taken from the title the APP wrote, at the moment it writes it, and our
    // own short name must never be mistaken for a folder.
    [Fact]
    public void Our_own_short_name_is_never_learned_as_a_folder()
    {
        var manager = Started();

        // Renamed by APP, which is the rule he actually has: keyed on the process, so it covers every
        // VS Code window and survives every title rewrite.
        Appears(monitor, Code(0x913, Bare));
        Assert.True(manager.RenameApp(new WindowHandle(0x913), "VSC").IsSuccess);
        Assert.Equal("VSC", titles.Titles[new WindowHandle(0x913)]);

        // VS Code then loads the folder and writes its own title. We note the folder and re-apply.
        Retitled(monitor, Code(0x913, LoadedTaskSpaces));
        // ...which produces a NAMECHANGE carrying OUR name back at us. It is not a folder.
        Retitled(monitor, Code(0x913, "VSC"));

        Assert.True(manager.AssignWindow(new WindowHandle(0x913), taskSpaces.Id).IsSuccess);

        var home = Assert.Single(store.Stored.ContainerHomes);
        Assert.Equal("TaskSpaces", home.Token);
    }

    // --- housekeeping -------------------------------------------------------------------------

    // A home pointing at a workspace that no longer exists would move a window to a desktop nothing
    // draws. Deleting the workspace forgets its folders, in the same breath as its roster and rules.
    [Fact]
    public void Deleting_a_workspace_forgets_the_folders_that_lived_in_it()
    {
        var manager = Started();
        Appears(monitor, Code(0x914, LoadedTaskSpaces));
        Assert.True(manager.AssignWindow(new WindowHandle(0x914), taskSpaces.Id).IsSuccess);
        Closed(monitor, Code(0x914, LoadedTaskSpaces));

        Assert.True(manager.RemoveWorkspace(taskSpaces.Id).IsSuccess);
        Assert.Empty(store.Stored.ContainerHomes);

        // And a window with that folder is left alone rather than moved somewhere arbitrary.
        Appears(monitor, Code(0x924, LoadedTaskSpaces));
        Assert.False(desktops.WindowPlacements.ContainsKey(new WindowHandle(0x924)));
    }

    // Titles change constantly -- every file you open in VS Code rewrites one -- and each one is a
    // chance to re-issue a move. That matters more than it sounds: moving a window that has focus
    // takes Windows to that desktop, so a tier that can fire twice can bounce the desktop with no
    // input at all. Petre has reported exactly that once already (#94's guard exists for it).
    [Fact]
    public void A_window_is_taken_home_once_per_folder_however_often_its_title_changes()
    {
        var manager = Started();
        Appears(monitor, Code(0x915, LoadedTaskSpaces));
        Assert.True(manager.AssignWindow(new WindowHandle(0x915), taskSpaces.Id).IsSuccess);
        Closed(monitor, Code(0x915, LoadedTaskSpaces));

        // The app itself lives in framework, so a bare window opens there and the folder loading really
        // does have somewhere to move it. Without this the window would open in its home already, and
        // the test would pass by measuring nothing.
        Appears(monitor, Code(0x916, Bare));
        Assert.True(manager.AssignWindow(new WindowHandle(0x916), framework.Id).IsSuccess);
        Closed(monitor, Code(0x916, Bare));

        // Counting starts after it has opened, so what is measured is the container tier alone rather
        // than the roster placement that happens the moment a bare window appears.
        Appears(monitor, Code(0x925, Bare));
        Assert.Equal(framework.DesktopId, desktops.WindowPlacements[new WindowHandle(0x925)]);

        var moves = 0;
        desktops.OnMoveWindow = (w, _) => { if (w == new WindowHandle(0x925)) moves++; };

        Retitled(monitor, Code(0x925, LoadedTaskSpaces));
        Retitled(monitor, Code(0x925, "README.md - TaskSpaces - Visual Studio Code"));
        Retitled(monitor, Code(0x925, "build.cs - TaskSpaces - Visual Studio Code"));

        Assert.Equal(1, moves);
    }
}
