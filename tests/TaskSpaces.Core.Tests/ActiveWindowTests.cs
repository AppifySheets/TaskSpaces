using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Tests;

// Petre: "active window should be highlighted in the floating window".
public class ActiveWindowTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    static WindowInfo Code(nint handle, string title) =>
        new(new WindowHandle(handle), (int)handle, "Code", @"C:\Code.exe", title, @"""C:\Code.exe""");

    (WorkspaceManager manager, Guid desktopId) StartedWithTwoCodeWindows()
    {
        var desktop = desktops.Create("Main").Value;
        desktops.CurrentDesktopId = desktop.Id;
        var first = Code(0x1, "one - Visual Studio Code");
        var second = Code(0x2, "two - Visual Studio Code");
        monitor.InitialWindows.AddRange([first, second]);
        desktops.WindowPlacements[first.Handle] = desktop.Id;
        desktops.WindowPlacements[second.Handle] = desktop.Id;

        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return (manager, desktop.Id);
    }

    static IReadOnlyList<Overview.WindowRow> RowsOf(WorkspaceManager manager) =>
        manager.WindowsByWorkspace().Value.OtherDesktops.Single().Windows;

    // Foreground events only report CHANGES, so the window that already had focus at launch
    // must be seeded from the OS -- otherwise the highlight is blank until the user switches
    // windows, which is indistinguishable from the feature being broken.
    [Fact]
    public void Startup_seeds_the_already_focused_window_as_active()
    {
        var desktop = desktops.Create("Main").Value;
        desktops.CurrentDesktopId = desktop.Id;
        var focused = Code(0x7, "already focused - Visual Studio Code");
        monitor.InitialWindows.Add(focused);
        desktops.WindowPlacements[focused.Handle] = desktop.Id;
        monitor.ForegroundWindow = focused.Handle;

        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);

        Assert.True(RowsOf(manager).Single().IsActive);
    }

    [Fact]
    public void No_window_is_active_until_one_is_activated() =>
        Assert.DoesNotContain(RowsOf(StartedWithTwoCodeWindows().manager), row => row.IsActive);

    [Fact]
    public void Activating_a_window_marks_exactly_that_row_active()
    {
        var (manager, _) = StartedWithTwoCodeWindows();

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, Code(0x2, "two - Visual Studio Code")));

        var rows = RowsOf(manager);
        Assert.True(rows.Single(r => r.Window.Handle.Value == 0x2).IsActive);
        Assert.False(rows.Single(r => r.Window.Handle.Value == 0x1).IsActive);
    }

    [Fact]
    public void Activating_a_second_window_moves_the_highlight()
    {
        var (manager, _) = StartedWithTwoCodeWindows();

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, Code(0x1, "one - Visual Studio Code")));
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, Code(0x2, "two - Visual Studio Code")));

        Assert.True(RowsOf(manager).Single(r => r.Window.Handle.Value == 0x2).IsActive);
        Assert.False(RowsOf(manager).Single(r => r.Window.Handle.Value == 0x1).IsActive);
    }

    // Activation must be presentational only: focus moving is not a statement about where a
    // window belongs, so nothing may be moved, inventoried or renamed by it.
    [Fact]
    public void Activating_a_window_places_nothing_and_persists_nothing()
    {
        var (_, desktopId) = StartedWithTwoCodeWindows();
        var savesBefore = store.SaveCount;

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, Code(0x1, "one - Visual Studio Code")));

        Assert.Equal(savesBefore, store.SaveCount);
        Assert.Equal(desktopId, desktops.WindowPlacements[new WindowHandle(0x1)]); // not moved
    }

    // Petre: "sometimes, active window is not being updated. i can't afford to think that one
    // window is active and another being highlighted. right now, vscode is active but i am
    // seeing a browser is active in the floating window."
    //
    // activeWindow used to be written ONLY by the Activated event, and EVENT_SYSTEM_FOREGROUND
    // is a WINEVENT_OUTOFCONTEXT delivery -- it can simply be dropped when the message queue is
    // busy (the same lossiness WindowMonitor.Resync was written for). Nothing ever re-derived
    // it, so a SINGLE dropped event stranded the highlight on the previously focused window
    // indefinitely: no later event corrects it, because the window that really has focus is not
    // going to be activated again while it already has focus.
    //
    // Hence the same two-layer shape the rest of this codebase uses: the event is the fast path,
    // and a periodic re-read of the OS is the truth.
    [Fact]
    public void Resync_adopts_the_real_foreground_window_when_the_activation_event_was_lost()
    {
        var (manager, _) = StartedWithTwoCodeWindows();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, Code(0x1, "one - Visual Studio Code")));

        // Focus really moved to the second window, but its EVENT_SYSTEM_FOREGROUND never arrived.
        monitor.ForegroundWindow = new WindowHandle(0x2);
        manager.ResyncActiveWindow();

        Assert.True(RowsOf(manager).Single(r => r.Window.Handle.Value == 0x2).IsActive);
        Assert.False(RowsOf(manager).Single(r => r.Window.Handle.Value == 0x1).IsActive);
    }

    // The sweep must not CLEAR the highlight, only correct it. Foreground() reports None for
    // anything we do not track -- the taskbar, the Start menu, and the floating bar itself
    // (opted out by hwnd via WindowMonitor.Ignore). Those are exactly the things Petre clicks
    // while looking at the bar, so treating None as "nothing is active" would blink the
    // highlight off every tick he touched it. Sticky-on-None matches what the event path
    // already does deliberately (see the EVENT_SYSTEM_FOREGROUND arm in WindowMonitor).
    [Fact]
    public void Resync_keeps_the_highlight_when_focus_is_on_a_window_we_do_not_track()
    {
        var (manager, _) = StartedWithTwoCodeWindows();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, Code(0x1, "one - Visual Studio Code")));

        monitor.ForegroundWindow = Maybe<WindowHandle>.None; // e.g. the bar itself has focus
        manager.ResyncActiveWindow();

        Assert.True(RowsOf(manager).Single(r => r.Window.Handle.Value == 0x1).IsActive);
    }

    // Runs on a timer, so the steady state must be free. Pulsing costs every open surface a
    // rebuild, and a rebuild costs one DesktopOf COM call per known window -- the same reason
    // OnActivated guards on change.
    [Fact]
    public void Resync_does_not_pulse_when_the_highlight_is_already_correct()
    {
        var (manager, _) = StartedWithTwoCodeWindows();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, Code(0x1, "one - Visual Studio Code")));
        monitor.ForegroundWindow = new WindowHandle(0x1);

        var pulses = 0;
        using var subscription = manager.StateChanged.Subscribe(_ => pulses++);
        manager.ResyncActiveWindow();

        Assert.Equal(0, pulses);
    }

    // Petre: "i want to be able to minimize windows from the floating bar." Minimizing fires no
    // window event this app hooks, so without an explicit pulse the icon would not dim until
    // focus happened to land somewhere else -- the action would look like it had done nothing.
    [Fact]
    public void Minimizing_a_window_pulses_so_the_bar_can_redraw_it()
    {
        var (manager, _) = StartedWithTwoCodeWindows();
        var activator = new FakeActivator();
        var pulses = 0;
        using var subscription = manager.StateChanged.Subscribe(_ => pulses++);

        Assert.True(manager.MinimizeWindow(new WindowHandle(0x1), activator).IsSuccess);

        Assert.Equal(new WindowHandle(0x1), Assert.Single(activator.Minimized));
        Assert.Equal(1, pulses);
    }

    // Minimizing is presentational, exactly like activation: where a window BELONGS is not
    // changed by putting it down.
    [Fact]
    public void Minimizing_a_window_places_nothing_and_persists_nothing()
    {
        var (manager, desktopId) = StartedWithTwoCodeWindows();
        var savesBefore = store.SaveCount;

        Assert.True(manager.MinimizeWindow(new WindowHandle(0x1), new FakeActivator()).IsSuccess);

        Assert.Equal(savesBefore, store.SaveCount);
        Assert.Equal(desktopId, desktops.WindowPlacements[new WindowHandle(0x1)]);
    }

    // Alt-tabbing fires foreground events continuously, and every pulse costs one DesktopOf
    // COM call per known window in each open surface. Re-activating the SAME window must
    // therefore be silent.
    [Fact]
    public void Reactivating_the_same_window_does_not_pulse_again()
    {
        var (manager, _) = StartedWithTwoCodeWindows();
        var pulses = 0;
        using var subscription = manager.StateChanged.Subscribe(_ => pulses++);

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, Code(0x1, "one - Visual Studio Code")));
        var afterFirst = pulses;
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, Code(0x1, "one - Visual Studio Code")));

        Assert.Equal(1, afterFirst);
        Assert.Equal(afterFirst, pulses);
    }
}
