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

    // A window that is alive but no longer LISTED -- hidden, minimised to the tray -- leaves the bar.
    //
    // Petre: "what's the bell icon in the gepha workspace?" It was Outlook's reminder dialog, hidden
    // since he dismissed it, holding a row it could not answer for: no taskbar button, and clicking it
    // said "Window is not on any desktop (closed or pinned)", because Windows reports no desktop for a
    // hidden window. Then: "yes, hide what's hidden."
    //
    // This is the HIDE path, not the gone path, and the difference is the whole point. OnHidden drops
    // the window from the live bookkeeping and keeps the rename ledger, so nothing forgets the original
    // title; reporting it as Disappeared would forget it, and a later re-show would then take our own
    // short name for the original. The monitor's own comment warns about exactly that, and it is why
    // this reconciles with two questions rather than one: IsAlive decides gone, and being listed
    // decides visible.
    [Fact]
    public void A_hidden_window_stops_holding_a_row()
    {
        GivenAWorkspace();
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Beeper()));
        Assert.Contains(manager.KnownWindows, w => w.Handle == Beeper().Handle);

        // Gone from the candidate list, still a real window: closed to tray, or a dialog its app hid.
        monitor.InitialWindows.Clear();

        manager.RepairWindowList();

        Assert.DoesNotContain(manager.KnownWindows, w => w.Handle == Beeper().Handle);
    }

    // ...and the app it belongs to is still remembered, which is what makes the drop safe: the roster
    // says what BELONGS to a workspace rather than what is live, so a window that comes back out of the
    // tray goes back where it was.
    [Fact]
    public void Hiding_a_window_does_not_forget_the_app()
    {
        var workspace = GivenAWorkspace();
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Beeper()));
        Assert.True(manager.AssignWindow(Beeper().Handle, workspace.Id).IsSuccess);

        monitor.InitialWindows.Clear();
        manager.RepairWindowList();

        Assert.Contains(manager.State.Inventory[workspace.Id], entry => entry.ProcessPath == Beeper().ProcessPath);
    }

    // The route the bell came back by, every time. Outlook rewrites that dialog's title as reminders
    // accumulate ("1 Reminder(s)", "2 Reminder(s)"), and a title change on a window we had dropped used
    // to be read as "it became taskbar-worthy late" and re-adopted -- so the row returned within
    // seconds of every sweep that removed it.
    [Fact]
    public void A_title_change_does_not_bring_a_hidden_window_back()
    {
        GivenAWorkspace();
        var manager = Started();
        monitor.InitialWindows.Clear();

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.TitleChanged, Beeper() with { Title = "2 Reminder(s)" }));

        Assert.DoesNotContain(manager.KnownWindows, w => w.Handle == Beeper().Handle);
    }

    // ...while a title change on a window that IS listed still adopts it, which is what that path is
    // for: an app whose window has no title until it has loaded something (a bare editor, a browser
    // starting up) only becomes taskbar-worthy when the title arrives.
    [Fact]
    public void A_title_change_still_adopts_a_listed_window()
    {
        GivenAWorkspace();
        var manager = Started();
        monitor.InitialWindows.Add(Beeper());

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.TitleChanged, Beeper() with { Title = "Beeper | Michelle" }));

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
