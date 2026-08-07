using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rules;

namespace TaskSpaces.Core.Tests;

// Petre: "why isn't the taskspaces window in the floating window? it's clearly open and i
// can't see it in the floating bar."
//
// It was missing because WindowMonitor hooked with WINEVENT_SKIPOWNPROCESS, which made the
// app structurally blind to its own windows. That flag is gone, so the Manage window now
// flows through the whole pipeline like anyone else's -- which means the pipeline has to
// stop doing three specific things to it. These tests pin all three, plus the part that
// must NOT be exempted: it still has to show up.
public class OwnWindowTests
{
    const int OurPid = 4242;
    const int TheirPid = 99;

    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    static WindowInfo Ours(nint hwnd = 1) =>
        new(new WindowHandle(hwnd), OurPid, "TaskSpaces.App", @"C:\apps\TaskSpaces.App.exe", "TaskSpaces: Manage", null);

    static WindowInfo Theirs(nint hwnd = 2) =>
        new(new WindowHandle(hwnd), TheirPid, "notepad", @"C:\windows\notepad.exe", "TaskSpaces: Manage", null);

    WorkspaceManager Started() =>
        new WorkspaceManager(desktops, monitor, titles, store, ownProcessId: OurPid) is var manager
        && manager.Start().IsSuccess
            ? manager
            : throw new InvalidOperationException("Start failed");

    // Both rules match on the TITLE, and Ours() and Theirs() deliberately share one, so the
    // only difference between the exempt case and the control is which process owns it.
    Workspace GivenAWorkspaceWithARuleMatching(string titlePattern)
    {
        var workspace = new Workspace(Guid.NewGuid(), "Work", Guid.NewGuid());
        desktops.Desktops.Add(new Abstractions.DesktopInfo(workspace.DesktopId!.Value, "Work"));
        store.Stored = AppState.Empty with
        {
            Workspaces = [workspace],
            WorkspaceRules = [new WorkspaceRule(workspace.Id, RuleMatchKind.TitleRegex, titlePattern)],
            RenameRules = [new RenameRule(RuleMatchKind.TitleRegex, titlePattern, "short")],
        };
        return workspace;
    }

    // --- carve-out 1: never move our own window -----------------------------------------

    [Fact]
    public void A_rule_never_moves_our_own_window_to_a_workspace()
    {
        GivenAWorkspaceWithARuleMatching("TaskSpaces");
        var manager = Started();

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Ours()));

        Assert.Empty(desktops.WindowPlacements);
    }

    // The control: the SAME rule, the same title, a different process. Without this the test
    // above could pass because the rule never matched anything at all.
    [Fact]
    public void The_same_rule_still_moves_someone_elses_window()
    {
        var workspace = GivenAWorkspaceWithARuleMatching("TaskSpaces");
        var manager = Started();

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Theirs()));

        Assert.Equal(workspace.DesktopId, desktops.WindowPlacements[Theirs().Handle]);
    }

    // RestorePlacements runs at every startup, so without the exemption our own window would
    // be yanked onto a workspace desktop each launch -- the window Petre is looking at.
    [Fact]
    public void Startup_placement_leaves_our_own_window_where_it_is()
    {
        GivenAWorkspaceWithARuleMatching("TaskSpaces");
        monitor.InitialWindows.Add(Ours());

        Started();

        Assert.Empty(desktops.WindowPlacements);
    }

    // --- carve-out 2: never rename our own window ---------------------------------------

    // WPF owns the Manage window's Title, so our WM_SETTEXT and its own binding would
    // overwrite each other indefinitely.
    [Fact]
    public void A_rename_rule_never_retitles_our_own_window()
    {
        GivenAWorkspaceWithARuleMatching("TaskSpaces");
        var manager = Started();

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Ours()));

        Assert.Empty(titles.Titles);
    }

    // A Failure rather than a silent skip, so the one caller with a human waiting on it --
    // Petre right-clicking the icon in the bar and choosing Rename -- says why instead of
    // appearing to do nothing.
    [Fact]
    public void Renaming_our_own_window_by_hand_is_refused_with_a_reason()
    {
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Ours()));

        var result = manager.RenameWindow(Ours().Handle, "nope");

        Assert.True(result.IsFailure);
        Assert.Contains("own windows", result.Error);
        Assert.Empty(titles.Titles);
    }

    // --- carve-out 3: never remember ourselves as an app that belongs somewhere ----------

    // Otherwise "▶ Start" would try to relaunch TaskSpaces straight into its own
    // single-instance dialog.
    [Fact]
    public void Dragging_our_own_window_onto_a_workspace_moves_it_but_does_not_roster_it()
    {
        var workspace = GivenAWorkspaceWithARuleMatching("nothing-matches");
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Ours()));

        Assert.True(manager.AssignWindow(Ours().Handle, workspace.Id).IsSuccess);

        Assert.Equal(workspace.DesktopId, desktops.WindowPlacements[Ours().Handle]); // the move DID happen
        Assert.Empty(manager.NotRunningRoster(workspace.Id));
        Assert.Empty(store.Stored.Inventory.GetValueOrDefault(workspace.Id, []));
    }

    // Pinning by hand still pins in Windows; it just isn't remembered as a standing
    // instruction to re-pin us at every launch.
    [Fact]
    public void Pinning_our_own_window_is_not_written_into_placement_memory()
    {
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Ours()));

        Assert.True(manager.PinWindow(Ours().Handle).IsSuccess);

        Assert.Contains(Ours().Handle, desktops.PinnedWindows);
        Assert.Empty(store.Stored.PinnedApps);
    }

    // --- and the part that is NOT exempt ------------------------------------------------

    // The whole reason the SKIPOWNPROCESS flag came off. Everything above is about what the
    // app must not do BEHIND Petre's back; being visible and reachable is the feature.
    [Fact]
    public void Our_own_window_is_still_tracked_so_the_bar_can_show_it()
    {
        var manager = Started();

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Ours()));

        Assert.Contains(manager.KnownWindows, w => w.Handle == Ours().Handle);
    }

    [Fact]
    public void Our_own_window_appears_in_the_overview_like_any_other()
    {
        var workspace = new Workspace(Guid.NewGuid(), "Work", Guid.NewGuid());
        desktops.Desktops.Add(new Abstractions.DesktopInfo(workspace.DesktopId!.Value, "Work"));
        desktops.CurrentDesktopId = workspace.DesktopId!.Value;
        store.Stored = AppState.Empty with { Workspaces = [workspace] };
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Ours()));
        // Sitting on the workspace's desktop, as it would be if opened from the tray there.
        desktops.WindowPlacements[Ours().Handle] = workspace.DesktopId!.Value;

        var overview = manager.WindowsByWorkspace().Value;

        Assert.Contains(overview.Workspaces.Single().Running, r => r.Window.Handle == Ours().Handle);
    }
}
