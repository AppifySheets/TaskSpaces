using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Tests;

// The manager half of #89: a drop that names a screen. The geometry itself is pinned down in
// MonitorMoveTests; what is decided here is what the operation does to a window, and in what order.
public class MoveToMonitorTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();
    readonly FakeScreenLayout screen = new();

    static readonly MonitorBounds Left = new(-3840, 0, 0, 2160);
    static readonly MonitorBounds Right = new(0, 0, 1920, 1080);

    static WindowInfo Window(nint handle, string process) =>
        new(new WindowHandle(handle), (int)handle, process, $@"C:\{process}.exe", $"{process} window", $@"""C:\{process}.exe""");

    readonly WindowInfo code = Window(0x1, "Code");

    Guid work;
    Guid personal;
    Guid target;
    // The workspace you are standing in, as opposed to `work` which is its desktop.
    Guid home;

    readonly FakeActivator activator = new();

    WorkspaceManager Started(bool withScreen = true)
    {
        var here = new Workspace(Guid.NewGuid(), "Work", Guid.NewGuid());
        var there = new Workspace(Guid.NewGuid(), "Personal", Guid.NewGuid());
        new[] { here, there }.ToList()
            .ForEach(w => desktops.Desktops.Add(new DesktopInfo(w.DesktopId!.Value, w.Name)));
        store.Stored = AppState.Empty with { Workspaces = [here, there] };

        work = here.DesktopId!.Value;
        personal = there.DesktopId!.Value;
        target = there.Id;
        home = here.Id;
        desktops.CurrentDesktopId = work;

        monitor.InitialWindows.Add(code);
        desktops.WindowPlacements[code.Handle] = work;

        screen.Facts = ScreenFacts.Empty with
        {
            MonitorPlacement = new Dictionary<int, MonitorBounds> { [1] = Left, [2] = Right },
        };
        // On the right-hand screen, half its width and height.
        screen.Rects[code.Handle] = new WindowRect(480, 270, 1440, 810);

        var manager = new WorkspaceManager(desktops, monitor, titles, store, activator: activator,
            screenLayout: withScreen ? screen : null);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    [Fact]
    public void A_window_moves_to_the_other_screen()
    {
        var manager = Started();

        Assert.True(manager.MoveWindowToMonitor(code.Handle, 1).IsSuccess);

        var (window, rect) = Assert.Single(screen.Moved);
        Assert.Equal(code.Handle, window);
        // Scaled onto the bigger screen, keeping the same fraction of it (see MonitorMoveTests).
        Assert.Equal(new WindowRect(-2880, 540, -960, 1620), rect);
    }

    // Dropping a window on the half of the row it is already in asks for nothing, and nudging it by a
    // rounding error would be a worse answer than doing nothing.
    [Fact]
    public void Moving_a_window_to_the_screen_it_is_already_on_does_nothing()
    {
        var manager = Started();

        Assert.True(manager.MoveWindowToMonitor(code.Handle, 2).IsSuccess);

        Assert.Empty(screen.Moved);
    }

    [Fact]
    public void Moving_to_a_screen_that_is_not_there_is_refused()
    {
        var manager = Started();

        Assert.True(manager.MoveWindowToMonitor(code.Handle, 7).IsFailure);
        Assert.Empty(screen.Moved);
    }

    // An elevated window cannot be moved by an unelevated process. It is an ordinary outcome, and the
    // bar reports the message rather than pretending the drop worked.
    [Fact]
    public void A_refusal_from_the_OS_is_reported()
    {
        var manager = Started();
        screen.RefuseMoves = true;

        Assert.True(manager.MoveWindowToMonitor(code.Handle, 1).IsFailure);
    }

    [Fact]
    public void A_window_that_has_gone_is_refused()
    {
        var manager = Started();
        screen.Rects.Remove(code.Handle);

        Assert.True(manager.MoveWindowToMonitor(code.Handle, 1).IsFailure);
    }

    // Compatibility mode: no screen layout at all. Nothing offers the gesture there, because the rows
    // draw no monitor marks either, but the operation still has to answer rather than throw.
    [Fact]
    public void With_no_screen_information_the_move_is_refused() =>
        Assert.True(Started(withScreen: false).MoveWindowToMonitor(code.Handle, 1).IsFailure);

    // --- the combined drop ----------------------------------------------------------------------

    // A drop on ANOTHER workspace's half changes the desktop now and the screen later.
    //
    // Petre found the version that tried to do both at once: "i tried dragging an icon from EC to the
    // left monitor... and it didn't work", with his desktop switching underneath him at the same time.
    // A window on another virtual desktop is CLOAKED, and both halves of a screen move fail on one --
    // the geometry write is ignored, and un-maximizing a maximized window to move it brings that window
    // forward, which takes Windows to its desktop. Beeper moved correctly for him throughout, because
    // Beeper was on the desktop he was standing on.
    [Fact]
    public void Assigning_to_another_workspace_moves_the_screen_at_once_but_leaves_the_show_state_alone()
    {
        var manager = Started();

        Assert.True(manager.AssignWindow(code.Handle, target, monitor: 1).IsSuccess);

        Assert.Equal(personal, desktops.WindowPlacements[code.Handle]);
        var (window, rect) = Assert.Single(screen.Moved);
        Assert.Equal(code.Handle, window);
        Assert.Equal(new WindowRect(-2880, 540, -960, 1620), rect);

        // The one thing withheld for a window on another desktop: un-maximizing it in order to move it,
        // which is what makes Windows follow the window to its desktop. Petre saw that as his desktop
        // jumping about while he was doing nothing.
        Assert.False(Assert.Single(screen.ShowStateAllowed));
    }

    // The OS declining is what deferral is FOR, and it is no longer assumed from the window being
    // elsewhere: the attempt is made, and only a move that did not take is queued.
    [Fact]
    public void A_move_the_OS_declines_is_held_and_retried_when_you_arrive()
    {
        var manager = Started();
        screen.RefuseMoves = true;

        Assert.True(manager.AssignWindow(code.Handle, target, monitor: 1).IsFailure);
        Assert.Empty(screen.Moved);

        // It lands on the next attempt, which arriving at that desktop triggers.
        screen.RefuseMoves = false;
        desktops.CurrentDesktopId = personal;
        desktops.CurrentChangedSubject.OnNext(personal);

        var (window, rect) = Assert.Single(screen.Moved);
        Assert.Equal(code.Handle, window);
        Assert.Equal(new WindowRect(-2880, 540, -960, 1620), rect);
        // Reachable this time, so the show state may be handled properly.
        Assert.True(screen.ShowStateAllowed[^1]);
    }

    // The same drop onto the workspace you are ALREADY in does both immediately, since the window is
    // reachable: nothing is held, and this is the case that always worked.
    [Fact]
    public void Assigning_within_the_workspace_you_are_in_moves_the_screen_at_once()
    {
        var manager = Started();
        var here = manager.State.Workspaces.Single(w => w.DesktopId == work).Id;

        Assert.True(manager.AssignWindow(code.Handle, here, monitor: 1).IsSuccess);

        Assert.Single(screen.Moved);
    }

    // A held move must not be applied to a window that is only VISITING: an anchored group pins its
    // parent's windows onto the child's desktop while you stand there, so "is it on this desktop" is
    // briefly true for windows that live elsewhere. Applied after the borrowing settles, so a window
    // whose real home is elsewhere is not moved on someone else's arrival.
    [Fact]
    public void A_held_move_waits_for_the_windows_own_desktop()
    {
        var manager = Started();
        screen.RefuseMoves = true;
        Assert.True(manager.AssignWindow(code.Handle, target, monitor: 1).IsFailure);

        // Arriving somewhere else entirely changes nothing: the window lives on Personal now.
        screen.RefuseMoves = false;
        desktops.CurrentChangedSubject.OnNext(work);

        Assert.Empty(screen.Moved);
    }

    // A move that never takes is given up on rather than retried for the life of the session -- an
    // elevated window this process may never be allowed to move.
    [Fact]
    public void A_move_that_never_takes_is_eventually_abandoned()
    {
        var manager = Started();
        screen.RefuseMoves = true;
        Assert.True(manager.AssignWindow(code.Handle, target, monitor: 1).IsFailure);
        desktops.CurrentDesktopId = personal;

        // Well past the attempt limit.
        Enumerable.Range(0, 40).ToList().ForEach(_ => manager.ApplyPendingMonitorMoves());

        // Stopped trying: a further round does nothing at all, even once the OS would allow it.
        screen.RefuseMoves = false;
        manager.ApplyPendingMonitorMoves();

        Assert.Empty(screen.Moved);
    }

    // No screen named: the workspace-only drop the bar has always done, on a row with no split to aim
    // at.
    [Fact]
    public void Assigning_without_a_screen_leaves_the_window_where_it_is_on_screen()
    {
        var manager = Started();

        Assert.True(manager.AssignWindow(code.Handle, target).IsSuccess);

        Assert.Empty(screen.Moved);
        Assert.Equal(personal, desktops.WindowPlacements[code.Handle]);
    }

    // Petre: "when you move that window, make it foreground, first window." A window you have just sent
    // to another screen is the one you are about to use.
    [Fact]
    public void A_moved_window_comes_to_the_front()
    {
        var manager = Started();

        Assert.True(manager.MoveWindowToMonitor(code.Handle, 1).IsSuccess);

        Assert.Equal([code.Handle], activator.Activated);
    }

    // ...but NOT when the drop sent it to a workspace you are not in. Petre: "i don't understand
    // whether moving a window from one workspace to another takes me to the destination workspace or
    // not; it seems to me that it does sometimes, sometimes it doesn't."
    //
    // It did, and this was the mechanism. Activating a window that lives on another desktop makes
    // Windows follow it, so a cross-workspace drop that ALSO changed the window's screen took him
    // along, while one that did not change the screen left him where he was. Read off his own log:
    // three drops wrote "monitor move done" and every one of them was followed by "arrived <the
    // destination>"; the twenty-six that wrote "monitor move skipped" were followed by nothing.
    //
    // Which meant the deciding factor was invisible: not the row he dropped on, but which HALF of it
    // against the screen the window already happened to be on.
    //
    // #89's request stands where it was made -- "when you move that window, make it foreground" was
    // about moving a window between screens WITHIN the workspace you are in, and it still does that.
    // A window sent somewhere you are not is by definition not the window you are about to use, and
    // this app's standing rule is that it never yanks the desktop.
    //
    // ...with exactly one exception, below: the window you were ACTIVE in, which you cannot still be
    // about to use if you have just sent it away. Note this test's window is not that one.
    [Fact]
    public void A_window_moved_to_another_workspace_does_not_pull_you_after_it()
    {
        var manager = Started();

        Assert.True(manager.AssignWindow(code.Handle, target, monitor: 1).IsSuccess);

        // It went, and it went to the screen the drop named.
        Assert.Equal(personal, desktops.WindowPlacements[code.Handle]);
        Assert.Equal(new WindowRect(-2880, 540, -960, 1620), Assert.Single(screen.Moved).Rect);
        // ...and nothing activated it, so Windows has no reason to follow it.
        Assert.Empty(activator.Activated);
    }

    // The other side of that rule, and the reason it is a guard rather than a deletion: a held move
    // lands when you arrive on the workspace, and by then the window IS where you are, so it takes the
    // foreground exactly as #89 asked.
    [Fact]
    public void A_held_move_still_brings_the_window_forward_when_you_arrive()
    {
        var manager = Started();
        screen.RefuseMoves = true;
        Assert.True(manager.AssignWindow(code.Handle, target, monitor: 1).IsFailure);
        Assert.Empty(activator.Activated);

        screen.RefuseMoves = false;
        desktops.CurrentDesktopId = personal;
        desktops.CurrentChangedSubject.OnNext(personal);

        Assert.Contains(code.Handle, activator.Activated);
    }

    // ...and #107's other half: a MINIMIZED window stays down. The drag said which screen the window
    // belongs on, not that it should come back up, and a gesture that quietly un-minimizes windows while
    // you tidy up is a gesture nobody can use. Its restore rectangle still moves, which is what makes it
    // come back on the new screen when you next open it.
    [Fact]
    public void A_minimized_window_moves_without_being_woken_up()
    {
        var manager = Started();
        screen.Facts = screen.Facts with { Minimized = new HashSet<WindowHandle> { code.Handle } };

        Assert.True(manager.MoveWindowToMonitor(code.Handle, 1).IsSuccess);

        // It moved...
        var (window, rect) = Assert.Single(screen.Moved);
        Assert.Equal(code.Handle, window);
        Assert.Equal(new WindowRect(-2880, 540, -960, 1620), rect);
        // ...and nothing brought it to the front.
        Assert.Empty(activator.Activated);
    }

    // The one window that IS the exception, and the reason the rule above needed narrowing rather
    // than keeping. Petre: "i think moving active windows to a different workspace doesn't always
    // activate that workspace."
    //
    // The standing rule is that the app never yanks the desktop, and it still holds for every window
    // you are not in: tidying three background windows onto another row must not drag you across the
    // machine three times. But the window you are ACTIVE in is the one case where staying put is the
    // surprising answer -- you have just sent away the thing you were looking at, so the desktop you
    // are standing on is now emptier than when you started, and the window you wanted is elsewhere.
    //
    // "Active" is the app's own answer, the same one the bar draws its highlight from, which is what
    // makes this predictable from the screen: follow the highlighted icon and you go with it. Note
    // the bar cannot muddy it by being clicked, because WindowMonitor ignores our own hwnd, so
    // Foreground() reports None for it and MarkActive never clears on None.
    [Fact]
    public void Dropping_the_window_you_are_in_takes_you_with_it()
    {
        monitor.ForegroundWindow = code.Handle;
        var manager = Started();

        Assert.True(manager.AssignWindow(code.Handle, target).IsSuccess);

        Assert.Equal(personal, desktops.WindowPlacements[code.Handle]);
        Assert.Equal([personal], desktops.Switches);
    }

    // ...and the window you followed is the one that has focus when you land. Without this the
    // arrival's own focus restore answers instead, handing you whatever you were last using over
    // there -- so the drop would take you to the right workspace and then put you in the wrong
    // window.
    [Fact]
    public void The_window_you_followed_is_the_one_you_land_in()
    {
        monitor.ForegroundWindow = code.Handle;
        var manager = Started();
        // Somebody else was last active over there, which is what the ledger would otherwise restore.
        var slack = Window(0x2, "slack");
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, slack));
        desktops.WindowPlacements[slack.Handle] = personal;

        Assert.True(manager.AssignWindow(code.Handle, target).IsSuccess);
        desktops.CurrentDesktopId = personal;
        desktops.CurrentChangedSubject.OnNext(personal);

        Assert.Equal(code.Handle, activator.Activated.Last());
    }

    // An idle window is still the ordinary case, and it still leaves you where you are.
    [Fact]
    public void Dropping_a_window_you_are_not_in_leaves_you_where_you_are()
    {
        var manager = Started();

        Assert.True(manager.AssignWindow(code.Handle, target).IsSuccess);

        Assert.Equal(personal, desktops.WindowPlacements[code.Handle]);
        Assert.Empty(desktops.Switches);
    }

    // Dropping the active window back on the row it already lives in asks for nothing, so it must not
    // switch to the desktop you are already standing on: a pointless Switch would leave the follow
    // armed with no arrival to spend it on.
    [Fact]
    public void Dropping_the_active_window_on_its_own_row_switches_nowhere()
    {
        monitor.ForegroundWindow = code.Handle;
        var manager = Started();

        Assert.True(manager.AssignWindow(code.Handle, home).IsSuccess);

        Assert.Empty(desktops.Switches);
    }
}
