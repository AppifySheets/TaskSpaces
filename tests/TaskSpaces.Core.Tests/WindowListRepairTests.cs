using System.Reactive.Linq;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Tests;

// Petre: "i also don't see obs in the notary workspace", and in the same breath, "those serena
// windows are absent from taskbar".
//
// One cause, measured on his machine with the app's own code. There are two window lists, and only
// one of them is ever repaired:
//
//   * WindowMonitor.Resync reconciles the MONITOR's list against the OS every five seconds. Its
//     adopt half returns immediately for anything already in `known`, and its drop half removes
//     handles that fail IsWindow.
//   * WorkspaceManager's list is fed purely by the monitor's EVENTS, and reconciles against nothing.
//
// So the moment the two disagree, they stay disagreed for the life of the process. OBS recreates its
// native window (Qt does that), and his bar was left holding the destroyed handle 0x1C13F2 while the
// live one, 0xF11DA, sat in the monitor's list and never reached the manager. Nineteen hours of
// sweeps could not help: to Resync, that window was already known.
//
//   probe: obs 0xF11DA is a candidate: True    FromHwnd(obs).HasValue = True
//   bar:   band notary: msedge, Code, Docker Desktop, StartAllBackCfg, 9 x python   (no obs)
//
// Restarting the app fixed it, which is the tell: nothing was wrong with the machine, only with what
// the app remembered about it. These tests cover the repair that makes a restart stop being the cure.
public class WindowListRepairTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeStore store = new();

    static WindowInfo Obs(nint hwnd = 0xF11DA) =>
        new(new WindowHandle(hwnd), 700, "obs64", @"C:\Program Files\obs-studio\bin\64bit\obs64.exe", "OBS 32.2.1", null);

    static WindowInfo Beeper() =>
        new(new WindowHandle(0x206E4), 800, "Beeper", @"C:\apps\Beeper.exe", "Beeper", null);

    Workspace GivenAWorkspace(string name = "notary")
    {
        var workspace = new Workspace(Guid.NewGuid(), name, Guid.NewGuid());
        desktops.Desktops.Add(new DesktopInfo(workspace.DesktopId!.Value, name));
        desktops.CurrentDesktopId = workspace.DesktopId!.Value;
        store.Stored = store.Stored with { Workspaces = [workspace] };
        return workspace;
    }

    WorkspaceManager Started()
    {
        var manager = new WorkspaceManager(desktops, monitor, new FakeTitles(), store, ownProcessId: 4242);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    // The OBS half. The window is listed by the OS and missing from the manager, and no event is ever
    // coming for it, because the monitor believes it announced this one long ago.
    [Fact]
    public void A_window_the_manager_never_heard_about_is_adopted()
    {
        var workspace = GivenAWorkspace();
        var manager = Started();

        // Listed by the OS, never announced: the event was dropped, or announced before the manager
        // was listening. Either way the manager cannot know it exists.
        monitor.InitialWindows.Add(Obs());
        desktops.WindowPlacements[Obs().Handle] = workspace.DesktopId!.Value;
        Assert.DoesNotContain(manager.KnownWindows, w => w.Handle == Obs().Handle);

        manager.RepairWindowList();

        Assert.Contains(manager.KnownWindows, w => w.Handle == Obs().Handle);
        Assert.Contains(manager.WindowsByWorkspace().Value.Workspaces.Single().Running,
            row => row.Window.Handle == Obs().Handle);
    }

    // The ghost half. A destroyed handle the manager is still drawing a row for, because the monitor
    // dropped it without the manager hearing.
    [Fact]
    public void A_dead_handle_is_dropped()
    {
        var workspace = GivenAWorkspace();
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Obs(0x1C13F2)));
        desktops.WindowPlacements[Obs(0x1C13F2).Handle] = workspace.DesktopId!.Value;
        Assert.Contains(manager.KnownWindows, w => w.Handle == Obs(0x1C13F2).Handle);

        // The window is destroyed. It is absent from the OS listing AND its handle is dead, which is
        // what tells this apart from the tray case below.
        monitor.Dead.Add(Obs(0x1C13F2).Handle);

        manager.RepairWindowList();

        Assert.DoesNotContain(manager.KnownWindows, w => w.Handle == Obs(0x1C13F2).Handle);
    }

    // The trap this repair must not fall into, and it is the one the monitor's own comment warns
    // about: a window minimised to the tray is absent from the taskbar-candidate list while still
    // existing. Dropping it here would forget its rename ledger entry, so a later re-show would take
    // our own short name for the original title.
    [Fact]
    public void A_window_minimised_to_the_tray_is_not_dropped()
    {
        GivenAWorkspace();
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Beeper()));

        // Gone from the candidate list, still a real window.
        monitor.InitialWindows.Clear();

        manager.RepairWindowList();

        Assert.Contains(manager.KnownWindows, w => w.Handle == Beeper().Handle);
    }

    // A repair runs every five seconds for as long as the app is open, and every pulse rebuilds every
    // surface, which costs a desktop lookup per known window. So a repair that changed nothing has to
    // say nothing.
    [Fact]
    public void A_repair_that_changes_nothing_is_silent()
    {
        GivenAWorkspace();
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Obs()));
        monitor.InitialWindows.Add(Obs());

        var pulses = 0;
        using var subscription = manager.StateChanged.Subscribe(_ => pulses++);

        manager.RepairWindowList();
        manager.RepairWindowList();

        Assert.Equal(0, pulses);
    }

    [Fact]
    public void A_repair_that_adopts_a_window_pulses_once()
    {
        GivenAWorkspace();
        var manager = Started();
        monitor.InitialWindows.Add(Obs());

        var pulses = 0;
        using var subscription = manager.StateChanged.Subscribe(_ => pulses++);

        manager.RepairWindowList();

        Assert.Equal(1, pulses);
    }

    // A repair is not an arrival. The window has been open for hours exactly where its owner left it,
    // so adopting it must not run the placement tiers and move it somewhere -- which is the difference
    // between repairing a list and re-living the window's first moment.
    [Fact]
    public void Adopting_a_window_does_not_move_it()
    {
        var workspace = GivenAWorkspace();
        var elsewhere = new Workspace(Guid.NewGuid(), "Extra", Guid.NewGuid());
        desktops.Desktops.Add(new DesktopInfo(elsewhere.DesktopId!.Value, "Extra"));
        store.Stored = store.Stored with { Workspaces = [workspace, elsewhere] };

        var manager = Started();
        // Extra owns this app: it was placed there once by hand, so memory would put a NEW window of
        // it there. The window being repaired is not a new window.
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Obs()));
        Assert.True(manager.AssignWindow(Obs().Handle, elsewhere.Id).IsSuccess);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Disappeared, Obs()));

        // Now the same app is open again on notary's desktop, and the manager has lost track of it.
        var other = Obs(0xF22EB);
        monitor.InitialWindows.Add(other);
        desktops.WindowPlacements[other.Handle] = workspace.DesktopId!.Value;

        manager.RepairWindowList();

        Assert.Equal(workspace.DesktopId, desktops.WindowPlacements[other.Handle]);
    }
}
