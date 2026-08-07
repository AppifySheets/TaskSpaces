using TaskSpaces.Core.Abstractions;
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

        // X.sln is running -- in NO workspace at all -- Y.sln is not.
        Appear(new WindowInfo(new WindowHandle(0x20), 7, "rider64", @"C:\rider\rider64.exe", "X", "\"C:\\rider\\rider64.exe\" X.sln"));

        var notRunning = manager.NotRunningRoster(work.Id);
        Assert.Contains("Y.sln", notRunning.Single().CommandLine);
    }

    // StartWorkspace's test lived here. It went with the launch path itself, which became
    // unreachable once the restore prompt was removed (Petre: "no, bad, don't want this") --
    // Manage's ▶ Start had already gone with the Windows tab. NotRunningRoster, the roster
    // query that test also exercised, is still covered by RosterTests.

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

    // --- Task 10: bug fix -- missing windows in the non-workspace section ---------

    // Suspect (a): a desktop the user never manually renamed reports Name == "" from the
    // OS (Windows' Task View label "Desktop 2" is a shell-UI overlay, not the COM Name
    // property). Confirmed as a real implementation gap (not reproduced live -- Petre's
    // own desktops are all named -- but the original code had no fallback at all). Fixed
    // by falling back to "Desktop {index+1}" using GetDesktops order.
    [Fact]
    public void Unnamed_desktop_falls_back_to_positional_name()
    {
        var (manager, _) = Started();

        // Position 0: a named desktop already claimed by "Work" (from Started()).
        // Position 1: unnamed OS desktop, no workspace owns it.
        var unnamed = new DesktopInfo(Guid.NewGuid(), "");
        desktops.Desktops.Add(unnamed);
        var strayWindow = Appear(App(0x40, name: "mystery", path: @"C:\mystery.exe", title: "???"));
        desktops.WindowPlacements[strayWindow] = unnamed.Id;

        var overview = manager.WindowsByWorkspace().Value;

        var group = overview.OtherDesktops.Single(g => g.DesktopId == unnamed.Id);
        Assert.Equal("Desktop 2", group.Name); // index 1 in GetDesktops order -> "Desktop 2"
    }

    // Suspect (b): CONFIRMED live on Petre's machine -- a real, visible, taskbar-candidate
    // window ("Windows Input Experience") for which DesktopOf failed (VirtualDesktop.
    // FromHwnd returned null) was silently absent from Pinned, every workspace, AND
    // OtherDesktops. WindowsByWorkspace() only ever populates `desktopOf` for successful
    // queries, so a non-pinned window missing from that dictionary IS exactly this case.
    // Fixed with a catch-all "Unplaced" group instead of disappearing outright.
    [Fact]
    public void Window_whose_desktop_cannot_be_resolved_lands_in_unplaced_catchall()
    {
        var (manager, _) = Started();

        // Appear a window but never record a desktop placement for it (and don't pin
        // it) -- FakeDesktops.DesktopOf fails exactly like the real API did for the
        // shell-owned window found in the diagnostic.
        var ghost = Appear(App(0x41, name: "ime", path: @"C:\Windows\ime.exe", title: "Windows Input Experience"));

        var overview = manager.WindowsByWorkspace().Value;

        var unplaced = overview.OtherDesktops.Single(g => g.Name == "Unplaced");
        Assert.Equal(ghost, unplaced.Windows.Single().Window.Handle);
    }

    // Six tests covering CycleWorkspace's wrap arithmetic and SwitchToIndex's range checks
    // used to sit here (Task 9). Both methods went with the Ctrl+Alt+arrows and Ctrl+Alt+1..9
    // chords they existed to serve -- Petre: "i don't think we need ctrl+alt and those,
    // ctrl+tab is good enough" -- so the tests went too rather than being kept alive against
    // dead code.
    //
    // Nothing they asserted is now untested: MRU-order walking replaces cycling and is covered
    // by WorkspaceMruTests, and Switch(workspaceId) -- the one switching path left besides
    // SwitchToDesktop -- is exercised throughout this file and CurrentDesktopPulseTests.
}
