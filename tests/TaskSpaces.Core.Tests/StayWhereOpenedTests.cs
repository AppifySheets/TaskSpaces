using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rehydration;
using TaskSpaces.Core.Rules;
using Xunit.Abstractions;

namespace TaskSpaces.Core.Tests;

// Petre: "some of the windows always need to open in the current workspace where they originate, like
// browsers", and "when i say current, i mean the one where they're opened from."
//
// Browsers, terminals and system pickers get the shell's treatment: no identity, so placement memory
// has nothing to move them by and nothing is learned from them. The reasoning, and why each name is on
// the list, is in RosterIdentity.StaysWhereOpened.
//
// Same shape as ShellPlacementTests: every test that denies one of these windows something is paired
// with proof that a real app (Beeper), or an installed web app, still gets it. Otherwise "memory is
// off" would pass every test here, and that would be the wrong repair for a right complaint.
public class StayWhereOpenedTests(ITestOutputHelper output)
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    const string EdgePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
    // The command line his own Edge was started with, as state.json records it.
    const string EdgeCmd = @"""C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"" --profile-directory=Default --restore-last-session";
    // An installed web app running as its own process: same exe, same profile, plus its app id.
    const string PwaCmd = @"""C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"" --profile-directory=Default --app-id=cinhimbnkkaeohfgghhklpknlkffjgod";
    const string BeeperPath = @"C:\Programs\BeeperTexts\Beeper.exe";
    const string BeeperCmd = @"""C:\Programs\BeeperTexts\Beeper.exe"" ";

    static WindowInfo EdgeWindow(nint handle) =>
        new(new WindowHandle(handle), 16788, "msedge", EdgePath, "New tab - Work - Microsoft Edge", EdgeCmd);

    static WindowInfo PwaWindow(nint handle) =>
        new(new WindowHandle(handle), 20992, "msedge", EdgePath, "YouTube Music", PwaCmd);

    static WindowInfo BeeperWindow(nint handle) =>
        new(new WindowHandle(handle), 42, "Beeper", BeeperPath, "Beeper | HRIS", BeeperCmd);

    WorkspaceManager Started(AppState state)
    {
        store.Stored = state;
        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    // A workspace whose roster already claims Edge, the PWA and Beeper: the state his file is in after
    // one drag of each.
    (WorkspaceManager Manager, Workspace Home) WithEverythingRostered()
    {
        var home = new Workspace(Guid.NewGuid(), "TaskSpace", null);
        var manager = Started(AppState.Empty with
        {
            Workspaces = [home],
            Inventory = new Dictionary<Guid, IReadOnlyList<InventoryEntry>>
            {
                [home.Id] =
                [
                    new InventoryEntry(EdgePath, EdgeCmd, "Edge"),
                    new InventoryEntry(EdgePath, PwaCmd, "YouTube Music"),
                    new InventoryEntry(BeeperPath, BeeperCmd, "Beeper"),
                ],
            },
        });
        return (manager, manager.State.Workspaces.Single());
    }

    void Log() =>
        output.WriteLine("placements: " + string.Join(", ", desktops.WindowPlacements.Select(kv => $"{kv.Key.Value:X}->{kv.Value}")));

    // --- the identity itself ---------------------------------------------------------

    [Theory]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")]
    [InlineData(@"C:\Program Files\Mozilla Firefox\firefox.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.23_x64__8wekyb3d8bbwe\WindowsTerminal.exe")]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe")]
    [InlineData(@"C:\WINDOWS\system32\cmd.exe")]
    [InlineData(@"C:\WINDOWS\system32\PickerHost.exe")]
    [InlineData(@"C:\Windows\System32\CredentialUIBroker.exe")]
    [InlineData(@"C:\WINDOWS\system32\Taskmgr.exe")]
    public void These_apps_stay_where_they_were_opened(string path) =>
        Assert.True(RosterIdentity.StaysWhereOpened(path, $"\"{path}\""));

    [Theory]
    [InlineData(@"C:\Programs\BeeperTexts\Beeper.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe")]
    // Not a substring test: a tool of one's own whose name starts like a browser's is not one.
    [InlineData(@"C:\Tools\chrome-devtools-helper.exe")]
    public void Other_apps_do_not(string path) =>
        Assert.False(RosterIdentity.StaysWhereOpened(path, $"\"{path}\""));

    // The one exception inside the list: an installed web app is an app with a home.
    [Fact]
    public void An_installed_web_app_is_not_a_browser_window()
    {
        Assert.False(RosterIdentity.StaysWhereOpened(EdgePath, PwaCmd));
        Assert.True(RosterIdentity.StaysWhereOpened(EdgePath, EdgeCmd));
    }

    [Fact]
    public void A_browser_window_has_no_placement_identity()
    {
        Assert.True(RosterIdentity.Of(EdgeWindow(0x10)).HasNoValue);
        Assert.True(RosterIdentity.Of(PwaWindow(0x11)).HasValue);
        Assert.True(RosterIdentity.Of(BeeperWindow(0x12)).HasValue);
    }

    // --- the reported defect, end to end ---------------------------------------------

    // Edge is in TaskSpace's roster, and a new Edge window opens while no other Edge window is live,
    // which is exactly when memory used to act. It stays on the desktop it was opened on.
    [Fact]
    public void A_rostered_browser_does_not_pull_a_new_window_to_its_workspace()
    {
        WithEverythingRostered();

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, EdgeWindow(0xB1354)));

        Log();
        Assert.DoesNotContain(new WindowHandle(0xB1354), desktops.WindowPlacements.Keys);
    }

    // Same state, same moment: the web app and Beeper still go home.
    [Fact]
    public void A_rostered_web_app_and_a_rostered_app_are_still_placed()
    {
        var (_, home) = WithEverythingRostered();

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, PwaWindow(0x20)));
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, BeeperWindow(0x21)));

        Log();
        Assert.Equal(home.DesktopId, desktops.WindowPlacements[new WindowHandle(0x20)]);
        Assert.Equal(home.DesktopId, desktops.WindowPlacements[new WindowHandle(0x21)]);
    }

    // A workspace rule is an instruction Petre wrote himself, so it still moves a browser window. It
    // does not roster the browser, for the same reason a drag does not.
    [Fact]
    public void A_rule_still_moves_a_browser_window_without_rostering_it()
    {
        var home = new Workspace(Guid.NewGuid(), "TaskSpace", null);
        var manager = Started(AppState.Empty with { Workspaces = [home] });
        Assert.True(manager.SetRules([new WorkspaceRule(home.Id, RuleMatchKind.ProcessName, "msedge")], []).IsSuccess);

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, EdgeWindow(0x70)));

        Assert.Equal(manager.State.Workspaces.Single().DesktopId, desktops.WindowPlacements[new WindowHandle(0x70)]);
        Assert.Empty(store.Stored.Inventory.SelectMany(kv => kv.Value));
    }

    // --- and nothing is learned from them --------------------------------------------

    // A drag still moves the window, since that is an explicit act, but it does not teach the roster
    // that Edge lives on that row.
    [Fact]
    public void Dragging_a_browser_window_onto_a_row_moves_it_without_rostering_the_browser()
    {
        var home = new Workspace(Guid.NewGuid(), "TaskSpace", null);
        var manager = Started(AppState.Empty with { Workspaces = [home] });
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, EdgeWindow(0x30)));

        Assert.True(manager.AssignWindow(new WindowHandle(0x30), home.Id).IsSuccess);

        Assert.Equal(manager.State.Workspaces.Single().DesktopId, desktops.WindowPlacements[new WindowHandle(0x30)]);
        Assert.Empty(store.Stored.Inventory.SelectMany(kv => kv.Value));
    }

    [Fact]
    public void Dragging_a_web_app_onto_a_row_still_rosters_it()
    {
        var home = new Workspace(Guid.NewGuid(), "TaskSpace", null);
        var manager = Started(AppState.Empty with { Workspaces = [home] });
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, PwaWindow(0x40)));

        Assert.True(manager.AssignWindow(new WindowHandle(0x40), home.Id).IsSuccess);

        Assert.Contains(store.Stored.Inventory.SelectMany(kv => kv.Value), e => e.CommandLine == PwaCmd);
    }

    // A hand pin pins that one window in Windows, and is not remembered as an order to pin every Edge
    // window opened after it.
    [Fact]
    public void Pinning_a_browser_window_pins_it_without_remembering_the_browser_as_pinned()
    {
        var manager = Started(AppState.Empty with { Workspaces = [new Workspace(Guid.NewGuid(), "TaskSpace", null)] });
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, EdgeWindow(0x50)));

        Assert.True(manager.PinWindow(new WindowHandle(0x50)).IsSuccess);

        Assert.True(desktops.IsPinned(new WindowHandle(0x50)).GetValueOrDefault(false));
        Assert.Empty(store.Stored.PinnedApps);
    }

    [Fact]
    public void Pinning_a_real_app_is_still_remembered()
    {
        var manager = Started(AppState.Empty with { Workspaces = [new Workspace(Guid.NewGuid(), "TaskSpace", null)] });
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, BeeperWindow(0x60)));

        Assert.True(manager.PinWindow(new WindowHandle(0x60)).IsSuccess);

        Assert.Contains(store.Stored.PinnedApps, e => e.ProcessPath == BeeperPath);
    }
}
