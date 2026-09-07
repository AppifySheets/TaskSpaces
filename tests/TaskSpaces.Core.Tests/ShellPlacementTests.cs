using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rehydration;
using Xunit.Abstractions;

namespace TaskSpaces.Core.Tests;

// Petre: "run window opens in llc workspace" / "should open in current" / "it just opens in that
// workspace".
//
// The Win+R dialog is explorer's, and explorer's identity is one path with no arguments shared by the
// desktop, the taskbar, every folder window and that dialog. His state.json had learned
// Explorer.EXE -> LLC from a window titled "Run", so placement memory moved each freshly built dialog
// off the desktop it appeared on. The reasoning is written out in RosterIdentity.
//
// Beeper's case is the control in both halves below: it is the app placement memory exists FOR, so
// every test here that denies the shell something is paired with proof that a real app still gets it.
public class ShellPlacementTests(ITestOutputHelper output)
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    const string ExplorerPath = @"C:\WINDOWS\Explorer.EXE";
    const string ExplorerCmd = @"C:\WINDOWS\Explorer.EXE";
    const string BeeperPath = @"C:\Programs\BeeperTexts\Beeper.exe";

    // Titled "Run" and pathed like his file, because that pair is what the entry in it records.
    static WindowInfo RunDialog(nint handle) =>
        new(new WindowHandle(handle), 16708, "explorer", ExplorerPath, "Run", ExplorerCmd);

    static WindowInfo BeeperWindow(nint handle) =>
        new(new WindowHandle(handle), 42, "Beeper", BeeperPath, "Beeper | HRIS", @"""C:\Programs\BeeperTexts\Beeper.exe"" ");

    WorkspaceManager Started(AppState state)
    {
        store.Stored = state;
        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    // --- the identity itself ---------------------------------------------------------

    [Fact]
    public void The_shell_has_no_placement_identity()
    {
        Assert.True(RosterIdentity.Of(RunDialog(0x10)).HasNoValue);
        Assert.True(RosterIdentity.Of(BeeperWindow(0x11)).HasValue); // and a real app still does
    }

    // Case-insensitively and whatever the path's shape, since his file holds "Explorer.EXE" while the
    // window reports the lowercase form.
    [Theory]
    [InlineData(@"C:\WINDOWS\Explorer.EXE", true)]
    [InlineData(@"C:\Windows\explorer.exe", true)]
    [InlineData(@"C:\Programs\BeeperTexts\Beeper.exe", false)]
    // Not a substring test: an app of one's own called explorer-something is not the shell.
    [InlineData(@"C:\Tools\explorer-plus.exe", false)]
    public void IsShell_names_the_shell_and_nothing_else(string path, bool shell) =>
        Assert.Equal(shell, RosterIdentity.IsShell(path));

    // --- the reported defect, end to end ---------------------------------------------

    // A dialog appears on the desktop Windows made it on, with LLC's claim standing in state.
    // Nothing may move it.
    [Fact]
    public void A_remembered_shell_entry_does_not_move_the_run_dialog()
    {
        var llc = new Workspace(Guid.NewGuid(), "LLC", null);
        Started(AppState.Empty with
        {
            Workspaces = [llc],
            Inventory = new Dictionary<Guid, IReadOnlyList<InventoryEntry>>
            {
                [llc.Id] = [new InventoryEntry(ExplorerPath, ExplorerCmd, "Run")],
            },
        });

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, RunDialog(0x41124A)));

        output.WriteLine("placements: " + string.Join(", ", desktops.WindowPlacements.Select(kv => $"{kv.Key.Value:X}->{kv.Value}")));
        Assert.DoesNotContain(new WindowHandle(0x41124A), desktops.WindowPlacements.Keys);
    }

    // The same state, the same workspace, a real app: still placed. This is what stops the fix above
    // from being "memory is off", which would have been the wrong repair for a right complaint.
    [Fact]
    public void A_remembered_app_is_still_placed_from_the_same_state()
    {
        var llc = new Workspace(Guid.NewGuid(), "LLC", null);
        var manager = Started(AppState.Empty with
        {
            Workspaces = [llc],
            Inventory = new Dictionary<Guid, IReadOnlyList<InventoryEntry>>
            {
                [llc.Id] = [new InventoryEntry(ExplorerPath, ExplorerCmd, "Run"),
                            new InventoryEntry(BeeperPath, @"""C:\Programs\BeeperTexts\Beeper.exe"" ", "Beeper")],
            },
        });

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, BeeperWindow(0x20)));

        Assert.Equal(manager.State.Workspaces.Single().DesktopId, desktops.WindowPlacements[new WindowHandle(0x20)]);
    }

    // --- and it cannot be learned again ----------------------------------------------

    // The drag is how his file got the entry in the first place. The window still MOVES -- dropping a
    // folder window on a row is an explicit act and moving it is harmless -- but the shell must not be
    // recorded as living there afterwards.
    [Fact]
    public void Dragging_a_shell_window_onto_a_row_moves_it_without_rostering_the_shell()
    {
        var llc = new Workspace(Guid.NewGuid(), "LLC", null);
        var manager = Started(AppState.Empty with { Workspaces = [llc] });
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, RunDialog(0x30)));

        Assert.True(manager.AssignWindow(new WindowHandle(0x30), llc.Id).IsSuccess);

        Assert.Equal(manager.State.Workspaces.Single().DesktopId, desktops.WindowPlacements[new WindowHandle(0x30)]);
        Assert.Empty(store.Stored.Inventory.SelectMany(kv => kv.Value));
    }

    [Fact]
    public void Dragging_a_real_app_onto_a_row_still_rosters_it()
    {
        var llc = new Workspace(Guid.NewGuid(), "LLC", null);
        var manager = Started(AppState.Empty with { Workspaces = [llc] });
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, BeeperWindow(0x40)));

        Assert.True(manager.AssignWindow(new WindowHandle(0x40), llc.Id).IsSuccess);

        Assert.Contains(store.Stored.Inventory.SelectMany(kv => kv.Value), e => e.ProcessPath == BeeperPath);
    }
}
