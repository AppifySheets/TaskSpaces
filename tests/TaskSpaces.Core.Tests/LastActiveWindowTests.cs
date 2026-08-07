using CSharpFunctionalExtensions;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Overview;

namespace TaskSpaces.Core.Tests;

// Petre: "when switching workspaces, i want you to activate the window which was last active
// last time this workspace was active", and -- for the marker -- "make the last active window
// in that workspace look a bit different... so i know what i'm going to have activated when i
// land on that workspace".
//
// One map drives both, so the restore and the marker cannot disagree; these tests hold both
// ends of that to the same facts.
public class LastActiveWindowTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();
    readonly FakeActivator activator = new();

    static WindowInfo Window(nint handle, string process) =>
        new(new WindowHandle(handle), (int)handle, process, $@"C:\{process}.exe", $"{process} window", $@"""C:\{process}.exe""");

    readonly WindowInfo code = Window(0x1, "Code");
    readonly WindowInfo browser = Window(0x2, "Browser");
    readonly WindowInfo mail = Window(0x3, "Mail");

    Guid work;
    Guid personal;

    // Two desktops: Work holds Code + Browser, Personal holds Mail. We start on Work.
    WorkspaceManager Started()
    {
        work = desktops.Create("Work").Value.Id;
        personal = desktops.Create("Personal").Value.Id;
        desktops.CurrentDesktopId = work;

        monitor.InitialWindows.AddRange([code, browser, mail]);
        desktops.WindowPlacements[code.Handle] = work;
        desktops.WindowPlacements[browser.Handle] = work;
        desktops.WindowPlacements[mail.Handle] = personal;

        var manager = new WorkspaceManager(desktops, monitor, titles, store, activator: activator);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    // FakeDesktops.Switch records the call but raises nothing, exactly like the real service:
    // CurrentChanged comes back from the OS separately. Driven explicitly here so a test can be
    // precise about the order the two arrive in.
    void SwitchTo(Guid desktopId)
    {
        desktops.CurrentDesktopId = desktopId;
        desktops.CurrentChangedSubject.OnNext(desktopId);
    }

    static IReadOnlyList<WindowRow> RowsOn(WorkspaceManager manager, Guid desktopId) =>
        manager.WindowsByWorkspace().Value.OtherDesktops.Single(g => g.DesktopId == desktopId).Windows;

    [Fact]
    public void Landing_on_a_desktop_activates_the_window_that_had_focus_when_we_left_it()
    {
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, browser));

        SwitchTo(personal);
        activator.Activated.Clear(); // whatever Personal did on arrival is not what we assert
        SwitchTo(work);

        Assert.Equal(browser.Handle, Assert.Single(activator.Activated));
    }

    // The stamp happens on the way OUT, so a desktop never visited this session has nothing to
    // restore -- and must not guess. Activating "something" would be worse than leaving focus
    // where Windows put it, because the marker would have promised nothing.
    [Fact]
    public void Landing_on_a_desktop_we_have_never_left_activates_nothing()
    {
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, browser));

        SwitchTo(personal);

        Assert.Empty(activator.Activated);
    }

    // A remembered window that has since been closed or dragged elsewhere must not be dragged
    // back. DesktopOf answers both cases at once -- it fails outright for a dead hwnd.
    [Fact]
    public void A_remembered_window_that_moved_desktops_is_not_restored()
    {
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, browser));
        SwitchTo(personal);
        activator.Activated.Clear();

        desktops.WindowPlacements[browser.Handle] = personal; // dragged away while we were gone
        SwitchTo(work);

        Assert.Empty(activator.Activated);
    }

    // The marker cannot outlive the window's presence on that desktop, and it gets that for
    // free rather than by bookkeeping: a marker is only ever drawn on a row, and a row only
    // exists on the desktop the window is actually on. So a window that leaves takes its marker
    // with it, without anything having to notice.
    [Fact]
    public void A_window_that_left_the_desktop_stops_being_marked_there()
    {
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, browser));
        SwitchTo(personal);
        Assert.True(RowsOn(manager, work).Single(r => r.Window.Handle == browser.Handle).WillActivate);

        desktops.WindowPlacements[browser.Handle] = personal; // dragged to where we now stand

        Assert.DoesNotContain(RowsOn(manager, work), r => r.WillActivate);
    }

    // A pinned window is on every desktop already, so restoring it says nothing and marking it
    // would claim a landing spot that was never in question.
    [Fact]
    public void A_pinned_window_is_never_remembered_as_a_desktops_landing_spot()
    {
        var manager = Started();
        desktops.PinnedWindows.Add(browser.Handle);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, browser));

        SwitchTo(personal);
        activator.Activated.Clear();
        SwitchTo(work);

        Assert.Empty(activator.Activated);
    }

    [Fact]
    public void The_window_we_will_land_on_is_marked_on_the_desktop_we_are_not_on()
    {
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, browser));
        SwitchTo(personal);

        var workRows = RowsOn(manager, work);
        Assert.True(workRows.Single(r => r.Window.Handle == browser.Handle).WillActivate);
        Assert.False(workRows.Single(r => r.Window.Handle == code.Handle).WillActivate);
    }

    // The map is stamped on the way OUT, so while you stand on a desktop its entry still names
    // whatever you were looking at when you last LEFT. Rendering that would put a "you will land
    // here" marker on one icon while a different icon on the same row wears the live IsActive
    // highlight -- two contradictory claims about one row.
    [Fact]
    public void Nothing_is_marked_on_the_desktop_we_are_already_standing_on()
    {
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, browser));
        SwitchTo(personal);
        SwitchTo(work); // back, with browser remembered and now re-activated

        Assert.DoesNotContain(RowsOn(manager, work), r => r.WillActivate);
    }

    // JumpTo switches the desktop and THEN activates its target, so the CurrentChanged that the
    // switch raises could otherwise restore the previous visit's window and race the jump for
    // the same foreground. Claiming the target up front makes the two agree instead of compete.
    [Fact]
    public void Jumping_to_a_window_makes_it_that_desktops_landing_spot_rather_than_racing_it()
    {
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, browser));
        SwitchTo(personal);
        activator.Activated.Clear();

        // Jump back to Work, but at CODE -- not the remembered browser.
        var jump = new FakeActivator();
        Assert.True(manager.JumpTo(code.Handle, jump).IsSuccess);
        // The switch JumpTo just performed comes back from the OS as CurrentChanged. Landing
        // must now restore the jump's own target, not the browser left over from last visit.
        SwitchTo(work);

        Assert.Contains(code.Handle, jump.Activated);
        Assert.Equal(code.Handle, Assert.Single(activator.Activated));
    }
}
