using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Tests;

// Petre: "i have teams running on the messenger workspace and it's not there", with a screenshot
// showing Teams in the bar's "Unplaced" catch-all instead of in its workspace row.
//
// MEASURED against his live windows, which is what turned three wrong theories into this one:
//
//   20842 WhatsApp  isWindow=True  visible=False  FromHwnd=NULL        (minimised to tray)
//   313F8 Teams     isWindow=True  visible=True   FromHwnd=Messengers
//   1611EC Edge     isWindow=True  visible=True   cloaked=2  FromHwnd=GEPHA
//
// A window minimised to the tray has NO resolvable desktop. Not closed, not pinned, and not a
// cloaking problem: the Edge window in the control is cloaked, because it sits on another desktop,
// and answers perfectly well. Invisibility is the thing the virtual-desktop API declines to answer
// for, and messengers spend most of the day invisible.
//
// So the window fell out of its row for as long as it was in the tray. These pin the fix: the last
// real answer is remembered and used when the OS declines.
public class HiddenWindowDesktopTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    static readonly Workspace Messengers = new(Guid.NewGuid(), "Messengers", Guid.NewGuid());
    static readonly Workspace Work = new(Guid.NewGuid(), "Work", Guid.NewGuid());

    static readonly WindowInfo Teams =
        new(new WindowHandle(0x313F8), 42, "ms-teams", @"C:\ms-teams.exe", "Chat | Microsoft Teams", null);

    WorkspaceManager Started()
    {
        new[] { Messengers, Work }.ToList()
            .ForEach(w => desktops.Desktops.Add(new DesktopInfo(w.DesktopId!.Value, w.Name)));
        desktops.CurrentDesktopId = Messengers.DesktopId!.Value;
        store.Stored = AppState.Empty with { Workspaces = [Messengers, Work] };

        monitor.InitialWindows.Add(Teams);
        desktops.WindowPlacements[Teams.Handle] = Messengers.DesktopId!.Value;

        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    // Removing the placement is exactly what the OS does when a window is hidden: DesktopOf fails.
    void Hide(WindowHandle window) => desktops.WindowPlacements.Remove(window);

    static IReadOnlyList<string> RowOf(Core.Overview.Overview overview, WindowHandle window) =>
        overview.Workspaces.Where(w => w.Running.Any(r => r.Window.Handle == window)).Select(w => w.Workspace.Name)
            .Concat(overview.OtherDesktops.Where(d => d.Windows.Any(r => r.Window.Handle == window)).Select(d => d.Name))
            .ToList();

    [Fact]
    public void A_window_that_stops_reporting_a_desktop_keeps_the_row_it_had()
    {
        var manager = Started();
        Assert.Equal(["Messengers"], RowOf(manager.WindowsByWorkspace().Value, Teams.Handle));

        Hide(Teams.Handle);

        // Still in Messengers, not in the catch-all: it did not move, it stopped answering.
        Assert.Equal(["Messengers"], RowOf(manager.WindowsByWorkspace().Value, Teams.Handle));
    }

    // ...and it goes back to the OS as the truth the moment the OS has one again, rather than
    // trusting the memory for ever.
    [Fact]
    public void The_OS_wins_again_as_soon_as_it_answers()
    {
        var manager = Started();
        Assert.Equal(["Messengers"], RowOf(manager.WindowsByWorkspace().Value, Teams.Handle));

        Hide(Teams.Handle);
        Assert.Equal(["Messengers"], RowOf(manager.WindowsByWorkspace().Value, Teams.Handle));

        // Shown again, somewhere else: the answer follows the OS, not the memory.
        desktops.WindowPlacements[Teams.Handle] = Work.DesktopId!.Value;
        Assert.Equal(["Work"], RowOf(manager.WindowsByWorkspace().Value, Teams.Handle));
    }

    // Unplaced still means what it says: nobody has ever told us where this window lives. That is the
    // "Windows Input Experience" case the catch-all was written for, and it must not be swallowed.
    [Fact]
    public void A_window_that_never_reported_a_desktop_is_still_unplaced()
    {
        var manager = Started();
        var stranger = new WindowInfo(new WindowHandle(0x777), 7, "textinputhost", @"C:\tih.exe", "Windows Input Experience", null);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, stranger));

        Assert.Equal(["Unplaced"], RowOf(manager.WindowsByWorkspace().Value, stranger.Handle));
    }

    // A remembered desktop that has since been DELETED is worse than the catch-all: the window would
    // sit in a row nothing draws. The catch-all is at least visible and can be dragged out of.
    [Fact]
    public void A_remembered_desktop_that_no_longer_exists_falls_back_to_unplaced()
    {
        var manager = Started();
        Assert.Equal(["Messengers"], RowOf(manager.WindowsByWorkspace().Value, Teams.Handle));

        Hide(Teams.Handle);
        desktops.Desktops.RemoveAll(d => d.Id == Messengers.DesktopId!.Value);

        Assert.Equal(["Unplaced"], RowOf(manager.WindowsByWorkspace().Value, Teams.Handle));
    }

    // The one case the OS cannot correct for us: moving a window that is currently hidden. Without
    // recording the move, the memory would keep naming where it used to live.
    [Fact]
    public void Moving_a_hidden_window_updates_what_is_remembered()
    {
        var manager = Started();
        Hide(Teams.Handle);

        Assert.True(manager.AssignWindow(Teams.Handle, Work.Id).IsSuccess);

        // FakeDesktops.MoveWindow records a placement, so clear it again: the point is that the
        // MEMORY, not the OS, is what answers for a window that is still hidden.
        Hide(Teams.Handle);
        Assert.Equal(["Work"], RowOf(manager.WindowsByWorkspace().Value, Teams.Handle));
    }
}
