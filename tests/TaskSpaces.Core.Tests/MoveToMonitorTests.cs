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
        desktops.CurrentDesktopId = work;

        monitor.InitialWindows.Add(code);
        desktops.WindowPlacements[code.Handle] = work;

        screen.Facts = ScreenFacts.Empty with
        {
            MonitorPlacement = new Dictionary<int, MonitorBounds> { [1] = Left, [2] = Right },
        };
        // On the right-hand screen, half its width and height.
        screen.Rects[code.Handle] = new WindowRect(480, 270, 1440, 810);

        var manager = new WorkspaceManager(desktops, monitor, titles, store,
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
    public void Assigning_to_another_workspace_holds_the_screen_move_until_you_get_there()
    {
        var manager = Started();

        Assert.True(manager.AssignWindow(code.Handle, target, monitor: 1).IsSuccess);

        // The desktop move happened; the window was not touched on screen.
        Assert.Equal(personal, desktops.WindowPlacements[code.Handle]);
        Assert.Empty(screen.Moved);

        // ...and it lands the moment that desktop is the one you are standing on.
        desktops.CurrentDesktopId = personal;
        desktops.CurrentChangedSubject.OnNext(personal);

        var (window, rect) = Assert.Single(screen.Moved);
        Assert.Equal(code.Handle, window);
        Assert.Equal(new WindowRect(-2880, 540, -960, 1620), rect);
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
        Assert.True(manager.AssignWindow(code.Handle, target, monitor: 1).IsSuccess);

        // Arriving somewhere else entirely changes nothing.
        desktops.CurrentChangedSubject.OnNext(work);

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
}
