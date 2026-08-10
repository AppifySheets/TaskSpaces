using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Tests;

// #94 in the pipeline: what actually happens to a window when the app that started it lives in a
// workspace. The walk itself is pinned down in LaunchedByTests; what is decided here is the placement,
// its precedence against memory and rules, and the side effects it deliberately does NOT have.
public class LaunchedByPlacementTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();
    readonly FakeProcessTree processes = new();

    static WindowInfo Window(nint handle, int pid, string process) =>
        new(new WindowHandle(handle), pid, process, $@"C:\{process}.exe", $"{process} window", $@"""C:\{process}.exe""");

    // The editor, sitting in Gepha with a window we track. Its pid is 700, matching the chains below.
    readonly WindowInfo editor = Window(0x1, 700, "Code");

    Guid gepha;
    Guid personal;

    // Gepha is where the editor lives; Personal is the desktop we are standing on, so a new window
    // appears there and has somewhere to be moved FROM.
    WorkspaceManager Started(bool withProcesses = true)
    {
        var here = new Workspace(Guid.NewGuid(), "Personal", Guid.NewGuid());
        var there = new Workspace(Guid.NewGuid(), "Gepha", Guid.NewGuid());
        new[] { here, there }.ToList()
            .ForEach(w => desktops.Desktops.Add(new DesktopInfo(w.DesktopId!.Value, w.Name)));
        store.Stored = AppState.Empty with { Workspaces = [here, there] };

        personal = here.Id;
        gepha = there.Id;
        desktops.CurrentDesktopId = here.DesktopId!.Value;

        var manager = new WorkspaceManager(desktops, monitor, titles, store,
            processes: withProcesses ? processes : null);
        Assert.True(manager.Start().IsSuccess);

        // The editor's window exists and has been claimed by Gepha, the same way a drag would claim it.
        desktops.WindowPlacements[editor.Handle] = desktops.CurrentDesktopId;
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, editor));
        Assert.True(manager.AssignWindow(editor.Handle, gepha).IsSuccess);

        return manager;
    }

    // The measured chain: chrome ← node ← cmd ← claude ← Code(helper) ← Code(window) ← explorer.
    void EditorOpenedIt(nint handle, int pid) =>
        processes.Chain(($"child{handle}", pid), ("node", 800), ("cmd", 801), ("claude", 802), ("Code", 803), ("Code", 700), ("explorer", 12));

    // A window appears the way the OS produces one: on the desktop you are standing on. Recorded in the
    // fake BEFORE the event, so anything the pipeline decides is layered on top of that rather than on
    // a window with no desktop at all.
    WindowInfo Appears(WorkspaceManager manager, nint handle, int pid, string name = "chrome")
    {
        var window = Window(handle, pid, name);
        desktops.WindowPlacements[window.Handle] = desktops.CurrentDesktopId;
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, window));
        return window;
    }

    Guid? WorkspaceOf(WorkspaceManager manager, WindowInfo window) =>
        manager.WindowsByWorkspace().Value.Workspaces
            .Where(g => g.Running.Any(r => r.Window.Handle == window.Handle))
            .Select(g => (Guid?)g.Workspace.Id)
            .FirstOrDefault();

    [Fact]
    public void A_window_opened_by_an_app_joins_that_apps_workspace()
    {
        var manager = Started();
        EditorOpenedIt(0x2, 900);

        var browser = Appears(manager, 0x2, 900);

        Assert.Equal(gepha, WorkspaceOf(manager, browser));
    }

    // The side effect this must NOT have. RosterAdd strips an identity from every other workspace so
    // exactly one can claim it, which is the mechanism behind Petre's "i'm starting the edge browser and
    // it immediately goes to personal". An inference about one window must not rewrite where he says
    // the app lives.
    [Fact]
    public void It_does_not_claim_the_app_belongs_to_that_workspace()
    {
        var manager = Started();
        EditorOpenedIt(0x2, 900);

        var browser = Appears(manager, 0x2, 900);

        // Gepha holds the editor, because that WAS an explicit act. What it must not hold is the window
        // the editor opened.
        Assert.DoesNotContain(
            manager.State.Inventory.SelectMany(entry => entry.Value),
            entry => entry.ProcessPath.Contains(browser.ProcessName, StringComparison.OrdinalIgnoreCase));
    }

    // A launcher on the desktop you are standing on needs nothing done: Windows already opens new
    // windows there. Skipping it keeps the ordinary case free of any recording at all.
    [Fact]
    public void A_launcher_on_the_current_desktop_changes_nothing()
    {
        var manager = Started();
        // Standing in Gepha now, which is where the editor is.
        desktops.CurrentDesktopId = manager.State.Workspaces.Single(w => w.Id == gepha).DesktopId!.Value;
        EditorOpenedIt(0x2, 900);

        // Counted rather than inferred from the result: the window would END UP in Gepha either way,
        // since that is the desktop it opened on. What the guard saves is the work, so the work is what
        // is asserted -- no desktop move at all.
        var moves = 0;
        desktops.OnMoveWindow = (window, _) => { if (window.Value == 0x2) moves++; };

        Appears(manager, 0x2, 900);

        Assert.Equal(0, moves);
    }

    // Two windows of the launcher in two different workspaces gives no single answer, and the codebase
    // already answers that situation the same way for placement memory: do nothing rather than guess.
    [Fact]
    public void A_launcher_living_in_two_workspaces_places_nothing()
    {
        var manager = Started();
        var second = Window(0x9, 700, "Code");
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, second));
        Assert.True(manager.AssignWindow(second.Handle, personal).IsSuccess);
        EditorOpenedIt(0x2, 900);

        var browser = Appears(manager, 0x2, 900);

        Assert.Equal(personal, WorkspaceOf(manager, browser)); // wherever it opened, untouched
    }

    // Precedence, and the reason for it: launched-by is about THIS window and is the fresher intent,
    // while memory is about the app in general. Memory pulling a window away from the app that just
    // opened it is the complaint that started all of this.
    [Fact]
    public void Launched_by_beats_placement_memory()
    {
        var manager = Started();
        // Teach memory that chrome belongs in Personal, by placing a chrome window there by hand.
        var earlier = Appears(manager, 0x3, 901);
        Assert.True(manager.AssignWindow(earlier.Handle, personal).IsSuccess);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Disappeared, earlier));

        EditorOpenedIt(0x2, 900);
        var browser = Appears(manager, 0x2, 900);

        Assert.Equal(gepha, WorkspaceOf(manager, browser));
    }

    [Fact]
    public void Launched_by_beats_a_workspace_rule()
    {
        var manager = Started();
        Assert.True(manager.SetRules([new WorkspaceRule(personal, RuleMatchKind.ProcessName, "chrome")], []).IsSuccess);
        EditorOpenedIt(0x2, 900);

        var browser = Appears(manager, 0x2, 900);

        Assert.Equal(gepha, WorkspaceOf(manager, browser));
    }

    // With no launcher, everything downstream still works exactly as before. This is the guard against
    // the new tier swallowing the old ones.
    [Fact]
    public void With_no_launcher_a_rule_still_places_the_window()
    {
        var manager = Started();
        Assert.True(manager.SetRules([new WorkspaceRule(gepha, RuleMatchKind.ProcessName, "chrome")], []).IsSuccess);
        // Started by the shell, so there is no launcher.
        processes.Chain(("chrome", 900), ("explorer", 12));

        var browser = Appears(manager, 0x2, 900);

        Assert.Equal(gepha, WorkspaceOf(manager, browser));
    }

    // Compatibility mode, and every caller predating this: no process tree, no launched-by tier, and
    // placement behaves as it always did.
    [Fact]
    public void Without_a_process_tree_nothing_is_launched_by_anything()
    {
        var manager = Started(withProcesses: false);
        EditorOpenedIt(0x2, 900);

        var browser = Appears(manager, 0x2, 900);

        Assert.Equal(personal, WorkspaceOf(manager, browser));
    }

    // A window Petre has already dragged out of every workspace by hand stays out: AutoPlaceable guards
    // the whole tier, and this makes sure the new one sits inside that guard rather than beside it.
    [Fact]
    public void A_window_already_placed_by_hand_is_not_moved()
    {
        var manager = Started();
        EditorOpenedIt(0x2, 900);
        var browser = Appears(manager, 0x2, 900);
        Assert.True(manager.AssignWindow(browser.Handle, personal).IsSuccess);

        // A second Appeared for the same window, which Resync produces routinely.
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, browser));

        Assert.Equal(personal, WorkspaceOf(manager, browser));
    }
}
