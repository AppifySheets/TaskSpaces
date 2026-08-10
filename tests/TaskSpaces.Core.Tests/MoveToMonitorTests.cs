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

    // A drop on another workspace's half does both halves of the job.
    [Fact]
    public void Assigning_with_a_screen_moves_the_window_and_the_desktop()
    {
        var manager = Started();

        Assert.True(manager.AssignWindow(code.Handle, target, monitor: 1).IsSuccess);

        Assert.Single(screen.Moved);
        Assert.Equal(personal, desktops.WindowPlacements[code.Handle]);
    }

    // The screen move has to happen BEFORE the desktop move: moving a window to another virtual
    // desktop cloaks it, and a cloaked window's rectangle is not reliably writable. The order is
    // observable here because FakeDesktops records when the desktop move happened.
    [Fact]
    public void The_screen_move_happens_while_the_window_is_still_visible()
    {
        var manager = Started();
        var desktopMovedFirst = false;
        screen.Moved.Clear();

        // FakeDesktops writes the placement synchronously, so reading it inside the screen move's own
        // recording is enough to establish the order.
        desktops.OnMoveWindow = (_, _) => desktopMovedFirst = screen.Moved.Count == 0;

        Assert.True(manager.AssignWindow(code.Handle, target, monitor: 1).IsSuccess);

        Assert.False(desktopMovedFirst);
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
