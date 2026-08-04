using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Tests;

// Petre: "YouTube music gets no app icon, it's in personal" -- with the Personal row empty.
//
// The icon was never the problem. A probe run at that moment found Obsidian and YouTube Music
// both sitting on Personal's desktop, and every other row in the bar matched the probe
// exactly, so those two windows had fallen out of the app's live bookkeeping and nothing was
// going to bring them back. WindowMonitor.Resync now reconciles against the OS on the 5s
// sweep, and re-announces a recovered window as Appeared.
//
// These tests cover the MANAGER's half: what has to be true when that re-announcement
// arrives. The monitor's own diffing is exercised by an integration test, since it needs real
// hwnds to enumerate.
public class WindowRecoveryTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    static WindowInfo Obsidian(string title = "26-07-27-Monday - obsidian") =>
        new(new WindowHandle(0x501), 700, "Obsidian", @"C:\apps\Obsidian.exe", title, null);

    Workspace GivenAWorkspace()
    {
        var workspace = new Workspace(Guid.NewGuid(), "Personal", Guid.NewGuid());
        desktops.Desktops.Add(new Abstractions.DesktopInfo(workspace.DesktopId!.Value, "Personal"));
        desktops.CurrentDesktopId = workspace.DesktopId!.Value;
        store.Stored = store.Stored with { Workspaces = [workspace] };
        return workspace;
    }

    WorkspaceManager Started()
    {
        var manager = new WorkspaceManager(desktops, monitor, titles, store, ownProcessId: 4242);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    // The defect exactly as Petre saw it: a window that is really there, on a workspace's
    // desktop, showing in no row at all.
    [Fact]
    public void A_window_lost_to_a_stray_Hidden_event_is_absent_until_it_is_re_announced()
    {
        var workspace = GivenAWorkspace();
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Obsidian()));
        desktops.WindowPlacements[Obsidian().Handle] = workspace.DesktopId!.Value;

        // HIDE does not mean gone -- a tray-minimise fires it, and so does the shell.
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Hidden, Obsidian()));
        Assert.Empty(manager.WindowsByWorkspace().Value.Workspaces.Single().Running);

        // What Resync produces once it notices the window is still listed by the OS.
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Obsidian()));

        Assert.Contains(manager.WindowsByWorkspace().Value.Workspaces.Single().Running,
            row => row.Window.Handle == Obsidian().Handle);
    }

    // The recovery re-runs OnAppeared, which re-evaluates rename rules. RenameLedger.Apply
    // keeps the FIRST original title on re-apply, and this pins that: if recovery captured
    // the current title instead, our own short name would become the thing "Restore title"
    // restores to -- permanently, and invisibly until someone tried it.
    [Fact]
    public void Recovering_a_renamed_window_does_not_overwrite_its_original_title()
    {
        GivenAWorkspace();
        store.Stored = store.Stored with
        {
            RenameRules = [new RenameRule(RuleMatchKind.ProcessName, "Obsidian", "notes")],
        };
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Obsidian()));
        Assert.Equal("notes", titles.Titles[Obsidian().Handle]);

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Hidden, Obsidian()));
        // Re-announced carrying the title it is CURRENTLY wearing, which is our short name --
        // exactly what a fresh WindowInfoFactory.FromHwnd would read off the window.
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Obsidian("notes")));

        Assert.True(manager.RestoreTitle(Obsidian().Handle).IsSuccess);
        Assert.Equal("26-07-27-Monday - obsidian", titles.Titles[Obsidian().Handle]);
    }

    // A genuine close still has to be reported as gone, or Resync's recovery half would
    // resurrect dead windows into the bar.
    [Fact]
    public void A_destroyed_window_stays_gone()
    {
        GivenAWorkspace();
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Obsidian()));

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Disappeared, Obsidian()));

        Assert.DoesNotContain(manager.KnownWindows, w => w.Handle == Obsidian().Handle);
    }
}
