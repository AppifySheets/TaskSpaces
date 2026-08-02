using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Tests;

public class OverviewTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();
    readonly FakeActivator activator = new();
    readonly FakeLauncher launcher = new();

    (WorkspaceManager Manager, Workspace Work) Started()
    {
        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return (manager, manager.AddWorkspace("Work").Value);
    }

    static WindowInfo App(nint hwnd, string name = "notepad", string? path = @"C:\notepad.exe", string title = "Notes") =>
        new(new WindowHandle(hwnd), 100, name, path, title, path is null ? null : $"\"{path}\"");

    WindowHandle Appear(WindowInfo w) { monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, w)); return w.Handle; }

    [Fact]
    public void Overview_groups_pinned_workspace_and_other_desktop_windows()
    {
        var (manager, work) = Started();

        var inWork = Appear(App(0x10));
        manager.AssignWindow(inWork, work.Id);
        desktops.WindowPlacements[inWork] = work.DesktopId!.Value;

        var pinnedW = Appear(App(0x11, name: "mstsc", path: @"C:\mstsc.exe", title: "RDP Manager"));
        manager.PinWindow(pinnedW);

        var elsewhere = Appear(App(0x12, name: "paint", path: @"C:\paint.exe", title: "Doodle"));
        var strayDesktop = desktops.Create("Desktop 1").Value;   // an OS desktop no workspace owns
        desktops.WindowPlacements[elsewhere] = strayDesktop.Id;
        desktops.CurrentDesktopId = strayDesktop.Id;             // Petre is currently ON it

        var overview = manager.WindowsByWorkspace().Value;

        Assert.Equal(pinnedW, overview.Pinned.Single().Window.Handle);
        var workGroup = overview.Workspaces.Single(g => g.Workspace.Id == work.Id);
        Assert.Equal(inWork, workGroup.Running.Single().Window.Handle);
        Assert.False(workGroup.IsCurrent);
        var other = overview.OtherDesktops.Single();
        Assert.Equal(("Desktop 1", true), (other.Name, other.IsCurrent)); // named by the desktop, current flagged
        Assert.Equal(elsewhere, other.Windows.Single().Window.Handle);
    }

    [Fact]
    public void Overview_shows_original_title_for_renamed_windows()
    {
        var (manager, work) = Started();
        var h = Appear(App(0x10, title: "myserver - Remote Desktop"));
        manager.AssignWindow(h, work.Id);
        desktops.WindowPlacements[h] = work.DesktopId!.Value;
        manager.RenameWindow(h, "RDP");

        var row = manager.WindowsByWorkspace().Value.Workspaces.Single().Running.Single();
        Assert.Equal("myserver - Remote Desktop", row.OriginalTitle.Value);
    }

    [Fact]
    public void NotRunning_uses_identity_and_running_anywhere_suppresses()
    {
        var (manager, work) = Started();
        manager.AddRosterEntry(work.Id, @"C:\rider\rider64.exe", "X.sln");
        manager.AddRosterEntry(work.Id, @"C:\rider\rider64.exe", "Y.sln");

        // X.sln is running — in NO workspace at all — Y.sln is not.
        Appear(new WindowInfo(new WindowHandle(0x20), 7, "rider64", @"C:\rider\rider64.exe", "X", "\"C:\\rider\\rider64.exe\" X.sln"));

        var notRunning = manager.NotRunningRoster(work.Id);
        Assert.Contains("Y.sln", notRunning.Single().CommandLine);
    }

    [Fact]
    public void StartWorkspace_launches_only_missing_registers_pending_and_switches()
    {
        var (manager, work) = Started();
        manager.AddRosterEntry(work.Id, @"C:\Tools\gitextensions.exe", "browse");
        Appear(App(0x10, name: "devenv", path: @"C:\devenv.exe"));
        manager.AddRosterEntry(work.Id, @"C:\devenv.exe", null); // this identity is bare-devenv...
        // ...but the live window's command line is also bare "C:\devenv.exe" -> running.

        Assert.True(manager.StartWorkspace(work.Id, launcher).IsSuccess);

        Assert.Equal(@"C:\Tools\gitextensions.exe", launcher.Launched.Single().ProcessPath);
        Assert.Equal([work.DesktopId!.Value], desktops.Switches.TakeLast(1).ToArray());

        // The launched app's window arrives -> pending placement routes it to Work,
        // even though no rule matches it.
        Appear(new WindowInfo(new WindowHandle(0x30), 9000, "gitextensions", @"C:\Tools\gitextensions.exe", "GE", "\"C:\\Tools\\gitextensions.exe\" browse"));
        Assert.Equal(work.DesktopId, desktops.WindowPlacements[new WindowHandle(0x30)]);
    }

    [Fact]
    public void JumpTo_switches_to_the_windows_desktop_then_activates()
    {
        var (manager, work) = Started();
        var h = Appear(App(0x10));
        desktops.WindowPlacements[h] = work.DesktopId!.Value;
        desktops.CurrentDesktopId = Guid.NewGuid(); // somewhere else

        Assert.True(manager.JumpTo(h, activator).IsSuccess);

        Assert.Contains(work.DesktopId!.Value, desktops.Switches);
        Assert.Equal([h], activator.Activated);
    }

    [Fact]
    public void JumpTo_pinned_window_activates_without_switching()
    {
        var (manager, _) = Started();
        var h = Appear(App(0x10));
        manager.PinWindow(h);
        var before = desktops.Switches.Count;

        Assert.True(manager.JumpTo(h, activator).IsSuccess);

        Assert.Equal(before, desktops.Switches.Count);
        Assert.Equal([h], activator.Activated);
    }

    [Fact]
    public void Assigning_a_pinned_window_unpins_it_first()
    {
        var (manager, work) = Started();
        var h = Appear(App(0x10));
        manager.PinWindow(h);

        Assert.True(manager.AssignWindow(h, work.Id).IsSuccess);

        Assert.Empty(desktops.PinnedWindows); // "put it in Work" = "not everywhere anymore"
        Assert.Equal(work.DesktopId, desktops.WindowPlacements[h]);
    }

    [Fact]
    public void Appeared_pinned_window_is_not_auto_placed_by_rules()
    {
        var (manager, work) = Started();
        manager.SetRules([new WorkspaceRule(work.Id, RuleMatchKind.ProcessName, "notepad")], []);
        desktops.PinnedWindows.Add(new WindowHandle(0x10)); // pinned before we ever saw it

        Appear(App(0x10));

        Assert.Empty(desktops.WindowPlacements); // pinned = on ALL desktops; rules keep out
    }

    // --- Task 9: hotkey-driven cycling / direct switch ---------------------------

    [Fact]
    public void Cycle_wraps_in_workspace_order()
    {
        var (manager, work1) = Started();
        var work2 = manager.AddWorkspace("Personal").Value;
        desktops.CurrentDesktopId = work1.DesktopId!.Value;

        Assert.True(manager.CycleWorkspace(+1).IsSuccess);
        Assert.Equal(work2.DesktopId!.Value, desktops.Switches.Last());

        desktops.CurrentDesktopId = work2.DesktopId!.Value;
        Assert.True(manager.CycleWorkspace(+1).IsSuccess);
        Assert.Equal(work1.DesktopId!.Value, desktops.Switches.Last()); // wraps back to first
    }

    [Fact]
    public void Cycle_from_non_workspace_desktop_goes_to_first()
    {
        var (manager, work1) = Started();
        var work2 = manager.AddWorkspace("Personal").Value;
        desktops.CurrentDesktopId = Guid.NewGuid(); // some plain OS desktop, not a workspace

        Assert.True(manager.CycleWorkspace(+1).IsSuccess);
        Assert.Equal(work1.DesktopId!.Value, desktops.Switches.Last()); // enters at the first

        desktops.CurrentDesktopId = Guid.NewGuid();
        Assert.True(manager.CycleWorkspace(-1).IsSuccess);
        Assert.Equal(work2.DesktopId!.Value, desktops.Switches.Last()); // enters at the last
    }

    [Fact]
    public void Cycle_with_no_workspaces_fails()
    {
        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);

        Assert.True(manager.CycleWorkspace(+1).IsFailure);
    }

    [Fact]
    public void SwitchToIndex_out_of_range_fails()
    {
        var (manager, work) = Started();

        Assert.True(manager.SwitchToIndex(0).IsSuccess);
        Assert.Equal(work.DesktopId!.Value, desktops.Switches.Last());

        Assert.True(manager.SwitchToIndex(5).IsFailure);
    }
}
