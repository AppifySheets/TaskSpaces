using System.Reactive.Linq;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Tests;

// Petre: "when i press the shortcut, it shows me the previous workspace which was active."
//
// The switch itself was fine; every surface was stale. Switch() changes the desktop and pulses
// nothing, so the bar kept drawing the overview it last built: the old workspace still marked
// current, windows still grouped where they used to be. Nothing in the app consumed
// IVirtualDesktopService.CurrentChanged, the observable declared for exactly this.
public class CurrentDesktopPulseTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    WorkspaceManager Started()
    {
        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    [Fact]
    public void A_desktop_change_pulses_so_every_surface_rebuilds()
    {
        var manager = Started();
        var pulses = 0;
        using var subscription = manager.StateChanged.Subscribe(_ => pulses++);

        desktops.CurrentChangedSubject.OnNext(Guid.NewGuid());

        Assert.Equal(1, pulses);
    }

    // The point of subscribing the observable rather than pulsing inside Switch(): a switch made
    // by ANY means has to refresh the UI, including Win+Ctrl+arrows and Task View, which this
    // app never sees as a call.
    [Fact]
    public void A_switch_made_outside_the_app_pulses_too()
    {
        var manager = Started();
        var pulses = 0;
        using var subscription = manager.StateChanged.Subscribe(_ => pulses++);

        desktops.CurrentChangedSubject.OnNext(Guid.NewGuid());
        desktops.CurrentChangedSubject.OnNext(Guid.NewGuid());

        Assert.Equal(2, pulses);
    }

    // End to end through the real path: switching to a workspace must leave the overview
    // reporting THAT workspace as current, not the one before it.
    [Fact]
    public void After_switching_the_overview_marks_the_new_workspace_current()
    {
        var first = new Workspace(Guid.NewGuid(), "First", Guid.NewGuid());
        var second = new Workspace(Guid.NewGuid(), "Second", Guid.NewGuid());
        desktops.Desktops.Add(new Abstractions.DesktopInfo(first.DesktopId!.Value, "First"));
        desktops.Desktops.Add(new Abstractions.DesktopInfo(second.DesktopId!.Value, "Second"));
        desktops.CurrentDesktopId = first.DesktopId!.Value;
        store.Stored = AppState.Empty with { Workspaces = [first, second] };
        var manager = Started();

        Assert.True(manager.Switch(second.Id).IsSuccess);
        // The fake records switches rather than moving a real desktop, so mirror what the OS
        // would then report -- which is also what raises CurrentChanged in the real service.
        desktops.CurrentDesktopId = second.DesktopId!.Value;
        desktops.CurrentChangedSubject.OnNext(second.DesktopId!.Value);

        var overview = manager.WindowsByWorkspace().Value;
        Assert.True(overview.Workspaces.Single(w => w.Workspace.Id == second.Id).IsCurrent);
        Assert.False(overview.Workspaces.Single(w => w.Workspace.Id == first.Id).IsCurrent);
    }
}
