using System.Reactive.Linq;
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
