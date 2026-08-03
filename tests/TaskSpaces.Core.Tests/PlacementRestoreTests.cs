using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rehydration;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Tests;

// Petre: "move Beeper to pinned, close the app, open again, it is incorrectly placed in
// GEPHA workspace" -> "last placement in the workspace should be where it's placed when
// started" -> "last placement beats rules, last placement IS the rule".
//
// Two halves are tested here: RECORDING a placement (so it survives the app closing) and
// RE-APPLYING it, both when a window appears later and when it was already open at startup.
public class PlacementRestoreTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    const string BeeperPath = @"C:\Programs\BeeperTexts\Beeper.exe";
    const string BeeperCmd = @"""C:\Programs\BeeperTexts\Beeper.exe"" ";

    static readonly InventoryEntry BeeperEntry = new(BeeperPath, BeeperCmd, "Beeper");

    // A DIFFERENT handle each time on purpose: an Electron app closing to tray destroys and
    // recreates its window, which is precisely why placement must be keyed by identity.
    static WindowInfo BeeperWindow(nint handle) =>
        new(new WindowHandle(handle), 42, "Beeper", BeeperPath, "Beeper | HRIS", BeeperCmd);

    WorkspaceManager Started(AppState state)
    {
        store.Stored = state;
        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    // --- recording -------------------------------------------------------------------

    [Fact]
    public void Pinning_a_window_remembers_it_as_pinned()
    {
        var manager = Started(AppState.Empty);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, BeeperWindow(0x10)));

        Assert.True(manager.PinWindow(new WindowHandle(0x10)).IsSuccess);

        Assert.Contains(store.Stored.PinnedApps, e => RosterIdentity.Of(e) == RosterIdentity.Of(BeeperEntry));
    }

    [Fact]
    public void Unpinning_forgets_it()
    {
        var manager = Started(AppState.Empty with { PinnedApps = [BeeperEntry] });
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, BeeperWindow(0x10)));

        Assert.True(manager.UnpinWindow(new WindowHandle(0x10)).IsSuccess);

        Assert.Empty(store.Stored.PinnedApps);
    }

    [Fact]
    public void Dragging_a_window_onto_a_plain_desktop_remembers_it_as_detached()
    {
        var plain = desktops.Create("Main").Value;
        var manager = Started(AppState.Empty with { PinnedApps = [BeeperEntry] });
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, BeeperWindow(0x10)));

        Assert.True(manager.MoveToDesktop(new WindowHandle(0x10), plain.Id).IsSuccess);

        Assert.Contains(store.Stored.DetachedApps, e => RosterIdentity.Of(e) == RosterIdentity.Of(BeeperEntry));
        Assert.Empty(store.Stored.PinnedApps); // the three states are mutually exclusive
    }

    [Fact]
    public void Assigning_to_a_workspace_clears_pinned_and_detached_memory()
    {
        var work = new Workspace(Guid.NewGuid(), "Work", null);
        var manager = Started(AppState.Empty with
        {
            Workspaces = [work],
            PinnedApps = [BeeperEntry],
            DetachedApps = [BeeperEntry],
        });
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, BeeperWindow(0x10)));

        Assert.True(manager.AssignWindow(new WindowHandle(0x10), work.Id).IsSuccess);

        Assert.Empty(store.Stored.PinnedApps);
        Assert.Empty(store.Stored.DetachedApps);
    }

    // --- re-applying to a window that appears later ----------------------------------

    // THE reported defect, end to end: the app restarts, Beeper's window comes back with a
    // brand-new handle, and it must be re-pinned rather than left in whatever workspace's
    // desktop it happened to materialise on.
    [Fact]
    public void A_remembered_pinned_app_is_repinned_when_its_window_reappears()
    {
        var gepha = new Workspace(Guid.NewGuid(), "GEPHA", null);
        Started(AppState.Empty with { Workspaces = [gepha], PinnedApps = [BeeperEntry] });

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, BeeperWindow(0xBEEF)));

        Assert.Contains(new WindowHandle(0xBEEF), desktops.PinnedWindows);
        Assert.DoesNotContain(new WindowHandle(0xBEEF), desktops.WindowPlacements.Keys); // not dragged into a workspace
    }

    [Fact]
    public void A_remembered_workspace_is_reapplied_when_its_window_reappears()
    {
        var sparrow = new Workspace(Guid.NewGuid(), "Sparrow", null);
        var manager = Started(AppState.Empty with
        {
            Workspaces = [sparrow],
            Inventory = new Dictionary<Guid, IReadOnlyList<InventoryEntry>> { [sparrow.Id] = [BeeperEntry] },
        });

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, BeeperWindow(0x20)));

        Assert.Equal(manager.State.Workspaces.Single().DesktopId, desktops.WindowPlacements[new WindowHandle(0x20)]);
    }

    // Precedence: "last placement IS the rule". A standing rule must not yank a window out
    // of the workspace Petre last put it in.
    [Fact]
    public void Last_placement_beats_a_conflicting_rule()
    {
        var gepha = new Workspace(Guid.NewGuid(), "GEPHA", null);
        var sparrow = new Workspace(Guid.NewGuid(), "Sparrow", null);
        var manager = Started(AppState.Empty with
        {
            Workspaces = [gepha, sparrow],
            WorkspaceRules = [new WorkspaceRule(gepha.Id, RuleMatchKind.ProcessName, "Beeper")],
            Inventory = new Dictionary<Guid, IReadOnlyList<InventoryEntry>> { [sparrow.Id] = [BeeperEntry] },
        });

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, BeeperWindow(0x30)));

        var sparrowDesktop = manager.State.Workspaces.Single(w => w.Name == "Sparrow").DesktopId;
        Assert.Equal(sparrowDesktop, desktops.WindowPlacements[new WindowHandle(0x30)]);
    }

    // ...but a rule still decides for a window with no history: that is the only thing it
    // can do which placement memory structurally cannot.
    [Fact]
    public void A_rule_still_places_a_window_with_no_remembered_placement()
    {
        var gepha = new Workspace(Guid.NewGuid(), "GEPHA", null);
        var manager = Started(AppState.Empty with
        {
            Workspaces = [gepha],
            WorkspaceRules = [new WorkspaceRule(gepha.Id, RuleMatchKind.ProcessName, "Beeper")],
        });

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, BeeperWindow(0x40)));

        Assert.Equal(manager.State.Workspaces.Single().DesktopId, desktops.WindowPlacements[new WindowHandle(0x40)]);
    }

    // --- re-applying at startup to windows that were ALREADY open --------------------

    // Start() used to only RECORD the snapshot; nothing placed a window that was already
    // open, so rules never touched pre-existing windows and the roster was never consulted.
    [Fact]
    public void Startup_moves_an_already_open_window_from_a_plain_desktop_into_its_workspace()
    {
        var plain = desktops.Create("Main").Value;
        var sparrow = new Workspace(Guid.NewGuid(), "Sparrow", null);
        var window = BeeperWindow(0x50);
        monitor.InitialWindows.Add(window);
        desktops.WindowPlacements[window.Handle] = plain.Id; // sitting on an unbound desktop
        desktops.CurrentDesktopId = plain.Id;

        var manager = Started(AppState.Empty with
        {
            Workspaces = [sparrow],
            Inventory = new Dictionary<Guid, IReadOnlyList<InventoryEntry>> { [sparrow.Id] = [BeeperEntry] },
        });

        Assert.Equal(manager.State.Workspaces.Single().DesktopId, desktops.WindowPlacements[window.Handle]);
    }

    // A pin is ADDITIVE — it makes a window appear everywhere rather than moving it — so it
    // is re-applied regardless of which desktop the window currently sits on. Without this,
    // the "only sweep windows on unbound desktops" guard below would leave Petre's Beeper
    // unpinned exactly when it came back inside a workspace, which is the reported bug.
    [Fact]
    public void Startup_repins_a_remembered_pinned_app_even_when_it_sits_on_a_workspace_desktop()
    {
        var gepha = new Workspace(Guid.NewGuid(), "GEPHA", Guid.NewGuid());
        desktops.Desktops.Add(new DesktopInfo(gepha.DesktopId!.Value, "GEPHA"));
        var window = BeeperWindow(0x60);
        monitor.InitialWindows.Add(window);
        desktops.WindowPlacements[window.Handle] = gepha.DesktopId!.Value;

        Started(AppState.Empty with { Workspaces = [gepha], PinnedApps = [BeeperEntry] });

        Assert.Contains(window.Handle, desktops.PinnedWindows);
    }

    // Hand-move protection: if a window already sits on a workspace-bound desktop, the sweep
    // leaves it alone. Petre may have moved it there with Windows' own Task View, which
    // TaskSpaces never saw, and a restart must not undo that.
    [Fact]
    public void Startup_does_not_move_a_window_that_already_sits_on_a_workspace_desktop()
    {
        var gephaDesktop = Guid.NewGuid();
        var gepha = new Workspace(Guid.NewGuid(), "GEPHA", gephaDesktop);
        var sparrowDesktop = Guid.NewGuid();
        var sparrow = new Workspace(Guid.NewGuid(), "Sparrow", sparrowDesktop);
        desktops.Desktops.Add(new DesktopInfo(gephaDesktop, "GEPHA"));
        desktops.Desktops.Add(new DesktopInfo(sparrowDesktop, "Sparrow"));
        var window = BeeperWindow(0x70);
        monitor.InitialWindows.Add(window);
        desktops.WindowPlacements[window.Handle] = gephaDesktop; // already in a workspace

        Started(AppState.Empty with
        {
            Workspaces = [gepha, sparrow],
            Inventory = new Dictionary<Guid, IReadOnlyList<InventoryEntry>> { [sparrow.Id] = [BeeperEntry] },
        });

        Assert.Equal(gephaDesktop, desktops.WindowPlacements[window.Handle]); // untouched
    }
}
